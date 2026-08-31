# Fase 1 — Sessão e chat 1:1

Continuação do P2PChat. A fase 0 entregou solution, protocolo com envelope
MessagePack, identidade persistente e descoberta de peers por broadcast UDP.

Agora o objetivo é: **abrir uma sessão TCP com um peer descoberto, manter essa
sessão viva, e trocar mensagens de texto com histórico local.**

Escopo estritamente fechado em 1:1. **Não implemente salas, grupos, mesh, áudio,
vídeo ou captura de tela.** Uma conexão ativa por vez basta — a estrutura de dados
pode suportar várias, mas a UI e o fluxo são 1:1.

---

## 1. Extensão do protocolo (P2PChat.Core)

Novos tipos no enum existente:

```csharp
Hello        = 10,   // primeira mensagem após conectar
HelloAck     = 11,
Ping         = 12,
Pong         = 13,
ChatMessage  = 20,
ChatAck      = 21,   // recibo de entrega
Disconnect   = 30,
```

Payloads (todos MessagePack, mesmo padrão da fase 0):

```csharp
[MessagePackObject]
public sealed class HelloPayload
{
    [Key(0)] public string Nickname { get; init; }
    [Key(1)] public byte ProtocolVersion { get; init; }
}

[MessagePackObject]
public sealed class ChatMessagePayload
{
    [Key(0)] public Guid MessageId { get; init; }
    [Key(1)] public string Text { get; init; }
}

[MessagePackObject]
public sealed class ChatAckPayload
{
    [Key(0)] public Guid MessageId { get; init; }
}
```

`MessageId` é gerado pelo remetente. Serve para deduplicação (reenvio após
reconexão pode duplicar) e para o recibo de entrega.

Limite `Text` a 4000 caracteres no encode. Rejeite no decode acima disso — não
confie no que chega da rede.

### Framing

TCP é stream, não mensagem. **Todo envelope vai precedido de um prefixo de 4 bytes
little-endian com o tamanho do payload.** Escreva um `FrameReader`/`FrameWriter`
dedicado no Core:

- `FrameWriter.WriteAsync(Stream, Envelope, ct)` — serializa, escreve tamanho,
  escreve corpo.
- `FrameReader.ReadAsync(Stream, ct)` — lê 4 bytes, valida o tamanho contra um
  máximo (`MaxFrameSize = 64 * 1024` nesta fase), lê exatamente esse número de
  bytes, decodifica.

Um tamanho anunciado maior que `MaxFrameSize` derruba a conexão imediatamente. Sem
isso um peer malicioso ou um pacote corrompido faz o app alocar gigabytes.

Leitura parcial é a regra em TCP, não a exceção: use `ReadExactlyAsync`, nunca
assuma que um `ReadAsync` trouxe tudo.

---

## 2. Camada de sessão (P2PChat.Net)

### PeerSession

Representa uma conexão viva com um peer.

```csharp
public enum SessionState { Connecting, Handshaking, Connected, Reconnecting, Closed }

public interface IPeerSession : IAsyncDisposable
{
    PeerId RemoteId { get; }
    string RemoteNickname { get; }
    SessionState State { get; }
    TimeSpan? RoundTripTime { get; }

    event Action<SessionState> StateChanged;
    event Action<Envelope> MessageReceived;

    Task SendAsync(Envelope envelope, CancellationToken ct);
}
```

Internamente: um loop de leitura e uma fila de escrita. **Não escreva no
`NetworkStream` de múltiplas threads** — use um `Channel<Envelope>` com um único
consumidor fazendo o write. Escritas concorrentes em TCP intercalam bytes e
corrompem o framing.

### Handshake

Quem conecta manda `Hello`. Quem aceita responde `HelloAck` com o próprio
`HelloPayload`. Só depois disso o estado vira `Connected` e mensagens de chat são
aceitas.

Regras:
- Timeout de 5s para o handshake completar. Estourou, fecha.
- `ProtocolVersion` diferente do esperado → fecha com `Disconnect` e motivo logado.
- Qualquer envelope que não seja `Hello`/`HelloAck` antes do handshake terminar →
  fecha a conexão. Não processe chat de quem não se identificou.

### Keepalive

`Ping` a cada 5s. `Pong` responde ecoando o timestamp original, o que dá o RTT
de graça — exponha em `RoundTripTime` para mostrar latência na UI.

Sem `Pong` por 15s, considere a conexão morta e vá para `Reconnecting`.

### Reconexão

Quando a sessão cai e o peer ainda aparece na descoberta:

- Tentativas com backoff: 1s, 2s, 4s, 8s, depois a cada 15s.
- Máximo de 5 minutos tentando; passou disso, `Closed`.
- Cancele imediatamente se o `IDiscoveryService` disparar `PeerLost` para esse ID —
  não adianta insistir em quem sumiu da rede.
- Ao reconectar, refaça o handshake do zero. Não presuma estado anterior.

### SessionManager

```csharp
public interface ISessionManager : IAsyncDisposable
{
    IReadOnlyDictionary<PeerId, IPeerSession> Sessions { get; }
    event Action<IPeerSession> SessionOpened;
    event Action<PeerId> SessionClosed;

    Task StartListeningAsync(CancellationToken ct);
    Task<IPeerSession> ConnectAsync(DiscoveredPeer peer, CancellationToken ct);
}
```

`StartListeningAsync` sobe um `TcpListener` na `SessionPort` de `NetworkOptions`
(já existe desde a fase 0).

**Trate a conexão simultânea.** Se A e B clicam em conectar ao mesmo tempo, cada
um abre uma conexão e vocês ficam com duas sessões para o mesmo par. Resolva por
regra determinística: quando já existe sessão com aquele `PeerId`, **o lado cujo
`PeerId` tem o menor valor mantém a conexão que ele iniciou; a outra é fechada.**
Ambos os lados aplicam a mesma regra e convergem sem negociação.

---

## 3. Persistência (novo projeto: P2PChat.Storage)

`classlib net8.0`, referencia Core. Pacote: `Microsoft.Data.Sqlite`.

Banco em `%APPDATA%/P2PChat/chat.db`. **Sem ORM** — SQL direto, o schema é
minúsculo e Dapper/EF aqui é peso morto.

```sql
CREATE TABLE IF NOT EXISTS peers (
    peer_id     TEXT PRIMARY KEY,
    nickname    TEXT NOT NULL,
    last_seen   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS messages (
    message_id  TEXT PRIMARY KEY,
    peer_id     TEXT NOT NULL,
    is_outgoing INTEGER NOT NULL,
    text        TEXT NOT NULL,
    sent_at     INTEGER NOT NULL,
    delivered   INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_messages_peer_time ON messages(peer_id, sent_at);
```

Interface:

```csharp
public interface IChatStore
{
    Task SaveMessageAsync(StoredMessage msg);
    Task MarkDeliveredAsync(Guid messageId);
    Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(PeerId peer, int limit = 100);
    Task UpsertPeerAsync(PeerId id, string nickname);
}
```

`message_id` como chave primária resolve deduplicação sozinho: reenvio da mesma
mensagem após reconexão vira um `INSERT OR IGNORE`, não uma linha duplicada.

Migração de schema: guarde `PRAGMA user_version` e aplique scripts incrementais.
Vou mexer nesse schema nas fases seguintes.

---

## 4. Fluxo de envio

1. Usuário digita e envia.
2. Gera `MessageId`, grava no SQLite com `delivered = 0`, aparece na UI
   imediatamente com indicador de "enviando".
3. Envia `ChatMessage` pela sessão.
4. Receptor grava, exibe, responde `ChatAck`.
5. Remetente recebe o ack, chama `MarkDeliveredAsync`, UI troca o indicador.

Se a sessão estiver caída no passo 3, a mensagem fica em `delivered = 0` e é
reenviada automaticamente quando a sessão voltar a `Connected`. Reenvie apenas as
não entregues daquele peer, em ordem de `sent_at`.

Mensagem que nunca entrega não é erro fatal — fica marcada como pendente na UI.

---

## 5. UI (P2PChat.App)

A coluna direita, que na fase 0 era placeholder, agora é a conversa.

- Cabeçalho: nickname do peer, estado da sessão e RTT
  (`Conectado · 12 ms` / `Reconectando…` / `Desconectado`).
- Lista de mensagens: balões alinhados à direita para as minhas, à esquerda para as
  dele. Horário em cada uma. Indicador discreto de entrega nas minhas
  (pendente / entregue).
- Campo de texto no rodapé. Enter envia, Shift+Enter quebra linha.
- Clicar num peer da lista da esquerda abre a conversa e carrega o histórico do
  SQLite **antes** de tentar conectar — quero ler conversas antigas mesmo com o
  peer offline.

Detalhes que mudam a sensação de uso e é fácil esquecer:
- Auto-scroll para o fim ao chegar mensagem nova, **exceto** se o usuário rolou
  para cima manualmente.
- `VirtualizingStackPanel` na lista. Histórico de 100+ mensagens sem virtualização
  trava a UI.
- Foco no campo de texto ao abrir uma conversa.

Mesma regra da fase 0: eventos de sessão chegam em thread de pool, o ViewModel
marshaliza com `Dispatcher`.

---

## 6. Testes

Ampliar `P2PChat.Tests`:

- `FrameWriter` → `FrameReader` em `MemoryStream` preserva o envelope.
- `FrameReader` rejeita prefixo de tamanho acima de `MaxFrameSize` sem alocar o
  buffer.
- `FrameReader` lida com stream que entrega 1 byte por vez (simule com um
  `Stream` customizado). Este teste pega o bug de leitura parcial.
- Handshake completo entre duas `PeerSession` sobre um par de streams em memória.
- Envelope de chat recebido antes do handshake resulta em conexão fechada.
- `IChatStore`: salvar, marcar entregue, ler histórico ordenado; inserir a mesma
  `MessageId` duas vezes não duplica.
- Regra de desempate de conexão simultânea: dados dois `PeerId` fixos, ambos os
  lados escolhem a mesma conexão para manter.

---

## 7. Critério de aceite

Duas instâncias, portas de sessão diferentes:

1. Descoberta funciona (fase 0 intacta).
2. Clicar num peer abre conversa e conecta em menos de 2s.
3. Mensagens trafegam nos dois sentidos, com indicador de entrega mudando.
4. Fechar e reabrir o app preserva o histórico.
5. Desligar o Wi-Fi por 20s: estado vai para "Reconectando", volta sozinho ao
   religar, e mensagens enviadas durante a queda são entregues depois.
6. Enviar 200 mensagens seguidas não trava a UI nem corrompe o framing.

**Teste o item 5 também com o Radmin VPN ativo**, não só na LAN local. É o cenário
real de uso e o comportamento de queda é diferente.

---

## 8. Entrega

Atualize o `README.md` com o fluxo de chat e a localização do banco. Commits
pequenos por etapa: protocolo, framing, sessão, storage, UI.
