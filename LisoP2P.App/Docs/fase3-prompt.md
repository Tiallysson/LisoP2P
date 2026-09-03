# Fase 3 — Transmissão de tela

Continuação do P2PChat. As fases anteriores entregaram: descoberta (0), sessão
TCP com handshake/keepalive/reconexão e chat (1), e um pipeline de captura +
encode H.264 validado isoladamente, gravando em arquivo local (2).

Esta fase junta as duas metades: **os frames codificados na fase 2 passam a
trafegar por UDP até o peer conectado na sessão da fase 1, e aparecem na tela
dele.** É o marco que prova o projeto — depois desta fase, "compartilhar tela com
alguém" funciona de ponta a ponta.

Escopo travado em **1:1**, um único transmissor por vez. Mesh e múltiplos
espectadores simultâneos são fase 5.

---

## 1. Por que UDP separado da sessão TCP

A sessão TCP da fase 1 continua existindo e carrega chat, handshake, controle
(pedido de keyframe, start/stop de transmissão). **Vídeo não passa por ela.**

Motivo: TCP retransmite pacote perdido e você não quer isso em vídeo ao vivo —
um frame de 3 quadros atrás que finalmente chega depois de retransmissão está
obsoleto, e o head-of-line blocking do TCP atrasa tudo que vem depois dele
esperando essa retransmissão. UDP deixa você decidir a própria política:
descartar o que chegou tarde, pedir keyframe novo em vez de esperar retransmissão.

Então: **um segundo socket UDP**, dedicado a mídia, em porta própria
(`MediaPort` em `NetworkOptions`, default 47102, mesmo padrão de flag de linha de
comando das portas anteriores).

---

## 2. Protocolo de mídia (P2PChat.Core)

Frames H.264 costumam passar de 1200 bytes — o MTU típico de rede é ~1500, então
fragmentação é obrigatória, não opcional.

### Cabeçalho de pacote de mídia

Formato binário fixo, **não MessagePack** — mídia é caminho quente, cada byte de
overhead e cada alocação importa a 30fps. Serialização manual com
`BinaryPrimitives`.

```
byte    Version           (1)
byte    StreamId          (identifica o track — só 1 nesta fase, mas deixe o campo)
uint    FrameId           (incrementa por frame codificado, não por pacote)
ushort  FragmentIndex     (0-based)
ushort  FragmentCount     (total de fragmentos deste frame)
byte    Flags             (bit 0 = IsKeyframe)
ushort  PayloadLength
byte[]  Payload
```

Tamanho de cada pacote UDP: mire `~1200 bytes` de payload por fragmento, deixando
folga para headers de IP/UDP abaixo do MTU comum de 1500. Não assuma jumbo frames.

`MediaPacketCodec` no Core, mesmo padrão de robustez da fase 0/1: decode que
recebe lixo **nunca lança**, retorna `false`. Pacote UDP é a superfície mais
exposta do app — qualquer coisa pode chegar nessa porta.

### Mensagens de controle (na sessão TCP existente)

Adicione ao enum de `MessageType` da fase 1:

```csharp
ScreenShareStart    = 40,   // payload: largura, altura, fps alvo
ScreenShareStop     = 41,
KeyframeRequest     = 42,   // sem payload
```

`KeyframeRequest` viaja por TCP de propósito — é controle, precisa chegar, e é
raro o suficiente para não pesar.

---

## 3. Envio (P2PChat.Net / P2PChat.Media)

### Fragmentação e envio

```csharp
public interface IMediaSender : IDisposable
{
    void SendFrame(EncodedFrame frame, IPEndPoint destination);
}
```

Recebe o `EncodedFrame` que já sai do `ICapturePipeline` da fase 2 (o evento
`FrameReady` de lá alimenta isso diretamente — é o ponto de junção das duas
fases). Fragmenta conforme o cabeçalho acima e escreve cada fragmento no socket
UDP dedicado.

`FrameId` incrementa mesmo se o frame for descartado antes de chegar aqui (não
precisa ser contíguo do lado do receptor, só monotônico do lado do emissor) —
isso ajuda o receptor a perceber lacunas.

### Adaptação simples de taxa

Nesta fase, **não implemente RTCP nem feedback do receptor** — isso é
refinamento de fase posterior. O único controle de taxa aqui é local: se o
`ICapturePipeline` já está descartando frames (contador da fase 2), o sender não
precisa fazer nada além — o descarte upstream já é a válvula de segurança.

---

## 4. Recepção e remontagem (P2PChat.Net)

```csharp
public interface IMediaReceiver : IAsyncDisposable
{
    event Action<DecodableFrame> FrameReassembled;
    event Action FrameDropped;    // frame incompleto, descartado por timeout
    Task StartAsync(int mediaPort, CancellationToken ct);
}

public readonly record struct DecodableFrame(
    byte[] Data, bool IsKeyframe, uint FrameId);
```

### Remontagem

Buffer por `FrameId`: acumula fragmentos até `FragmentCount` bater, então monta o
frame completo na ordem e dispara `FrameReassembled`.

Regras que evitam vazamento de memória e travamento — este é o núcleo da fase,
trate com cuidado:

- **Timeout por frame em remontagem.** Se um frame não completa em ~200ms
  (fragmento perdido), descarte o que tem, dispare `FrameDropped`, libere o
  buffer. Não espere o fragmento que nunca chega.
- **Descarte frames fora de ordem antigos.** Se `FrameId` 105 já foi entregue
  (completo ou descartado por timeout) e chega um fragmento do `FrameId` 103,
  descarte imediatamente — é um retardatário de um frame que já foi decidido.
  Mantenha só uma pequena janela de `FrameId`s em remontagem simultânea (ex. os
  últimos 8), não um dicionário sem limite. **Isso é obrigatório**: sem essa
  janela, um fluxo de pacotes fora de ordem ou um ataque simples de pacotes
  soltos faz o dicionário crescer sem controle.
- Pacote com `FragmentIndex >= FragmentCount` ou `PayloadLength` inconsistente
  com os bytes recebidos → descarta o pacote individual, não derruba a
  remontagem do frame inteiro.

### Recuperação por keyframe

Quando `FrameDropped` dispara (ou ao iniciar recepção de um stream novo — o
primeiro frame que chega pode já estar no meio de um GOP), envie
`KeyframeRequest` pela sessão TCP para o transmissor. Não tente decodificar
frames P/B sem ter recebido o keyframe do GOP — isso produz artefato visual, não
crash, mas é ruim o suficiente para não valer a pena.

Do lado de quem transmite: ao receber `KeyframeRequest`, chame
`Encode(frame, forceKeyframe: true)` no próximo frame disponível (a fase 2 já
implementou esse parâmetro).

---

## 5. Decode e render (P2PChat.Media)

```csharp
public interface IVideoDecoder : IDisposable
{
    void Configure(int width, int height);
    ID3D11Texture2D? Decode(DecodableFrame frame);   // null se não é frame decodificável ainda
}
```

Mesma estratégia de fallback da fase 2: Media Foundation hardware primeiro,
software depois. Reaproveite `VideoEncoderFactory` como modelo —
`VideoDecoderFactory` com a mesma lógica de tentativa e fallback.

Textura decodificada renderiza via `D3DImage` (a fase 0 já justificou WPF por
causa disso). Frame que chega fora de ordem ou corrompido no decode: descarte e
siga para o próximo, não trave o pipeline de render esperando recuperação
perfeita.

---

## 6. Orquestração de sessão de compartilhamento

```csharp
public interface IScreenShareSession : IAsyncDisposable
{
    Task StartSharingAsync(PeerId target, int monitorIndex, CancellationToken ct);
    Task StopSharingAsync();
    Task<IObservable<ID3D11Texture2D>> WatchAsync(PeerId source, CancellationToken ct);
    // ou equivalente com evento, se preferir não trazer Rx como dependência
}
```

Fluxo de quem inicia:
1. Manda `ScreenShareStart` pela sessão TCP com resolução/fps.
2. Sobe `ICapturePipeline` (fase 2) apontando `FrameReady` para o `IMediaSender`.

Fluxo de quem recebe:
1. Recebe `ScreenShareStart`, prepara `IVideoDecoder` com a resolução informada.
2. `IMediaReceiver` já está sempre escutando na porta de mídia (não precisa nascer
   sob demanda) — ao montar o primeiro frame daquele peer, alimenta o decoder.
3. Textura decodificada vai para a UI.

`ScreenShareStop` (explícito ou por queda da sessão TCP) encerra captura de um
lado e limpa o decoder do outro.

---

## 7. UI (P2PChat.App)

Integre com a janela de conversa da fase 1 — a aba de teste isolada da fase 2
pode continuar existindo para diagnóstico, mas o fluxo real do usuário é daqui
pra frente.

- Botão "Compartilhar tela" no cabeçalho da conversa (visível só quando a sessão
  está `Connected`). Ao clicar, se houver mais de um monitor, mostra o mesmo
  dropdown de seleção da fase 2.
- Enquanto compartilhando: indicador visível ("Compartilhando tela") com botão
  para parar.
- Do lado de quem recebe: quando chega `ScreenShareStart`, a área de vídeo
  aparece na conversa (substitua ou divida com a lista de mensagens — escolha um
  layout simples, ex. vídeo em cima, chat compacto embaixo). Some quando para.
- Contador discreto de FPS de exibição e um indicador se está reconstruindo por
  perda (útil pra você depurar, pode ficar atrás de um toggle "modo debug").

---

## 8. Testes

Fragmentação/remontagem é a lógica mais valiosa de cobrir — é pura, sem GPU, e é
onde bug esconde:

- `MediaPacketCodec`: round-trip de encode/decode do cabeçalho.
- Decode rejeita `PayloadLength` que não bate com os bytes restantes, sem lançar.
- Remontagem: fragmentos entregues em ordem → frame completo correto.
- Remontagem: fragmentos entregues **fora de ordem** → mesmo resultado.
- Remontagem: fragmento faltando → `FrameDropped` disparado após o timeout
  simulado (injete o clock, não use `Task.Delay` real no teste).
- Janela de `FrameId`s em voo: simule 20 frames chegando rápido com fragmentos
  intercalados e confirme que o dicionário interno nunca passa do tamanho
  configurado.
- Fragmento com `FragmentIndex >= FragmentCount`: pacote descartado, remontagem
  dos outros fragmentos daquele frame não é afetada.

Captura/encode/decode reais continuam não sendo unit-testáveis de forma
significativa — validação manual no critério de aceite.

---

## 9. Critério de aceite

Duas instâncias, portas de mídia diferentes, testadas **tanto em LAN local
quanto com Radmin VPN ativo** (o comportamento de perda de pacote é diferente
entre os dois, e é exatamente isso que esta fase precisa aguentar):

1. A inicia compartilhamento, B vê a tela de A em até 2 segundos.
2. Mover uma janela na tela de A aparece fluido em B, sem travar em quadro
   parado por mais de um segundo.
3. Minimizar/restaurar a janela do app em B não derruba a exibição.
4. Desconectar o Wi-Fi de B por 5 segundos e reconectar: a imagem se recupera
   sozinha (via keyframe request) sem precisar parar e reiniciar o
   compartilhamento.
5. B entra numa conversa onde A já está compartilhando há um minuto: B recebe
   keyframe e a imagem aparece correta, não um quadro corrompido esperando o
   próximo GOP natural.
6. Parar o compartilhamento em A limpa a tela em B e o indicador some dos dois
   lados.
7. Deixar rodando 10 minutos: memória estável em ambos os lados, sem o
   dicionário de remontagem crescendo (confirme pelo contador de debug).

Se os 7 itens passam — este é o marco do cronograma. A partir daqui o projeto já
prova a proposta central.

---

## 10. Nota sobre buffer de tempo

Esta fase tem o maior risco de estourar prazo do cronograma inteiro, mais ainda
que a fase 2 — aqui você está sincronizando dois pipelines (captura/encode local
e decode/render remoto) através de uma rede não confiável. Se a fase 2 já rodou
com fallback de software, replique o mesmo fallback no decoder desde o início
aqui, não deixe para depurar hardware decode e hardware encode ao mesmo tempo.

Se o prazo estourar bastante, o ponto de corte aceitável é: compartilhamento
funcionando **sem** recuperação automática por keyframe request (item 4/5 do
critério falha, os outros passam) — documente como limitação conhecida e siga
para a fase 4. Recuperação automática pode ser retrabalhada depois; o pipeline
básico funcionando é o que não pode ficar para trás.

---

## 11. Entrega

Atualize `README.md`: porta de mídia nova, explicação de por que vídeo vai por
UDP separado do chat, e a limitação documentada se o corte da seção 10 foi
necessário. Commits pequenos: protocolo de mídia, sender, receiver/remontagem,
decode, orquestração da sessão de compartilhamento, integração de UI.
