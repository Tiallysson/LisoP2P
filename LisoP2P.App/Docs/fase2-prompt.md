# Fase 2 — Captura de tela e encode

Continuação do P2PChat. As fases 0 e 1 entregaram descoberta, sessão TCP com
handshake/keepalive/reconexão e chat com histórico persistido.

Esta fase é **deliberadamente isolada de rede**. O objetivo é: capturar a tela,
codificar em H.264, e provar que o pipeline funciona ponta a ponta dentro da
própria máquina — gravando em arquivo e/ou exibindo na própria UI. **Nenhum byte
de vídeo trafega pela sessão TCP ainda; isso é fase 3.**

Motivo de separar assim: é a parte de maior risco técnico do projeto inteiro
(APIs Windows pouco documentadas, comportamento variável por GPU). Isolar da rede
significa que quando algo falhar, você sabe que é captura ou encoder, não
protocolo.

---

## 1. Escopo travado

Implemente **só**:
- Captura de um monitor via DXGI Desktop Duplication.
- Encode H.264 por hardware com fallback por software.
- Preview do resultado dentro do próprio app (uma aba/janela nova, não a conversa).
- Gravação opcional em arquivo `.h264` para validar no VLC.

**Não implemente:** envio pela rede, seleção de janela específica (só monitor
inteiro nesta fase), múltiplos monitores simultâneos, áudio do sistema. Tudo isso
vem depois.

---

## 2. P2PChat.Media — captura

Pacote novo: `Vortice.Windows` (bindings de DXGI/Direct3D11 para .NET; evita
P/Invoke manual em cima de COM, que é onde esse tipo de projeto historicamente
perde semanas).

### Interface

```csharp
public interface IScreenCapture : IDisposable
{
    Size FrameSize { get; }
    IReadOnlyList<CaptureAdapterInfo> AvailableMonitors { get; }

    void Start(int monitorIndex);
    void Stop();

    event Action<CapturedFrame> FrameCaptured;
}

public readonly record struct CapturedFrame(
    ID3D11Texture2D Texture,   // superfície GPU, não copiada para CPU
    long TimestampTicks);

public sealed record CaptureAdapterInfo(int Index, string DeviceName, Size Resolution);
```

### Implementação — `DxgiScreenCapture`

Pipeline por frame:

1. `IDXGIOutputDuplication.AcquireNextFrame` com timeout curto (ex. 100ms) — **não
   bloqueie indefinidamente**, isso trava o loop se a tela não mudar.
2. Timeout sem frame novo não é erro: a tela pode estar parada. Apenas repita.
3. Frame adquirido → dispara `FrameCaptured` com a textura.
4. **Chame `ReleaseFrame` assim que possível.** Segurar o frame além do necessário
   trava a duplication API inteira e o próximo `AcquireNextFrame` passa a falhar.
   Se o consumidor (encoder) precisa da textura por mais tempo, copie para uma
   textura própria (`CopyResource`) e libere o frame original imediatamente.

Loop roda em thread dedicada (`Thread` com `IsBackground = true`), não em thread
de pool — é contínuo e de longa duração, não uma tarefa pontual.

### Tratamento de erros específicos do DXGI

Isso não é detalhe cosmético — é a parte que normalmente quebra em produção:

- `DXGI_ERROR_ACCESS_LOST`: acontece quando o usuário troca de sessão RDP, faz
  UAC prompt, ou a GPU reseta. **Recrie a duplication do zero**, não tente
  recuperar o objeto antigo. Log e retry automático com pequeno delay.
- `DXGI_ERROR_WAIT_TIMEOUT`: normal, tratado no item acima, não é falha.
- Modo protegido por DRM (ex. player de vídeo com proteção de conteúdo) faz a
  região aparecer preta na captura. Não é bug seu — documente esse
  comportamento no README, não tente contornar.
- Falha ao criar o device D3D11 (GPU sem suporte, driver quebrado): `IScreenCapture`
  deve reportar isso de forma que a UI mostre mensagem clara, não uma exceção
  crua na tela.

### Enumeração de monitores

`IDXGIFactory1.EnumAdapters` → `IDXGIAdapter.EnumOutputs` → nome e resolução de
cada saída. Popule `AvailableMonitors` no construtor.

---

## 3. P2PChat.Media — encode

### Interface

```csharp
public interface IVideoEncoder : IDisposable
{
    void Configure(int width, int height, int targetBitrateKbps, int fps);
    EncodedFrame Encode(CapturedFrame frame, bool forceKeyframe);
    event Action<EncodedFrame> FrameEncoded;
}

public readonly record struct EncodedFrame(
    byte[] Data,
    bool IsKeyframe,
    long TimestampTicks);
```

### Estratégia de implementação

Tente nesta ordem, com fallback automático se a anterior falhar na inicialização:

1. **Media Foundation hardware** (`Windows.Media.MediaFoundation` via
   `SharpMF`/interop direto): usa NVENC/QuickSync/AMF conforme a GPU disponível.
   Menor CPU, mas API pouco documentada e comportamento varia por driver — é o
   item do cronograma com maior chance de estourar prazo.
2. **OpenH264** (biblioteca da Cisco, com binding gerenciado): software puro,
   funciona em qualquer máquina, CPU mais alta.

`VideoEncoderFactory.Create()` tenta (1), se falhar na configuração ou no
primeiro frame, cai para (2) e loga o motivo. **Não deixe o app inteiro travar
porque hardware encode falhou** — esse fallback é obrigatório, não um "se der
tempo".

### Parâmetros de partida

- Perfil H.264 Baseline ou Main (compatibilidade, sem exigir decoder sofisticado
  do outro lado).
- GOP curto: keyframe a cada 2 segundos (a 30fps, a cada 60 frames). Vídeo de
  tela tolera bem, e facilita recuperação de perda de pacote na fase 3.
- `Encode(frame, forceKeyframe: true)` deve gerar keyframe imediato quando
  chamado — vou precisar disso na fase 3 quando um peer novo entra numa
  transmissão em andamento.
- Bitrate alvo configurável, comece em 3000 kbps a 1080p30 como default.

### Conversão de formato

DXGI entrega BGRA. A maioria dos encoders quer NV12. Implemente a conversão via
shader de compute ou `VideoProcessor` do D3D11 (não em CPU pixel a pixel — isso
sozinho pode estourar o orçamento de frame a 30fps). Se estiver usando Media
Foundation hardware, muitas vezes o `IMFTransform` aceita BGRA diretamente e
converte internamente — verifique antes de implementar a conversão manual.

---

## 4. Pipeline de orquestração

```csharp
public interface ICapturePipeline : IAsyncDisposable
{
    Task StartAsync(int monitorIndex, CaptureSettings settings, CancellationToken ct);
    Task StopAsync();
    event Action<EncodedFrame> FrameReady;
    event Action<CaptureStats> StatsUpdated;   // fps, bitrate real, frames descartados
}
```

Junta `IScreenCapture` + `IVideoEncoder`. Ponto de atenção: captura e encode
correm em velocidades diferentes. Se o encoder está mais lento que a captura,
**descarte frames antigos, nunca enfileire.** Vídeo de tela não pode acumular
atraso — melhor perder frame que ficar cada vez mais defasado. Um único slot
"frame mais recente pendente", sobrescrito se o anterior não foi consumido a
tempo, resolve isso sem fila.

`CaptureStats` existe para a UI mostrar FPS real e frames descartados — isso vai
te dizer se a máquina de teste aguenta o pipeline antes de sequer envolver rede.

---

## 5. UI (P2PChat.App)

Nova aba/janela "Teste de captura", separada da conversa. Não integre com o chat
ainda.

- Dropdown de monitor (populado por `AvailableMonitors`).
- Botão iniciar/parar.
- Preview ao vivo: renderize a textura decodificada de volta via `D3DImage` —
  ou, mais simples nesta fase, decodifique o H.264 de volta e mostre como prova
  de que o encode não corrompeu nada. Não precisa ser o pipeline final de
  render, só validação visual.
- Contadores em tempo real: FPS capturado, FPS codificado, bitrate real, frames
  descartados, qual encoder está ativo (hardware/software) — isso importa para
  você decidir se o fallback está sendo acionado sem perceber.
- Checkbox "gravar em arquivo" → grava os `EncodedFrame.Data` concatenados em
  `.h264` bruto (Annex B) em `%APPDATA%/P2PChat/capture-test.h264`, para abrir
  no VLC e confirmar visualmente.

---

## 6. Testes

Captura e encode em si não são unit-testáveis de forma útil (dependem de GPU
real) — não force teste automatizado onde não cabe. Cubra o que é lógica pura:

- Cálculo de broadcast **não se aplica aqui**, ignore — mas: teste o slot
  "frame mais recente" do pipeline (descarta o antigo, nunca enfileira, sob
  produtor mais rápido que consumidor simulado).
- `VideoEncoderFactory`: dado um encoder "hardware" que falha propositalmente na
  configuração (mock), confirma fallback para software sem exceção subindo.
- Parser/writer do container Annex B usado na gravação: bytes de start code
  (`00 00 00 01`) corretos entre frames.

Validação do pipeline real (captura → encode → decode → visual) é manual, com o
critério de aceite abaixo.

---

## 7. Critério de aceite

1. Iniciar captura mostra preview fluido do próprio monitor em até 1s.
2. Trocar de monitor (se houver mais de um) funciona sem reiniciar o app.
3. Deixar rodando 10 minutos contínuos sem crash, sem vazamento de memória
   visível no Task Manager, sem o contador de "frames descartados" subindo
   descontroladamente em máquina parada.
4. Forçar troca de sessão (Win+L e voltar) não derruba a captura — ela se
   recupera do `ACCESS_LOST` sozinha.
5. Arquivo `.h264` gravado abre e reproduz corretamente no VLC, sem
   artefatos, tela verde/cinza ou travamento de frame.
6. Log mostra claramente qual encoder foi usado (hardware ou fallback) e, se
   caiu para software, o motivo.

Se os 6 itens passam, a fase 2 está pronta — mesmo que a CPU fique alta, isso é
ajuste fino de fase posterior, não bloqueador aqui.

---

## 8. Nota para quando estourar prazo

Este é o marco de decisão do cronograma: se Media Foundation hardware estiver
consumindo tempo desproporcional, **aceite rodar só no fallback OpenH264
temporariamente** e siga para a fase 3. CPU alta em software encode não te
impede de validar o pipeline de rede depois — otimizar o encoder pode esperar.
Documente essa decisão no README se for o caminho tomado.

---

## 9. Entrega

Atualize o `README.md` com: dependências novas (Vortice.Windows, OpenH264),
requisitos de driver de GPU, e a limitação conhecida de captura de conteúdo
protegido por DRM. Commits pequenos: captura isolada, encoder isolado,
pipeline de junção, UI de teste.
