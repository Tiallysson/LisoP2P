# Fase 4 — Áudio

Continuação do P2PChat. As fases anteriores entregaram descoberta (0), sessão TCP
com chat (1), captura/encode de tela validado isoladamente (2), e transmissão de
tela ponta a ponta com fragmentação/remontagem UDP (3).

Esta fase adiciona **voz**: captura de microfone e/ou áudio do sistema, encode
Opus, transporte pelo mesmo canal UDP de mídia já existente, jitter buffer e
playback. Ao final, a chamada tem tela + voz simultâneas.

Escopo travado em 1:1, mesmo par já em sessão. **Não implemente múltiplos
participantes de voz, cancelamento de eco avançado ou mixagem** — isso é fase 5
em diante, se necessário.

---

## 1. Por que áudio reaproveita a infraestrutura da fase 3, não recria

O canal UDP de mídia, o `MediaPacketCodec` com `StreamId`, e o `IMediaReceiver`
já existem. Áudio é só **outro `StreamId`** multiplexado no mesmo socket, não um
socket novo. Motivo: manter dois sockets UDP sincronizados manualmente é mais
complexo que multiplexar um.

Ajuste no `MediaPacketCodec` da fase 3 (deve já ter o campo, apenas passa a ser
usado de fato):

```
byte StreamId    // 0 = vídeo (já em uso), 1 = áudio (novo)
```

`IMediaReceiver` passa a rotear por `StreamId`: pacotes de vídeo continuam no
fluxo de remontagem de frame existente; pacotes de áudio vão para um caminho novo
e mais simples, descrito abaixo.

**Áudio não usa o mesmo mecanismo de remontagem por `FrameId` do vídeo.** Motivo:
um frame Opus cabe inteiro em um pacote UDP (tipicamente 20-100 bytes para voz),
então não há fragmentação a resolver — o pacote de áudio é sempre
`FragmentCount = 1`. O ajuste que essa fase precisa é diferente: ordenação e
absorção de jitter, não remontagem.

---

## 2. Captura (P2PChat.Media)

Pacote: `NAudio` (WASAPI wrapper maduro para .NET, evita interop manual).

```csharp
public interface IAudioCapture : IDisposable
{
    void Start(AudioCaptureMode mode);
    void Stop();
    event Action<AudioFrame> FrameCaptured;
}

public enum AudioCaptureMode { Microphone, SystemLoopback, Both }

public readonly record struct AudioFrame(float[] Samples, long TimestampTicks);
```

- **Microfone**: `WasapiCapture` padrão.
- **Loopback do sistema** (para compartilhar áudio de vídeo/jogo junto da tela):
  `WasapiLoopbackCapture`, captura o que está tocando no alto-falante.
- **Both**: mixagem simples por soma normalizada das duas fontes antes do encode.
  Não precisa ser sofisticado nesta fase — soma e clamp já resolve.

Normalize a taxa de amostragem para **48000 Hz mono** antes do encode,
independente do que o dispositivo entrega nativamente — Opus trabalha bem nessa
taxa e mono é suficiente para voz. Se o dispositivo capturar estéreo ou outra
taxa, resample aqui (NAudio tem `MediaFoundationResampler` ou equivalente).

### Framing de captura

Opus espera blocos de tamanho fixo em amostras: **960 amostras por frame a
48kHz = 20ms**, esse é o padrão da indústria para voz e o que usar por default.
Acumule amostras capturadas até bater 960 e dispare `FrameCaptured` nesse
tamanho exato — não em blocos arbitrários do driver de áudio.

---

## 3. Encode/decode Opus (P2PChat.Media)

Pacote: binding gerenciado de `libopus` (ex. `Concentus`, que é Opus portado
para C# puro — evita trazer DLL nativa extra além do que já existe para vídeo).

```csharp
public interface IAudioEncoder : IDisposable
{
    byte[] Encode(AudioFrame frame);   // um frame de 20ms in, um pacote Opus out
}

public interface IAudioDecoder : IDisposable
{
    float[] Decode(byte[] opusData);
    float[] DecodePacketLoss();        // gera PLC (packet loss concealment) para gap
}
```

Configuração do encoder:
- Modo VoIP (otimizado para voz, não música).
- Bitrate ~24-32 kbps — voz não precisa de mais, e mantém o canal de mídia leve
  ao lado do vídeo que já está consumindo banda.
- Complexidade padrão da biblioteca; não otimize agora.

`DecodePacketLoss()` existe porque Opus tem PLC embutido — ao perceber que um
pacote esperado não chegou, gerar esse "preenchimento" soa muito melhor que
silêncio abrupto ou repetir o frame anterior. Use isso no jitter buffer abaixo
em vez de inventar lógica de conta manualmente.

---

## 4. Jitter buffer (P2PChat.Net)

Este é o núcleo da fase — é o que faz a voz soar contínua apesar de pacotes UDP
chegando em intervalos irregulares.

```csharp
public interface IJitterBuffer
{
    void Push(ushort sequenceNumber, byte[] opusData, long arrivalTicks);
    float[]? Pull();   // chamado pelo motor de playback a cada 20ms, sempre
}
```

Adicione `ushort SequenceNumber` ao cabeçalho de pacote de áudio (separado do
`FrameId` de vídeo — áudio tem sua própria contagem incremental, por
`StreamId`).

Comportamento:

- Buffer interno mantém um pequeno atraso de segurança antes de começar a tocar
  — comece com **60ms (3 frames)** de profundidade alvo. Isso absorve jitter
  normal de rede sem adicionar latência perceptível demais para conversa.
- `Pull()` é chamado pelo motor de playback a cada 20ms **de forma incondicional**
  — o playback não pode parar de puxar só porque a rede está ruim, senão o
  áudio trava de vez em vez de degradar.
- Se o frame esperado (próximo `SequenceNumber` em ordem) está disponível,
  devolve ele.
- Se não está (ainda não chegou, mas pode chegar depois): devolve
  `DecodePacketLoss()` e avança a expectativa mesmo assim. **Não espere** — voz
  atrasada é pior que um gap de PLC.
- Se um pacote chega **atrasado demais** (sequência menor que a já consumida):
  descarte, não pode voltar no tempo do playback.
- Se a profundidade do buffer cresce continuamente (rede entregando mais rápido
  que o consumo, ou pacotes se acumulando), isso é sinal de que o clock do
  emissor e receptor divergem — descarte o excedente além de ~150ms de
  profundidade para não acumular atraso crescente ao longo da chamada.

Esse comportamento (nunca trava, degrada com PLC, corta acúmulo) é o que separa
um jitter buffer que funciona de um que "funciona no teste local e falha na
primeira rede real" — trate como requisito central, não polimento.

---

## 5. Playback (P2PChat.Media)

```csharp
public interface IAudioPlayback : IDisposable
{
    void Start();
    void Stop();
    void SetSource(Func<float[]> pullCallback);   // chamado a cada 20ms
}
```

`WasapiOut` do NAudio como sink, alimentado pelo `IJitterBuffer.Pull` a cada
callback do driver. Volume/mute controláveis pela UI, aplicados como ganho no
buffer de amostras antes de escrever no device — não desligue a captura/encode
ao mutar localmente (isso é decisão de UI, ver seção 7), a lógica de rede
continua rodando.

---

## 6. Orquestração

Estenda `IScreenShareSession` da fase 3 (ou, se fizer mais sentido no código já
existente, crie um `IVoiceSession` paralelo que compartilha a mesma sessão TCP de
controle e o mesmo `IMediaSender`/`IMediaReceiver`, só com `StreamId` diferente).
Decisão de nomenclatura fica com quem implementa — o requisito é que **voz e
tela numa mesma chamada compartilham o socket UDP de mídia e a sessão TCP de
controle**, não abrem conexões paralelas independentes.

Voz pode estar ativa **independente** de compartilhamento de tela — é o caso
comum (chamada de voz sem tela) e precisa funcionar sozinho, sem exigir
`ScreenShareStart` antes.

Adicione ao enum de controle:

```csharp
VoiceStart = 50,   // sem payload, ou payload com modo de captura escolhido
VoiceStop  = 51,
```

---

## 7. UI (P2PChat.App)

Na conversa (mesma área integrada na fase 3):

- Botão de microfone no cabeçalho, ao lado do botão de compartilhar tela.
  Estados: inativo / ativo / mutado.
- **Push-to-talk como modo padrão**, com tecla configurável (default: manter
  `Ctrl` pressionado). Justificativa: evita ruído de fundo constante e é mais
  simples de implementar corretamente nesta fase que detecção de atividade de
  voz (VAD). Adicione um toggle "voz aberta" (sem PTT) como alternativa, mas
  deixe PTT como default.
- Indicador visual de quem está falando no momento (você e o peer), baseado
  simplesmente em: está enviando frames de áudio agora.
- Seletor de dispositivo de entrada/saída nas configurações (a fase 6 vai
  expandir essa tela; aqui só o essencial de áudio).
- Indicador de "áudio do sistema" separado do microfone, caso o usuário queira
  compartilhar som do jogo/vídeo junto com a tela.

Mute local: enquanto mutado, **pare de capturar e codificar**, não apenas de
enviar — economiza CPU e deixa claro no código que nada está sendo processado
daquele áudio.

---

## 8. Testes

Jitter buffer é a peça mais valiosa de testar — é lógica pura, sem hardware de
áudio real:

- `Push`/`Pull` em ordem, sem perda: amostras saem na ordem certa, sem gap.
- Pacote fora de ordem chega antes do esperado: buffer reordena corretamente
  dentro da profundidade configurada.
- Pacote nunca chega: `Pull()` eventualmente retorna PLC em vez de bloquear ou
  lançar.
- Pacote atrasado além do já consumido: descartado silenciosamente, não afeta o
  próximo `Pull()`.
- Profundidade crescendo continuamente (simule produtor mais rápido que
  consumidor): confirme que o buffer se limita a ~150ms e não cresce sem teto.
- `MediaPacketCodec` com `StreamId = 1`: roteamento correto para o caminho de
  áudio, não cai no caminho de remontagem de vídeo por engano.

Captura/encode/playback reais de áudio, como no vídeo, não são unit-testáveis de
forma significativa — validação manual no critério de aceite.

---

## 9. Critério de aceite

Duas instâncias, testadas **tanto em LAN quanto com Radmin VPN ativo**:

1. Segurar a tecla de PTT em A produz áudio audível em B em menos de 300ms de
   latência perceptível.
2. Soltar a tecla para de transmitir (confirme que o encoder para de rodar, não
   só que o áudio some).
3. Ativar voz + compartilhar tela ao mesmo tempo: ambos os fluxos chegam sem um
   degradar o outro visivelmente (vídeo não engasga por causa do áudio
   competindo no mesmo socket).
4. Interromper a rede por 2-3 segundos durante uma fala: ao voltar, o áudio
   retoma em tempo real (não toca o que ficou "acumulado" tentando recuperar
   atraso) — confirma que o corte de profundidade do buffer está funcionando.
5. Alternar "áudio do sistema" ligado enquanto um vídeo toca localmente: o peer
   ouve o som do vídeo junto da voz, se `Both` estiver selecionado.
6. Trocar dispositivo de saída de áudio nas configurações durante uma chamada
   ativa não trava o app.
7. Chamada de voz sozinha (sem compartilhar tela) funciona — não é obrigatório
   ter vídeo ativo para ter áudio.

Se os 7 itens passam, a fase 4 está pronta.

---

## 10. Entrega

Atualize `README.md`: dependências novas (NAudio, Concentus), explicação de
multiplexação por `StreamId` no canal de mídia único, tecla de PTT default e
como reconfigurá-la. Commits pequenos: multiplexação por StreamId, captura,
encode/decode Opus, jitter buffer, playback, orquestração, UI.
