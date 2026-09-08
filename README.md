# LisoP2P

App de comunicação P2P para Windows, estilo Discord, que conecta máquinas
diretamente pela LAN (real ou virtual via Radmin VPN), sem servidor central.

Este repositório está na **fase 5 de 6**: solution, protocolo de mensagens,
descoberta de peers na rede, sessão TCP 1:1, chat de texto com histórico local,
captura de tela com encode H.264, transmissão de tela, voz (microfone e/ou som
do sistema, Opus, push-to-talk) e **sala em mesh** — várias sessões simultâneas
com chat, tela e voz para todos os membros. Veja `fase0-prompt.md` a
`fase5-prompt.md` em `LisoP2P.App/Docs/` para as especificações completas de
cada fase.

## Estrutura

- `LisoP2P.Core` — identidade de peer, protocolo de mensagens (MessagePack),
  framing de tamanho-prefixado para o transporte TCP.
- `LisoP2P.Net` — descoberta de peers via broadcast UDP, conector manual,
  sessão TCP 1:1 (`PeerSession`/`SessionManager`) com handshake, keepalive e
  reconexão automática, transporte de mídia por UDP (`UdpMediaSender`,
  `UdpMediaReceiver`, `FrameReassembler`), jitter buffer de áudio
  (`JitterBuffer`), mixagem de várias vozes (`AudioMixer`), a sala e seu gossip
  de membros (`RoomService`, `RoomChatRouter`, `BitrateLadder`) e a orquestração
  de tela e voz (`ScreenShareSession`, `VoiceSession`).
- `LisoP2P.Storage` — histórico de chat em SQLite (`IChatStore`).
- `LisoP2P.Media` — captura de tela via DXGI Desktop Duplication
  (`DxgiScreenCapture`), conversão BGRA→NV12 na GPU (`TextureConverter`,
  D3D11 VideoProcessor), encode H.264 via Media Foundation
  (`MediaFoundationH264Encoder`, `VideoEncoderFactory`), decode H.264 de volta
  para BGRA (`MediaFoundationH264Decoder`, `VideoDecoderFactory`,
  `Nv12Converter`), o pipeline que junta captura e encode (`CapturePipeline`) e
  o áudio: captura WASAPI (`WasapiAudioCapture`), playback (`WasapiAudioPlayback`),
  codec Opus (`OpusAudioEncoder`/`OpusAudioDecoder`) e normalização de amostras
  (`AudioResampler`, `AudioFrameAccumulator`).
- `LisoP2P.App` — interface WPF (MVVM).
- `LisoP2P.Tests` — testes de unidade (xUnit).

## Nome de usuário

O campo **Seu nome**, no topo da coluna da esquerda, define o nome que os
outros peers veem. Enter ou o botão "Salvar" grava o novo nome em
`identity.json`, junto do `PeerId`.

O `PeerId` **não muda** com o rename: é o mesmo `Guid` aleatório de sempre, e
continua sendo a única chave de identidade usada por descoberta, sessões e
histórico de chat. Trocar o nome não reabre sessão, não duplica peer na lista
e não afeta as conversas salvas.

A troca chega aos outros peers por dois caminhos, ambos imediatos:

- `DiscoveryService` dispara um announce assim que o nome muda, em vez de
  esperar o próximo tick do timer, então a lista de peers do outro lado é
  atualizada em menos de um segundo;
- `SessionManager` envia um `NicknameUpdate` (tipo 14) por cada sessão TCP
  aberta, o que atualiza o cabeçalho da conversa em andamento sem derrubar a
  sessão.

Regras de validação (`NicknameRules`, em `LisoP2P.Core`): no máximo 24
caracteres, sem caracteres de controle, sem zero-width nem BOM, espaços
colapsados e aparados. Nome vazio é rejeitado na UI. **Nome que chega pela
rede também passa por essa sanitização** — é texto arbitrário vindo de um
socket aberto, e um peer que anuncie nome vazio ou só com lixo é exibido pelo
fallback `user-<4 primeiros hex do id>`.

## Chat 1:1

Clicar num peer da lista da esquerda abre a conversa: o histórico salvo é
carregado do SQLite imediatamente (mesmo com o peer offline) e, em seguida, o
app conecta (ou reaproveita) a sessão TCP com aquele peer. O cabeçalho da
conversa mostra o estado da sessão e o RTT (`Conectado · 12 ms` /
`Reconectando…` / `Desconectado`).

Cada mensagem enviada é gravada no SQLite antes de ir para a rede (indicador
"pendente"), e passa a "entregue" quando o `ChatAck` do outro lado chega. Se a
sessão cair no meio do caminho, a mensagem fica pendente e é reenviada
automaticamente assim que a sessão volta a `Connected`.

O banco fica em `%APPDATA%/LisoP2P/chat.db` (ou
`%APPDATA%/LisoP2P/<session-port>/chat.db` quando `--session-port` é diferente
do padrão — mesma regra de isolamento usada para `identity.json`, necessária
para rodar duas instâncias na mesma máquina sem elas colidirem).

## Captura de tela (fase 2)

O botão **Teste de captura**, no canto inferior esquerdo da janela principal,
abre uma janela separada do chat com: seletor de monitor, iniciar/parar,
"forçar keyframe", "abrir log", "abrir gravação", preview ao vivo, contadores
em tempo real (fps capturado, fps codificado, bitrate real, frames
descartados, encoder ativo) e os controles de encode: resolução (720p, 1080p
ou nativa), fps (30, 45 ou 60), bitrate em kbps, qualidade do preview,
forçar encoder por software e gravar o resultado em arquivo.

A resolução escolhida é o alvo de **altura**; a largura é derivada dela
mantendo o aspecto do monitor e alinhada a par (1080p em um monitor 1920x1080
vira 1920x1080; em um 2560x1440 vira 1920x1080). "Nativa" desliga o
redimensionamento. O downscale acontece no mesmo `VideoProcessorBlt` que já
fazia a conversão BGRA→NV12, então não custa passagem extra pela GPU. O
bitrate sugerido acompanha resolução e fps (2500 kbps em 720p30, 5000 em
1080p30, 10000 em 1080p60) e pode ser editado à mão.

Com "Gravar em arquivo" marcado, os frames codificados são muxados em
**MP4** (`%APPDATA%/LisoP2P/capture-test.mp4`) pelo `IMFSinkWriter`, em
passagem direta: o H.264 já codificado entra no sink sem re-encode. O arquivo
abre em qualquer player. Nenhum byte de vídeo trafega pela sessão TCP nesta
fase.

Medido nesta máquina (RTX 5060, monitor 1920x1080, tela com animação
contínua): 1080p60 sustentando `captura=60 encode=60` com zero descarte, com
o preview de 1280 px a 30 fps ligado ao mesmo tempo.

Pontos de projeto que importam ao mexer aqui:

- A duplication API é liberada (`ReleaseFrame`) imediatamente após copiar a
  textura do frame; segurar o frame trava a API inteira e faz o
  `AcquireNextFrame` seguinte falhar.
- `DXGI_ERROR_WAIT_TIMEOUT` é normal (tela parada), não é falha.
  `DXGI_ERROR_ACCESS_LOST` (Win+L, prompt de UAC, troca de sessão RDP, reset
  de GPU) faz a duplication ser recriada do zero, com retry automático.
- Captura e encode rodam em threads separadas ligadas por um único slot de
  "frame mais recente" (`LatestFrameSlot`): se o encoder atrasa, o frame
  antigo é **descartado**, nunca enfileirado — vídeo de tela não pode acumular
  atraso.
- A conversão BGRA→NV12 (e o downscale para a resolução alvo) é feita no D3D11
  VideoProcessor (GPU), não pixel a pixel na CPU.
- `TextureConverter` mantém **duas** texturas de staging e faz ping-pong entre
  elas: o frame N é copiado para uma enquanto o `Map(Read)` lê a outra, com o
  frame N-1. Ler a mesma textura que acabou de receber o `CopyResource` força
  uma sincronização GPU→CPU e trava a thread de captura inteira — era um dos
  gargalos de fps. O preço é um frame de latência, e o timestamp devolvido por
  `TryConvert` é o do frame que saiu, não o que entrou.
- O gravador MP4 é criado no **primeiro keyframe**, não no start: o `avcC` do
  MP4 é montado a partir do `MF_MT_MPEG_SEQUENCE_HEADER` do tipo de saída
  negociado, que o MFT só preenche depois de produzir a primeira saída.
- A captura só entrega frame quando a tela muda — é a Desktop Duplication API
  funcionando como projetada. Tela parada mede fps baixo e isso não é defeito;
  para medir throughput de verdade é preciso conteúdo em movimento.

### Log em arquivo

Tudo que a janela de teste mostra no painel de log também vai para
`%APPDATA%/LisoP2P/logs/media.log` (ou
`%APPDATA%/LisoP2P/<session-port>/logs/media.log` quando `--session-port` é
diferente do padrão). O botão **Abrir log** abre a pasta com o arquivo já
selecionado.

Cada início de captura grava um bloco de diagnóstico antes de qualquer coisa:
versão do Windows e do runtime, todos os adaptadores de vídeo (nome, vendor
id, device id, VRAM, monitores ligados) e todos os MFTs H.264 registrados na
máquina com as flags `async` e `d3d11aware`. Falhas de encoder e de captura
vão com a exceção completa (HRESULT e stack), não só a mensagem. É esse
arquivo que deve ser anexado ao relatar um problema de encoder.

Exemplo real (RTX 5060):

```
INFO adapter[0] NVIDIA GeForce RTX 5060 vendor=0x10DE device=0x2D05 vram=7896MB saidas=[\\.\DISPLAY2 1920x1080, \\.\DISPLAY1 1920x1080]
INFO mft[hardware] NVIDIA H.264 Encoder MFT async=1 d3d11aware=-
INFO mft[software-sync] H264 Encoder MFT async=- d3d11aware=-
INFO Tentando encoder NVIDIA H.264 Encoder MFT (hardware=True) em 1920x1080@30 3000kbps.
INFO Encoder ativo: NVIDIA H.264 Encoder MFT (hardware).
```

O arquivo é rotacionado para `media.log.1` quando passa de 5 MB.

### Encoder: hardware (NVENC/QuickSync/AMF) x software

`VideoEncoderFactory` tenta os MFTs de hardware primeiro e cai para o encoder
H.264 por software do Windows se o hardware não configurar. O encoder em uso
aparece na janela de teste e no log. A checkbox **Forçar software** ignora o
hardware de propósito — útil para comparar ou contornar driver problemático.

Os MFTs de hardware (NVENC, por exemplo) são MFTs **assíncronos** e exigem um
protocolo diferente do síncrono; `MediaFoundationH264Encoder` implementa os
dois:

- destrava o MFT (`MF_TRANSFORM_ASYNC_UNLOCK`) antes de qualquer uso;
- consome `IMFMediaEventGenerator` e responde a `METransformNeedInput` /
  `METransformHaveOutput` em uma **thread própria de bombeamento**
  (`LisoP2P.EncoderPump`).

Dois detalhes que travam quem implementa isso pela primeira vez:

Cada `METransformNeedInput` é um **crédito de entrada**. Se o evento chega
quando não há frame para enviar, o crédito precisa ser guardado e usado no
próximo frame — descartar o evento faz o encoder parar de pedir entrada e o
pipeline morre depois do primeiro frame.

E o `Encode` de um MFT assíncrono **não pode esperar a saída do frame que
acabou de entregar**. Enfileirar o frame e bloquear até o `METransformHaveOutput`
transforma o encoder assíncrono em síncrono e joga a latência do NVENC (10 a
107 ms medidos) dentro do tempo de frame: 8 fps de saída para 21 fps de
entrada. Por isso `Encode` só empilha o sample (fila de no máximo 2, o mais
antigo é descartado) e retorna; as saídas chegam pelo evento `FrameEncoded`,
que é quem alimenta gravação, estatísticas e `FrameReady`.

**Não anexe o `IMFDXGIDeviceManager` com o device D3D11 da captura** enquanto
os frames forem entregues como NV12 em memória de sistema. O device da captura
tem `SetMultithreadProtected(true)` e está ocupado com `VideoProcessorBlt`,
`CopyResource` e `Map` a cada frame; compartilhá-lo com o MFT serializa o
NVENC contra a thread de captura. Medido: 8 fps de encode com o manager
anexado, 60 fps sem ele, mesmo hardware e mesma cena. O manager só passa a
valer a pena junto com a entrega da textura direto ao MFT.

Keyframe forçado (`Encode(frame, forceKeyframe: true)`) usa
`ICodecAPI::SetValue(CODECAPI_AVEncVideoForceKeyFrame, 1)` — o NVENC **ignora**
o atributo `MFSampleExtension_VideoEncodePictureType`, que é o que o encoder
por software respeita. Os dois caminhos são acionados, e o `ICodecAPI` só é
usado quando `IsSupported` confirma. Medido nesta máquina: pedido de keyframe
atendido no frame seguinte (~50 ms depois).

Desvios em relação à `fase2-prompt.md`:

- O fallback por software é o encoder do próprio Windows via Media Foundation,
  não o OpenH264 da Cisco — ele já vem no sistema, evita distribuir uma DLL
  nativa e a árvore de fallback continua a mesma.
- O preview mostra a textura capturada (BGRA reduzida na GPU), não o H.264
  decodificado de volta. A prova de que o encode não corrompeu nada é o
  arquivo `.mp4` tocando no player (critério 5 do aceite).
- Os frames entram no encoder como NV12 em memória de sistema (a conversão é
  na GPU, mas há um readback). O caminho sem cópia — entregar a textura NV12
  direto ao MFT via `MFCreateDXGISurfaceBuffer` — é otimização de fase
  posterior.

### Requisitos e limitações

- GPU com suporte a Direct3D 11 e driver funcional; sem isso a janela de teste
  mostra o motivo da falha em vez de quebrar o app.
- Conteúdo protegido por DRM (players com proteção de conteúdo) aparece
  **preto** na captura. É comportamento da própria Desktop Duplication API,
  não um bug do app, e não há como contornar.
- A captura é de um monitor inteiro. Janela específica e múltiplos monitores
  simultâneos não fazem parte desta fase.

## Transmissão de tela (fase 3)

Com a conversa conectada, o botão **Compartilhar tela** no cabeçalho começa a
transmitir para aquele peer; se a máquina tiver mais de um monitor, o seletor
ao lado escolhe qual. Do outro lado a área de vídeo aparece sozinha acima das
mensagens assim que o primeiro keyframe chega, e some quando a transmissão
para (ou quando a sessão TCP cai). A caixa **Debug** liga o contador de fps de
exibição, frames perdidos, frames em remontagem e keyframes pedidos.

### Por que o vídeo não vai pela sessão TCP

A sessão TCP da fase 1 continua carregando chat, handshake e o **controle** do
compartilhamento (`ScreenShareStart` = 40, `ScreenShareStop` = 41,
`KeyframeRequest` = 42). O vídeo em si vai por um **socket UDP separado**, na
porta 47102.

TCP retransmite pacote perdido e bloqueia a fila enquanto espera a
retransmissão (head-of-line blocking). Num vídeo ao vivo, um frame de três
quadros atrás que finalmente chega já é inútil — e enquanto ele não chega,
nada depois dele é entregue. Com UDP a política é nossa: descartar o que
atrasou e pedir um keyframe novo em vez de esperar. O `KeyframeRequest` vai
por TCP de propósito: é controle, precisa chegar, e é raro.

### Fragmentação e remontagem

Um frame H.264 passa fácil de 1200 bytes, então cada frame é fragmentado em
pacotes UDP com um cabeçalho binário fixo de 13 bytes
(`MediaPacketCodec`, no Core) — versão, stream id, `FrameId`, índice e total
de fragmentos, flags (bit 0 = keyframe) e tamanho do payload. É serialização
manual com `BinaryPrimitives`, não MessagePack: a 30 fps cada byte de overhead
e cada alocação no caminho quente importam. O payload é de no máximo 1200
bytes para caber no MTU típico de 1500 sem contar com jumbo frames.

O `FrameReassembler` é a parte que precisa de cuidado, porque é onde
vazamento de memória e travamento se escondem:

- no máximo **8 frames em remontagem simultânea**; ao encher, o `FrameId` mais
  antigo é descartado. Sem essa janela, pacotes fora de ordem (ou soltos de
  propósito por alguém) fazem o dicionário crescer sem limite;
- frame incompleto por **200 ms** é descartado e o buffer liberado — não se
  espera o fragmento que nunca vem;
- fragmento de um `FrameId` que já foi entregue (ou já expirou) é descartado
  na chegada, é retardatário de um frame já decidido;
- pacote com índice fora do total, tamanho inconsistente ou versão
  desconhecida é descartado sozinho, sem derrubar a remontagem do frame. O
  decode do cabeçalho nunca lança: a porta de mídia é a superfície mais
  exposta do app.

O relógio do remontador é injetado, então o timeout é testado sem
`Task.Delay` real.

### Recuperação por keyframe

Quem recebe pede `KeyframeRequest` pela sessão TCP quando um frame é
descartado e quando começa a receber um stream novo, e **não decodifica frame
P antes do primeiro keyframe** — decodificar no meio de um GOP produz imagem
corrompida. O pedido é debounced em 500 ms, senão uma rajada de perda vira uma
rajada de IDR. Quem transmite responde com `Encode(frame, forceKeyframe:
true)` no próximo frame.

Quem entra numa conversa onde o outro lado já está compartilhando recebe o
`ScreenShareStart` de novo assim que a sessão abre, junto de um keyframe — sem
isso a imagem ficaria corrompida até o próximo GOP natural.

### Decode e exibição

`VideoDecoderFactory` tenta os MFTs de **software primeiro** e o hardware
depois — o inverso do encoder, de propósito: a fase 2 já depende do encode por
hardware, e depurar encode e decode de hardware ao mesmo tempo é exatamente o
que a fase 3 recomenda evitar. Decodificar um único stream 1:1 em software é
barato. MFTs de decode assíncronos são recusados no `Configure`, e a fábrica
cai para o próximo candidato.

O decoder devolve BGRA em memória de sistema, exibido pelo mesmo
`WriteableBitmap` que a janela de teste da fase 2 já usava — não o `D3DImage`
que a especificação sugeria. É um desvio consciente: `D3DImage` exige interop
com D3D9Ex e não era necessário para o pipeline funcionar ponta a ponta.

Detalhe que estraga a imagem se ignorado: H.264 codifica em macroblocos de 16
pixels, então uma imagem de 1080 linhas sai do decoder numa superfície de
**1088**. O tamanho codificado é o que determina o offset do plano de croma; a
área visível continua sendo a anunciada pelo transmissor, e as linhas de
padding são cortadas. Usar a altura visível no lugar da codificada desloca as
cores e a imagem sai esverdeada.

### Limitações conhecidas

- Um transmissor por vez, 1:1. Mesh e vários espectadores simultâneos são
  fase 5.
- Não há RTCP nem feedback de taxa do receptor. O único controle de taxa é o
  descarte local do `CapturePipeline`, herdado da fase 2.
- Sem áudio.

## Voz (fase 4)

Com a conversa conectada, o botão de microfone no cabeçalho abre a voz com
aquele peer. **Voz não depende de compartilhamento de tela** — uma chamada só
de áudio funciona sozinha, e o contrário também: dá para compartilhar a tela
sem abrir a voz.

### Push-to-talk

O padrão é **push-to-talk**: segurar a tecla escolhida transmite, soltar
para. A tecla é selecionável na barra de áudio (Ctrl — o padrão —, Alt, Shift
ou Espaço). Com "Espaço" escolhido, a tecla é ignorada enquanto o cursor está
na caixa de mensagem, senão digitar transmitiria. Perder o foco da janela solta
o PTT, para a tecla não ficar "presa" apertada.

Segurar a tecla **liga o dispositivo de captura**; soltar desliga. Enquanto o
PTT está solto nada é capturado e nada é codificado — não é só o envio que
para. A caixa "Voz aberta" troca o PTT por microfone sempre ligado.

Os pontos coloridos ao lado dos botões mostram quem está falando: o verde é
você transmitindo, o azul é o peer (baseado em pacotes de áudio chegando).

### Um socket de mídia, dois streams

O áudio **não abre socket novo**: viaja no mesmo UDP da fase 3 (47102 por
padrão), multiplexado pelo campo `StreamId` do cabeçalho de pacote — `0` é
vídeo, `1` é áudio. Manter dois sockets sincronizados na mão dá mais trabalho
que multiplexar um.

O caminho de recepção é diferente por stream. Vídeo continua na remontagem por
`FrameId`; áudio vai direto para o jitter buffer, porque um frame Opus de voz
cabe inteiro num pacote UDP (dezenas de bytes) — não há fragmentação a
resolver. Pacote de áudio que se diz fragmentado é descartado.

Formato: **48 kHz mono, frames de 960 amostras (20 ms)**, Opus em modo VoIP a
24 kbps com FEC in-band. O que o dispositivo entregar (estéreo, 44,1 kHz) é
convertido antes do encode. O `VoiceStart` (tipo 50) leva taxa, canais e
tamanho de frame pela sessão TCP, e é com isso que o outro lado monta o
decoder; `VoiceStop` é 51.

### Jitter buffer

É o núcleo da fase — é o que faz a voz soar contínua apesar de UDP chegando em
intervalos irregulares. As regras importam mais que o tamanho:

- segura **3 frames (60 ms)** antes de começar a tocar, para absorver jitter
  normal sem latência perceptível demais;
- o playback chama `Pull()` a cada 20 ms **incondicionalmente** — rede ruim
  degrada, não trava;
- frame que não chegou vira **PLC do próprio Opus** e a expectativa avança.
  Esperar voz atrasada é pior que um gap disfarçado;
- pacote que chega depois do seu lugar já ter tocado é descartado: playback não
  volta no tempo;
- profundidade é limitada a **7 frames (140 ms)**; além disso os mais antigos
  são jogados fora e o playback pula à frente. Sem isso, uma rede que travou
  por 3 segundos voltaria tocando o acúmulo, e a chamada ficaria atrasada para
  sempre;
- depois de 25 frames ocultados sem nada chegar, ele silencia e volta a
  bufferizar em vez de gerar PLC eternamente.

Tudo isso é testado sem hardware de áudio: o buffer recebe um decoder falso e
sequências construídas à mão.

### Limitações conhecidas

- Sem cancelamento de eco e sem detecção de atividade de voz (VAD) — é por isso
  que o PTT é o padrão. Em "voz aberta" com caixas de som, o peer pode ouvir o
  retorno do próprio áudio.
- Sem RTCP: não há feedback de taxa do receptor, nem para vídeo nem para voz.
- A porta de mídia aceita pacotes de qualquer origem; o `SenderId` do cabeçalho
  (fase 5) diz de quem cada pacote se diz ser, mas não autentica ninguém.

## Sala em mesh (fase 5)

Até a fase 4 tudo assumia **um peer conectado por vez**. A fase 5 quebra essa
suposição: a **sala** agrupa N sessões e passa a ser o alvo de chat, tela e voz,
no lugar de um `PeerId` individual.

O escopo é **mesh completo, sem SFU ou retransmissor**: cada membro mantém uma
sessão com cada outro membro. O teto esperado é 3 pessoas confortável — onde 4
começa a doer está documentado abaixo, e é conhecimento, não bug.

### A sala não tem dono

Não existe servidor da sala para perguntar quem está lá. A lista de membros é
replicada por **gossip simples**:

1. A convida B (`RoomInvite` na sessão TCP entre os dois), já incluindo a lista
   de membros que A conhece.
2. B aceita e responde `RoomJoin` com a sua própria visão.
3. B então **conecta com todos os outros membros da lista** que A mandou, via
   `ISessionManager.ConnectAsync` (a descoberta da fase 0 resolve o IP; se o peer
   não estiver visível, vale o convite manual da fase 0).
4. Conforme cada conexão se estabelece, B anuncia sua entrada com
   `RoomMemberList`. Quem recebe e **aprende algo novo** retransmite; quem não
   aprende nada fica quieto, e é isso que faz o gossip terminar em vez de ecoar.

A convergência **não é instantânea**. Durante os primeiros segundos de alguém
entrando, membros diferentes podem ter visões levemente diferentes de quem está
na sala. Isso é esperado: a UI atualiza conforme `RoomMemberList` chega, sem
travar esperando "certeza".

A fusão de listas é **aditiva** — um `RoomMemberList` nunca remove ninguém. Um
peer atrasado que gossipasse sua visão velha despejaria membros vivos. Remoção
acontece só por `RoomLeave` explícito ou por sessão que fechou de vez (a máquina
de reconexão da fase 1 decide isso, o que leva até ~15 s).

Convites são **aceitos automaticamente**: o app é para uma LAN ou rede Radmin em
que o usuário já confia, e um diálogo de confirmação deixaria quem convidou
esperando sem retorno.

### Quem está falando

O indicador de fala usa o sinal explícito `RoomSpeaking`, enviado ao apertar e ao
soltar o push-to-talk — **nunca inferido dos pacotes de mídia chegando**. É mais
barato e mais confiável do que fazer cada peer inspecionar o fluxo de áudio de
todos os outros.

Como rede não é confiável, quem fala **renova** o sinal a cada 1 s, e quem ouve
descarta um falante que não renovou em 3 s. Sem isso, o app do outro lado
travando com a tecla pressionada deixaria a bolinha acesa para sempre.

### Mídia em mesh

Quem compartilha a tela **codifica uma vez e envia N vezes**: o
`IMediaSender.SendFrame` fragmenta o frame uma vez e manda cada fragmento para
todos os destinos. A CPU de encode não escala com a plateia — só a banda de
upload escala. Esse é exatamente o custo do mesh.

Voz é o contrário do vídeo: **todo mundo com todo mundo, simultaneamente**. Cada
receptor mantém **um `IJitterBuffer` por peer remetente**, nunca um só
compartilhado — os números de sequência são por remetente, e misturar fontes num
único buffer faria cada pacote parecer fora de ordem em relação ao anterior. A
cada 20 ms o `AudioMixer` puxa um frame de cada buffer ativo e soma, dividindo
pela raiz do número de fontes que realmente produziram áudio (uma voz sozinha não
fica com metade do volume quando a segunda pessoa fala) e aplicando um clamp.

Todo buffer ativo é puxado em todo tick, mesmo sem áudio: um jitter buffer que
não é drenado no ritmo certo dessincroniza.

#### `SenderId` no cabeçalho de mídia

O socket de mídia agora recebe datagramas de vários remetentes ao mesmo tempo, e
o filtro por endereço de origem da fase 3 não serve mais — além de quebrar quando
NAT ou Radmin remapeia a porta. O cabeçalho binário passou de 13 para 29 bytes
com um `SenderId` (Guid de 16 bytes) explícito, e a versão do pacote foi para 2;
um pacote versão 1 não decodifica mais.

O receptor mantém um `FrameReassembler` **por remetente** (no máximo 8, já que o
socket aceita bytes de qualquer origem) e descarta os próprios pacotes de volta.

### Bitrate por número de receptores

Ajuste **por degrau, com base na contagem de membros** — não adaptativo por
feedback de rede (RTCP segue fora de escopo). Mais espectadores puxam mais upload
do mesmo link, então a fonte diminui:

| Receptores | Bitrate   | FPS |
| ---------- | --------- | --- |
| 1          | 3000 kbps | 30  |
| 2          | 2200 kbps | 30  |
| 3          | 1600 kbps | 24  |
| 4 ou mais  | 1000 kbps | 15  |

Quem compartilha reage a `RoomMemberList` chegando durante o compartilhamento
ativo. Como o degrau muda **fps além do bitrate**, e ambos são negociados na
abertura do encoder, a captura é reiniciada com os novos parâmetros e o
`ScreenShareStart` é reanunciado — o que também gera o keyframe que os receptores
precisam para acompanhar a mudança.

**Estes números são um ponto de partida razoável, não resultado de medição.**
Calibrar exige o teste de 4 pessoas descrito abaixo.

### Um apresentador por vez

Só um membro compartilha a tela por vez: para os outros, o botão fica
desabilitado com um tooltip dizendo quem está apresentando. Isso é **decisão de
produto** desta fase — evita N streams de vídeo simultâneos, que o mesh não
aguenta bem — e não limitação do protocolo. Voz não tem essa restrição.

### Histórico

A tabela `messages` ganhou a coluna `room_id` (nullable) por migração incremental
via `PRAGMA user_version`: `NULL` é a conversa 1:1 antiga, preenchido é mensagem
de sala. No balão da sala o nome do remetente aparece — em 1:1 ele era implícito,
em grupo precisa ser explícito.

Mensagem de sala não é confirmada com `ChatAck`: não há um destinatário único
para confirmar, e N acks para um mesmo id deixariam o estado "entregue" ambíguo.

### Resultados do teste com 4 pessoas

**Ainda não executado.** Precisa de quatro máquinas em LAN e em Radmin VPN. O
registro pedido pelo cronograma vai aqui: degrau de bitrate observado, CPU de
quem compartilha (encode único, 3 envios) e de quem recebe (decode mais mixagem
de até 3 fontes de áudio), e se houve engasgo perceptível de vídeo ou áudio e em
qual papel.

Esse registro é a base para decidir se compensa implementar um relay/SFU depois —
que reintroduziria a dependência de servidor que o projeto evita — ou se o teto
de ~3-4 pessoas é aceitável. **Documentar o limite é a entrega, não escondê-lo.**

### Limitações conhecidas

- Convite é aceito automaticamente; não há recusa nem lista de bloqueio.
- Um apresentador de tela por vez (decisão de produto, ver acima).
- Sem consenso: a lista de membros converge por gossip aditivo, o que basta para
  3-4 nós e não pretende resolver partição de rede.
- A saída implícita depende do timeout de reconexão da fase 1, então leva até
  ~15 s para a lista atualizar quando alguém cai sem avisar.
- O `SenderId` identifica o remetente, mas **não o autentica**: nada impede um
  peer na mesma rede de forjar o campo.


## Dependências

- `Vortice.Direct3D11` / `Vortice.MediaFoundation` (bindings DXGI/D3D11/MF).
- `NAudio.Wasapi` (captura e playback WASAPI) e `Concentus` (Opus em C# puro,
  sem DLL nativa extra). É o pacote `NAudio.Wasapi` e não o `NAudio` completo
  porque o metapacote só entrega WASAPI em target `-windows`, e `LisoP2P.Media`
  precisa continuar em `net10.0` puro.
- `MessagePack`, `Microsoft.Data.Sqlite`, `CommunityToolkit.Mvvm`,
  `MaterialDesignThemes`.

## Build

```
dotnet build
```

## Rodar duas instâncias na mesma máquina

Para testar a descoberta localmente sem duas máquinas, rode duas instâncias
com portas diferentes:

```
dotnet run --project LisoP2P.App -- --discovery-port 47100 --session-port 47101
dotnet run --project LisoP2P.App -- --discovery-port 47100 --session-port 47201
```

A porta de mídia acompanha a de sessão: quando `--session-port` é diferente do
padrão, a porta de mídia vira `session-port + 1` (47202 no exemplo acima), o
que evita que a segunda instância dispute o socket UDP com a primeira.
`--media-port` sobrescreve isso explicitamente.

Cada instância deve aparecer na lista de peers da outra em poucos segundos.
Fechar uma delas deve removê-la da lista da outra em até 8 segundos.

## Testes

```
dotnet test
```

## Portas usadas

- **47100/UDP** — descoberta de peers (broadcast), configurável via
  `--discovery-port`.
- **47101/TCP** — sessão 1:1 (handshake, chat, keepalive, controle de
  compartilhamento), configurável via `--session-port`.
- **47102/UDP** — vídeo (`StreamId` 0) e voz (`StreamId` 1), configurável via
  `--media-port` (por padrão, `--session-port + 1` quando a porta de sessão
  não é a padrão).

## Firewall do Windows

Na primeira execução, o Windows deve perguntar se permite que o LisoP2P se
comunique em redes privadas/públicas. **Isso precisa ser aceito** — se você
clicar em "Cancelar", a descoberta automática de peers para de funcionar
silenciosamente, sem nenhum erro visível na interface. Se isso acontecer,
libere o app manualmente em
`Configurações do Windows > Rede e Internet > Firewall do Windows Defender >
Permitir um aplicativo através do firewall`.
