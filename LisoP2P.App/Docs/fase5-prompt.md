# Fase 5 — Grupo (mesh)

Continuação do P2PChat. As fases anteriores entregaram descoberta (0), sessão TCP
1:1 com chat (1), captura/encode de tela (2), transmissão de vídeo UDP
fragmentada (3), e voz multiplexada no mesmo canal com jitter buffer (4). Tudo
até aqui assumia **um único peer conectado por vez**.

Esta fase quebra essa suposição: várias sessões simultâneas, agrupadas em
"sala", com chat, tela e voz servindo a todos os membros. É a fase que testa se
as fundações das fases 1-4 foram bem generalizadas ou se tinham 1:1 hardcoded em
algum lugar — espere encontrar esses pontos ao migrar.

**Escopo travado em mesh completo**, sem SFU/retransmissor. O próprio cronograma
já define o teto esperado: 3 pessoas confortável, documentar onde 4 começa a
doer. Não tente resolver esse teto nesta fase — é conhecimento, não bug.

---

## 1. O que muda estruturalmente

Até a fase 4, `ISessionManager.Sessions` já era um dicionário por `PeerId` — a
fase 1 foi desenhada prevendo isso. O que era 1:1 de fato era: a UI (uma
conversa ativa por vez), a orquestração de mídia (`IScreenShareSession`/voz
falava com "o peer"), e o conceito de identidade do chat (mensagem ia para uma
sessão, não para uma sala).

Esta fase introduz a **Sala** como entidade nova que fica acima de
`ISessionManager`: agrupa N sessões, define quem está "dentro", e é o alvo de
chat/tela/voz daqui pra frente — não mais um `PeerId` individual.

```csharp
public sealed record RoomId(Guid Value);

public sealed class RoomMember
{
    public PeerId Id { get; init; }
    public string Nickname { get; set; }
    public SessionState ConnectionState { get; set; }
    public bool IsSpeaking { get; set; }
    public bool IsSharingScreen { get; set; }
}
```

Sala não tem servidor dono — é um acordo distribuído entre os peers conectados
entre si. Trate como **lista replicada por gossip simples**, não como estado
autoritativo em algum peer.

---

## 2. Protocolo de sala (P2PChat.Core)

Novo `MessageType` (viaja pela sessão TCP existente entre cada par):

```csharp
RoomInvite      = 60,   // convite para entrar numa sala
RoomJoin        = 61,   // aceite, com snapshot de membros conhecidos
RoomMemberList  = 62,   // atualização de membros (gossip)
RoomLeave       = 63,
RoomSpeaking    = 64,   // sinaliza início/fim de fala (sem payload de áudio)
```

Payload central:

```csharp
[MessagePackObject]
public sealed class RoomMemberListPayload
{
    [Key(0)] public Guid RoomId { get; init; }
    [Key(1)] public List<RoomMemberInfo> Members { get; init; }
}

[MessagePackObject]
public sealed class RoomMemberInfo
{
    [Key(0)] public Guid PeerId { get; init; }
    [Key(1)] public string Nickname { get; init; }
}
```

### Como alguém entra numa sala existente com N pessoas

Não existe um "servidor da sala" para perguntar quem está lá. O fluxo é:

1. A convida B (`RoomInvite` na sessão TCP 1:1 entre A e B), incluindo o
   `RoomMemberListPayload` atual conhecido por A.
2. B aceita (`RoomJoin`) de volta para A.
3. **B agora precisa conectar com todos os outros membros da lista que A
   mandou.** Para cada um, B usa `ISessionManager.ConnectAsync` normalmente (a
   descoberta da fase 0 já resolve o IP se estiverem na mesma
   LAN/Radmin; se não, é o mesmo mecanismo de convite manual da fase 0).
4. Conforme cada conexão nova se estabelece, B manda `RoomMemberList` anunciando
   sua própria entrada para os outros membros, que atualizam suas listas locais
   e retransmitem se souberem de alguém que B não sabe.

Isso é gossip básico, suficiente para 3-4 nós — não implemente um protocolo de
consenso, seria trabalho desproporcional ao tamanho do grupo que este projeto
suporta.

**Ponto que vai gerar bug se ignorado: convergência da lista de membros não é
instantânea.** Durante os primeiros segundos de alguém entrando, membros
diferentes podem ter visões levemente diferentes de quem está na sala. Isso é
esperado — não trave a UI esperando "certeza" de que todos convergiram, apenas
atualize conforme `RoomMemberList` chega.

### Saída

`RoomLeave` explícito ao clicar "sair". Saída implícita (crash, queda de rede):
detectada pela própria máquina de reconexão da fase 1 — quando uma sessão vai
para `Closed` definitivo (esgotou as tentativas de reconexão), remova o membro
da sala local e propague `RoomMemberList` atualizado para quem ainda está
conectado.

---

## 3. Roteamento de chat para todos (P2PChat.Net)

`RoomChatRouter`, novo componente:

```csharp
public interface IRoomChatRouter
{
    Task BroadcastAsync(RoomId room, string text, CancellationToken ct);
    event Action<PeerId, ChatMessagePayload> MessageReceived;
}
```

Envio: reusa `ChatMessage` (fase 1) enviado individualmente **por sessão TCP**
para cada membro da sala — não existe multicast em conexões P2P diretas, então
"broadcast" aqui é literalmente um loop enviando a mesma mensagem N vezes, uma
por sessão ativa.

`MessageId` (já existente desde a fase 1) evita duplicação se a topologia de
gossip fizer alguém receber a mesma notificação de mensagem por dois caminhos —
mas chat em si não é retransmitido por terceiros nesta fase, só o anúncio de
membros é. Cada peer manda chat direto para cada outro peer da sala com quem
tem sessão.

Histórico (`IChatStore` da fase 1): estenda a tabela `messages` com uma coluna
`room_id` nullable (`NULL` = conversa 1:1 antiga, preenchido = mensagem de
sala). Migração incremental via `PRAGMA user_version`, como já estabelecido.

---

## 4. "Quem está falando" (P2PChat.Net)

A fase 4 já capta, por construção, quando frames de áudio estão sendo enviados
(PTT pressionado). Conecte isso ao protocolo de sala:

- Ao começar a enviar áudio (tecla PTT pressionada), envie `RoomSpeaking` com
  flag "iniciou" para todos os membros da sala.
- Ao soltar, envie com flag "parou".
- **Não infira "falando" a partir de pacotes de mídia chegando** — use o sinal
  explícito de controle. É mais barato, mais confiável, e não exige que cada
  peer inspecione o fluxo de áudio de todos os outros para decidir isso.

Cada `RoomMember.IsSpeaking` na UI reflete o último sinal recebido daquele peer.
Timeout de segurança: se não vier "parou" em até 3s depois do último "iniciou"
sem renovação (ex. app do outro lado crashou com PTT pressionado), assuma parado
— não deixe o indicador travado ligado para sempre.

---

## 5. Mesh de mídia (P2PChat.Media / P2PChat.Net)

Esta é a parte estrutural mais pesada da fase.

### Vídeo (compartilhamento de tela em grupo)

Quem compartilha tela **envia o mesmo stream codificado para cada membro da
sala**, um `IMediaSender.SendFrame` por destino. **Não recodifica por
destinatário** — um encode, N envios do mesmo `EncodedFrame`. Isso significa que
a CPU de encode não escala com o número de espectadores, só a banda de upload
escala (é exatamente o "custo do mesh" que o cronograma já sinalizava).

Cada receptor mantém seu próprio `IMediaReceiver`/decoder, exatamente como na
fase 3, só que agora simultâneo para até N-1 remetentes potenciais — na prática,
apenas um por vez deve estar compartilhando tela nesta fase (ver seção 7,
UI: só uma pessoa compartilha por vez, para não multiplicar decode
simultâneo desnecessariamente na v1 de grupo).

`IMediaReceiver` já roteia por peer de origem desde que o pacote carregue
identificação suficiente — confirme que o cabeçalho de mídia da fase 3 permite
distinguir remetente (se dependia implicitamente do único socket ter um único
peer do outro lado, isso quebra em grupo e precisa de ajuste: inclua
`SenderId` no cabeçalho de pacote de mídia, ou associe pela porta/endpoint de
origem do datagrama recebido — prefira o `SenderId` explícito, é mais robusto
a NAT/Radmin remapeando porta).

### Áudio

Ao contrário do vídeo, voz em grupo é **todo mundo com todo mundo
simultaneamente** — não faz sentido restringir a um falante por vez. Cada membro
envia seu áudio para todos os outros membros (mesmo mecanismo de N envios do
mesmo frame). Cada receptor mantém **um `IJitterBuffer` por peer remetente**, não
um buffer único compartilhado — misturar pacotes de fontes diferentes num único
buffer por sequência quebra a lógica de ordenação da fase 4.

### Mixagem de áudio recebido

```csharp
public interface IAudioMixer
{
    void SetActiveSources(IReadOnlyCollection<PeerId> peers);
    float[] MixNextFrame();   // chamado a cada 20ms pelo playback
}
```

A cada tick de 20ms, puxa um frame de cada `IJitterBuffer` ativo (um por peer
falando) e soma normalizando para evitar clipping (ex. divide pela raiz do
número de fontes ativas, ou um limitador simples — não precisa ser
sofisticado). `IAudioPlayback` da fase 4 passa a consumir do `IAudioMixer` em
vez de um único `IJitterBuffer` diretamente.

---

## 6. Controle de bitrate por número de receptores

Requisito explícito do cronograma para esta fase. Nesta v1, **ajuste é por
degrau baseado em contagem de membros da sala**, não adaptativo por feedback de
rede (RTCP fica fora de escopo, como já estabelecido na fase 3) — é
proporcional ao problema real: mais espectadores puxam mais upload do mesmo
link, então reduza a fonte.

```csharp
public sealed class BitrateLadder
{
    public static (int bitrateKbps, int fps) SelectFor(int receiverCount) => receiverCount switch
    {
        <= 1 => (3000, 30),
        2    => (2200, 30),
        3    => (1600, 24),
        _    => (1000, 15),
    };
}
```

Quem compartilha tela reconfigura `IVideoEncoder` (`Configure`, já existente
desde a fase 2) sempre que o número de membros na sala muda enquanto está
compartilhando — reage a `RoomMemberList` chegando durante o compartilhamento
ativo. Force um keyframe (`forceKeyframe: true`) logo após reconfigurar
resolução/fps, os receptores vão precisar dele para se ajustar ao novo
parâmetro.

Documente esses degraus como ponto de ajuste futuro no README — os números
acima são ponto de partida razoável, não resultado de medição real; calibrar
exige o teste de 4 pessoas que o cronograma já pede como entregável.

---

## 7. UI (P2PChat.App)

Mudança de modelo: a lista de conversas da fase 1 (peer a peer) ganha uma opção
de sala ao lado.

- Criar sala: escolhe um nome, convida peers da lista de descoberta (ou
  contatos já conhecidos).
- Tela de sala: lista de membros com indicador de fala (bolinha que acende
  quando `IsSpeaking`) e ícone de quem está compartilhando tela.
- **Apenas um botão "Compartilhar tela" ativo por vez na sala** — se alguém já
  está compartilhando, o botão para os outros membros fica desabilitado com
  tooltip explicando quem está compartilhando. Isso é decisão de produto para
  esta fase (evita N streams de vídeo simultâneos, que o mesh não aguenta bem),
  não limitação técnica de protocolo — documente como tal.
- Área de vídeo mostra quem está compartilhando no momento (nome visível).
- Chat da sala é compartilhado entre todos os membros, mesma área de mensagens
  da fase 1 mas com o nome do remetente visível em cada balão (na conversa 1:1
  isso era implícito, em grupo precisa aparecer).
- Contador de membros e indicador do degrau de bitrate atual (debug, mesmo
  toggle da fase 3/4).

---

## 8. Testes

Foco em lógica pura, como nas fases anteriores — mesh real com 3-4 processos é
validação manual no critério de aceite:

- `BitrateLadder.SelectFor`: cada degrau retorna o valor esperado, incluindo o
  caso `> 3`.
- `IAudioMixer`: com 2 fontes ativas emitindo frames conhecidos, o frame
  misturado não estoura amplitude (clamp/normalização funcionando); com 0
  fontes ativas, retorna silêncio sem lançar.
- Gossip de membros: dado que o peer A conhece {A, B}, recebe `RoomMemberList`
  de C anunciando {A, B, C, D}, o estado local de A converge para {A, B, C, D}
  sem duplicar entradas.
- Timeout de "falando": simulate clock, confirma que `IsSpeaking` volta a falso
  sozinho sem sinal de "parou" explícito.
- Roteamento de pacote de mídia por `SenderId`: dois remetentes diferentes
  mandando pacotes na mesma sessão de recepção não se misturam no
  `IMediaReceiver`.

---

## 9. Critério de aceite

**Três instâncias** (A, B, C), testadas em LAN e com Radmin VPN:

1. A cria sala e convida B e C separadamente; os três convergem para a mesma
   lista de membros em até 5 segundos.
2. Chat enviado por qualquer um aparece nos outros dois, com nome do remetente
   correto.
3. A compartilha tela: B e C recebem simultaneamente, com bitrate no degrau de
   3 membros (~1600kbps/24fps conforme a tabela).
4. B fala (PTT): indicador de fala acende em A e C corretamente; C fala ao
   mesmo tempo: A ouve as duas vozes mixadas, sem travar.
5. C sai da sala (fecha o app): A e B percebem a saída em até ~15s (janela de
   timeout de reconexão da fase 1) e a lista de membros atualiza.
6. Reentrar com D já com A/B/C ativos: D consegue se conectar a todos os três
   automaticamente a partir do convite de um deles.

**Depois, quarta instância (D) com os quatro simultâneos** — este é o teste de
"documentar onde quebra" pedido pelo cronograma, não um critério de aprovação:

7. Repita 3 e 4 com 4 membros. Anote no README:
   - Bitrate cai para o degrau `> 3` (1000kbps/15fps) — a qualidade percebida
     piora, isso é esperado, documente como está.
   - Meça CPU de quem está compartilhando tela (encode único, mas 3 envios) e
     de quem está recebendo (o receptor com mais carga, já que cada um decodifica
     e cada um mixa até 3 fontes de áudio).
   - Anote se algum dos quatro apresenta engasgo perceptível de vídeo ou áudio,
     e em qual papel (compartilhando, recebendo, ambos).
   - Esse registro vira a base de decisão do cronograma: se compensa
     implementar um relay/SFU depois, ou se o teto de ~3-4 pessoas é aceitável
     para o uso pretendido do app.

---

## 10. Nota sobre o teto do mesh

Não tente aumentar o teto nesta fase adicionando otimizações ad-hoc (ex.
reduzir ainda mais o bitrate, pular frames agressivamente) só para o teste de 4
"passar bonito". O objetivo aqui é ter **dados reais** de onde a arquitetura
mesh para de escalar, para decidir com informação se um relay central (que
reintroduziria a dependência de servidor que o projeto todo evita) vale a pena
para o seu caso de uso real. Documentar o limite é o entregável, não escondê-lo.

---

## 11. Entrega

Atualize `README.md` com: conceito de sala e como o gossip de membros funciona,
tabela de bitrate por degrau, e a seção de resultados do teste com 4 pessoas
descrita no item 7 do critério de aceite. Commits pequenos: modelo de sala e
protocolo, gossip de membros, roteamento de chat, sinalização de fala, mesh de
vídeo (SenderId no cabeçalho), mesh de áudio com buffer por peer, mixer,
bitrate ladder, UI de sala.
