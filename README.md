# LisoP2P

App de comunicação P2P para Windows, estilo Discord, que conecta máquinas
diretamente pela LAN (real ou virtual via Radmin VPN), sem servidor central.

O cronograma de **6 fases está completo**: solution, protocolo de mensagens,
descoberta de peers na rede, sessão TCP 1:1, chat de texto com histórico local,
captura de tela com encode H.264, transmissão de tela, voz (microfone e/ou som
do sistema, Opus, push-to-talk), **sala em mesh** — várias sessões simultâneas
com chat, tela e voz para todos os membros — e o acabamento da fase 6: tela de
configurações persistida, identidade por par de chaves Ed25519 com fingerprint
visível, erros que viram estado na tela em vez de exceção crua, e um executável
único que roda sem .NET instalado. Veja `fase0-prompt.md` a `fase6-prompt.md` em
`LisoP2P.App/Docs/` para as especificações completas de cada fase.

> **Atualizando de uma versão anterior?** A fase 6 troca o `PeerId` aleatório por
> uma chave pública. Sua identidade antiga não pode ser convertida e é
> substituída na primeira execução — veja
> [Identidade e fingerprint](#identidade-e-fingerprint-fase-6).

## Estrutura

- `LisoP2P.Core` — identidade de peer (par de chaves Ed25519, fingerprint),
  protocolo de mensagens (MessagePack), framing de tamanho-prefixado para o
  transporte TCP, as configurações persistidas (`AppSettings`, `PortResolver`,
  `AppPaths`) e o log em arquivo (`FileAppLogger`).
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
- `LisoP2P.App` — interface WPF (MVVM), a tela de configurações, a tela de
  boas-vindas, o banner de erros (`IErrorPresenter`) e o `AppHost`, que separa o
  que depende de porta do que sobrevive a uma troca de porta.
- `LisoP2P.Tests` — testes de unidade (xUnit).

## Nome de usuário

O campo **Seu nome**, no topo da coluna da esquerda, define o nome que os
outros peers veem. Enter ou o botão "Salvar" grava o novo nome em
`identity.json`, junto do `PeerId`.

O `PeerId` **não muda** com o rename: é a chave pública Ed25519 gerada na
primeira execução, e continua sendo a única chave de identidade usada por
descoberta, sessões e histórico de chat. Trocar o nome não reabre sessão, não
duplica peer na lista e não afeta as conversas salvas.

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

### Requisitos

GPU com suporte a Direct3D 11 e driver funcional; sem isso a janela de teste
mostra o motivo da falha em vez de quebrar o app.

Veja [Limitações conhecidas](#limitações-conhecidas), onde as restrições de todas
as fases estão reunidas.

## Transmissão de tela (fase 3)

Com a conversa conectada, o botão **Compartilhar tela** no cabeçalho começa a
transmitir para aquele peer, usando o monitor, a resolução e o fps escolhidos em
Configurações → Vídeo. Do outro lado a área de vídeo aparece sozinha acima das
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

Veja [Limitações conhecidas](#limitações-conhecidas), onde as restrições de todas
as fases estão reunidas.

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

Veja [Limitações conhecidas](#limitações-conhecidas), onde as restrições de todas
as fases estão reunidas.

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
com um `SenderId` explícito (versão 2). A fase 6 trocou esse campo pelo
`PeerId` novo, de 32 bytes, o que levou o cabeçalho a 45 bytes e a versão do
pacote a 3; um pacote de versão anterior não decodifica mais.

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

Veja [Limitações conhecidas](#limitações-conhecidas), onde as restrições de todas
as fases estão reunidas.


## Configurações (fase 6)

Até a fase 5 as preferências viviam espalhadas: o seletor de monitor era um
dropdown solto na janela de teste de captura, as portas só existiam como flag de
linha de comando, e os dispositivos de áudio eram escolhidos dentro da conversa e
de novo dentro da sala. A fase 6 consolida tudo em **Configurações**, no canto
superior esquerdo da janela principal, persistido em
`%APPDATA%/LisoP2P/settings.json`.

- **Geral** — nome de usuário e o caminho do arquivo de configurações.
- **Vídeo** — monitor, resolução de captura, fps alvo, e a tabela de bitrate por
  degrau da fase 5 em **somente leitura**.
- **Áudio** — dispositivo de entrada e de saída, fonte (microfone, som do
  sistema, ambos), se usa push-to-talk e qual tecla (o botão "Definir" grava a
  próxima tecla pressionada).
- **Rede** — as três portas, com validação de faixa e um botão "Verificar
  portas" que tenta um bind rápido para dizer se estão livres agora.
- **Identidade** — o fingerprint desta instalação, com botão de copiar.

Nada é aplicado campo a campo: tudo vale ao clicar em **Salvar**. Uma porta pela
metade, digitada tecla a tecla, reiniciaria a rede a cada caractere.

**A troca de dispositivo de áudio é a exceção e vale na hora**, inclusive com a
voz já ativa.

### Precedência: flag > arquivo > default

As flags `--discovery-port`, `--session-port` e `--media-port` da fase 0
continuam funcionando e **ganham do arquivo**. Isso não é preferência de estilo:
o teste manual das fases 0 e 1 distingue duas instâncias na mesma máquina só pelo
`--session-port`, e um `settings.json` capaz de sobrescrever isso quebraria o
teste.

A regra que deriva a porta de mídia do `--session-port` (fase 0) vale **só para a
linha de comando**. Uma porta de sessão que veio do arquivo mantém a porta de
mídia que veio do arquivo.

Pela mesma razão, a pasta de dados só é aninhada sob o número da porta quando a
**flag** está presente. Se ela seguisse a porta efetiva, mudar a porta em
Configurações moveria o `identity.json` e entregaria uma identidade nova ao
usuário sem aviso.

### Aplicar uma porta nova sem reiniciar o processo

Trocar uma porta derruba e reconstrói tudo o que depende dela — descoberta,
sessões, sockets de mídia, compartilhamento de tela, voz, sala. O que sobrevive
fica no provider raiz: identidade, configurações, histórico de chat, pipeline de
captura e o banner de notificações. É por isso que o `AppHost` existe.

A tela avisa antes: **as sessões abertas são encerradas e a sala é deixada.** O
processo continua rodando.

## Identidade e fingerprint (fase 6)

A fase 0 gerava um `Guid` aleatório como `PeerId`. Era um identificador estável,
nunca uma identidade criptográfica, e não havia nada que o usuário pudesse
comparar.

O `PeerId` agora **é** a chave pública Ed25519 do peer, 32 bytes. Carregar a
chave inteira em vez de um hash dela significa que qualquer peer consegue
calcular o fingerprint de quem ouviu falar — de uma lista de membros, de um
`Hello` — sem uma segunda viagem, e deixa a porta aberta para assinar envelopes
numa fase futura.

A chave privada é gerada localmente e guardada em `identity.json` protegida pelo
**DPAPI do Windows** (escopo do usuário atual). Ela nunca sai da classe que a
carrega: nada no app assina nada ainda.

O fingerprint é o **SHA-256 da chave pública**, exibido em 16 grupos de 4
caracteres hex:

```
81AE 9F98 FB4A 1383 3D58 6A90 2825 D2E9 FB07 71A7 56B2 E099 95EC E72F 09B8 2F12
```

Ele aparece em dois lugares: em Configurações → Identidade (com botão de copiar)
e, **uma vez por peer**, como notificação não-bloqueante quando a sessão abre
("Conectado com fulano — fingerprint A1B2 C3D4…").

**Nada é bloqueado se os fingerprints não conferirem.** Isto é verificação por
transparência — dois usuários leem o valor em voz alta numa chamada e comparam —
e não um portão de aprovação. Exigir confirmação manual antes de conectar é uma
história de segurança maior do que esta fase cobre.

### Mudança incompatível para quem vem das fases 0-5

Um `identity.json` antigo tem um `Guid` e um nome. **Não há como transformar um
Guid num par de chaves**, então a identidade é substituída na primeira execução
após a atualização:

- o **nome de usuário é preservado** (ele nunca foi a identidade);
- o **`PeerId` é novo**, e os peers que já conheciam você passam a ver um
  contato desconhecido;
- o histórico de chat continua no `chat.db`, mas as linhas antigas apontam para
  ids que não existem mais e ficam sem dono (elas não somem e não quebram nada);
- o app avisa na tela, uma vez, quando a substituição acontece.

Não há usuários em produção, e o cronograma aceita explicitamente esse custo.

## Erros visíveis (fase 6)

A política é uma frase: **erro de rede nunca é modal bloqueante, sempre é estado
visível.** Não existe `MessageBox` travando a thread de UI em caminho de rede.

`IErrorPresenter` tem dois modos:

- `ShowTransient` para o que se resolve sozinho (reconectando, convite enviado);
  some sozinho em alguns segundos.
- `ShowPersistent` para o que exige ação (porta ocupada, nenhum encoder
  disponível); fica até ser dispensado, e reportar a mesma condição de novo
  **substitui** o aviso em vez de empilhar uma segunda cópia.

Tudo o que passa pelo banner também vai para `%APPDATA%/LisoP2P/app.log`, com
rotação por tamanho — é para depurar um problema relatado sem reproduzir do zero,
não telemetria e não relatório remoto.

O que os eventos que já existiam desde as fases anteriores passaram a produzir:

| Situação | Como aparece agora |
| --- | --- |
| Peer cai durante o chat | Bolinha e texto do cabeçalho mudam de cor: verde conectado, amarelo conectando/reconectando, vermelho desconectado |
| Reconexão esgota as tentativas (fase 1) | A sessão não some calada: aparece o botão **"Tentar novamente"** no cabeçalho da conversa |
| Peer cai durante o compartilhamento de tela | A área de vídeo ganha o overlay "Conexão perdida com *fulano*" em vez de congelar no último frame sem explicação |
| Peer cai durante a voz | O indicador de fala apaga e a bolinha do membro fica acinzentada, com tooltip "desconectado" |
| Falha ao iniciar a captura | Aviso persistente com o erro específico e a sugestão de verificar atualização do driver de vídeo |
| Nenhum encoder H.264 utilizável | Compartilhar tela fica desabilitado com o motivo no tooltip; o resto do app continua usável |
| Porta em uso ao iniciar | Tela inicial dizendo qual porta conflitou, com atalho direto para Configurações → Rede |

A tela de porta ocupada é o único ponto em que o app se recusa a prosseguir — e
mesmo ela oferece "Abrir Configurações", "Tentar de novo" e "Sair" em vez de
morrer.

## Primeira execução

Quando o par de chaves é gerado — primeira execução, ou pasta de dados apagada —
o app abre uma tela única de boas-vindas: nome de usuário, o aviso sobre liberar
as portas no Firewall do Windows (o mesmo do README, agora também na UI, já que
nem todo mundo lê o README) e o fingerprint recém-criado. Um botão, "Começar".

## Publicar o executável

```
dotnet publish LisoP2P.App -c Release -p:PublishProfile=win-x64
```

Sai um único `LisoP2P.exe` (~68 MB) em
`LisoP2P.App/bin/Release/publish/win-x64/`, que roda **sem .NET instalado**.

O perfil de publicação (`LisoP2P.App/Properties/PublishProfiles/win-x64.pubxml`)
equivale a `--self-contained -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true
-p:EnableCompressionInSingleFile=true`. Essas propriedades ficam no perfil e não
no `.csproj` para que `dotnet build` e `dotnet run` continuem rápidos e
independentes de RID durante o desenvolvimento.

**Trimming fica desligado de propósito.** WPF não suporta trimming, e Vortice,
NAudio e Concentus alcançam tipos por interop e reflexão — um build trimado
quebra em runtime, silenciosamente, exatamente na máquina limpa que não tem SDK
para depurar. O tamanho é o preço dessa certeza.

### Checklist de teste em máquina limpa

- [x] O `.exe` publicado inicia nesta máquina e abre a tela de primeira execução.
- [x] `%APPDATA%/LisoP2P/` é criado no primeiro boot, com `identity.json`,
      `settings.json` e `app.log` — sem depender de ter rodado `dotnet run` antes.
- [ ] O `.exe` inicia numa máquina Windows **sem SDK nem runtime .NET**.
- [ ] Captura de tela e voz funcionam nessa máquina limpa (é onde o interop
      nativo de Vortice, NAudio e Concentus realmente se prova).
- [ ] O Windows pede a liberação no firewall e a descoberta funciona depois.

## Onde ficam os arquivos

Tudo em `%APPDATA%/LisoP2P/`:

| Arquivo | O que é |
| --- | --- |
| `identity.json` | Chave pública, chave privada protegida por DPAPI e nome de usuário |
| `settings.json` | Portas, monitor, resolução, fps, dispositivos de áudio, push-to-talk |
| `chat.db` | Histórico de chat 1:1 e de sala (SQLite) |
| `app.log` | Erros e eventos da aplicação, com rotação por tamanho |
| `logs/media.log` | Diagnóstico de captura e encode (fase 2) |
| `capture-test.mp4` | Gravação da janela de teste de captura, quando ligada |

Quando `--session-port` é passado com um valor diferente do padrão, tudo isso vai
para `%APPDATA%/LisoP2P/<session-port>/` — é o que permite rodar duas instâncias
na mesma máquina com identidades distintas.

`settings.json` é texto e pode ser editado à mão; um valor inválido vira o
default, nunca uma recusa a iniciar.

## Limitações conhecidas

Consolidado das fases 2, 3, 4 e 5, num lugar só.

**Captura e vídeo**

- Conteúdo protegido por DRM aparece **preto** na captura. É comportamento da
  Desktop Duplication API, não um bug do app, e não há como contornar.
- A captura é de um monitor inteiro; não há captura de janela específica.
- Um apresentador de tela por vez na sala — decisão de produto, não limite de
  protocolo.
- Sem RTCP: não há feedback de taxa do receptor. O controle de taxa é o degrau
  de bitrate por número de membros mais o descarte local do `CapturePipeline`.
  Os números do degrau são um ponto de partida, não medição.

**Voz**

- Sem cancelamento de eco e sem detecção de atividade de voz (VAD) — é por isso
  que o push-to-talk é o padrão. Em "voz aberta" com caixas de som, o peer pode
  ouvir o retorno do próprio áudio.

**Sala**

- Teto prático de **3 a 4 pessoas**: o mesh é completo, sem SFU nem
  retransmissor, então a banda de upload de quem compartilha escala com a
  plateia. Onde exatamente isso dói ainda não foi medido (ver abaixo).
- Convite é aceito automaticamente; não há recusa nem lista de bloqueio.
- Sem consenso: a lista de membros converge por gossip aditivo, o que basta para
  3-4 nós e não pretende resolver partição de rede.
- A saída implícita depende do timeout de reconexão da fase 1, então leva até
  ~15 s para a lista atualizar quando alguém cai sem avisar.

**Segurança**

- **Mensagens não são assinadas.** O par de chaves da fase 6 dá identidade
  estável e verificável a olho; ele não autentica cada envelope. Um peer na
  mesma rede pode forjar o `SenderId` de um pacote de mídia ou de um envelope.
  Assinar e verificar cada mensagem é fase futura, não implementado.
- Não há criptografia do tráfego: chat, tela e voz vão em claro pela LAN.
- O fingerprint é verificação por transparência, não um portão: nada é
  bloqueado se ele não conferir.

**Operação**

- Sem telemetria e sem relatório remoto de erro, por decisão. `app.log` local é
  o que existe.
- Windows apenas (WASAPI, DXGI, Media Foundation, DPAPI).

## Dependências

- `Vortice.Direct3D11` / `Vortice.MediaFoundation` (bindings DXGI/D3D11/MF).
- `NAudio.Wasapi` (captura e playback WASAPI) e `Concentus` (Opus em C# puro,
  sem DLL nativa extra). É o pacote `NAudio.Wasapi` e não o `NAudio` completo
  porque o metapacote só entrega WASAPI em target `-windows`, e `LisoP2P.Media`
  precisa continuar em `net10.0` puro.
- `BouncyCastle.Cryptography` (Ed25519 gerenciado — o .NET 10 ainda não tem
  Ed25519 avulso, e uma implementação gerenciada mantém o publish single-file
  livre de uma biblioteca de criptografia nativa) e
  `System.Security.Cryptography.ProtectedData` (DPAPI).
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

A instância com `--session-port` diferente do padrão usa
`%APPDATA%/LisoP2P/<session-port>/` como pasta de dados, então as duas têm
identidades, configurações e históricos separados. **A flag é o que decide isso**
— trocar a porta em Configurações não move a pasta e não troca sua identidade.

## Testes

```
dotnet test
```

## Portas usadas

- **47100/UDP** — descoberta de peers (broadcast).
- **47101/TCP** — sessão 1:1 (handshake, chat, keepalive, controle de
  compartilhamento).
- **47102/UDP** — vídeo (`StreamId` 0) e voz (`StreamId` 1).

As três são configuráveis em **Configurações → Rede** e pelas flags
`--discovery-port`, `--session-port` e `--media-port`. A ordem de resolução é
**flag > `settings.json` > default**; a flag `--media-port`, quando ausente e
`--session-port` foi passado, vira `session-port + 1`.

## Firewall do Windows

Na primeira execução, o Windows deve perguntar se permite que o LisoP2P se
comunique em redes privadas/públicas. **Isso precisa ser aceito** — se você
clicar em "Cancelar", a descoberta automática de peers para de funcionar
silenciosamente, sem nenhum erro visível na interface. Se isso acontecer,
libere o app manualmente em
`Configurações do Windows > Rede e Internet > Firewall do Windows Defender >
Permitir um aplicativo através do firewall`.
