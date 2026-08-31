# Fase 0 — Fundação do P2PChat

Você vai criar o esqueleto de um app de comunicação P2P para Windows, estilo Discord,
que conecta máquinas diretamente pela LAN (real ou virtual via Radmin VPN), sem
servidor central.

Esta é a **fase 0 de 6**. O escopo aqui é estritamente: solution, protocolo de
mensagens e descoberta de peers na rede. **Não implemente chat, áudio, vídeo ou
captura de tela** — isso vem nas fases seguintes. Se sentir vontade de adiantar,
não adiante.

---

## 1. Scaffold

Crie na raiz do repositório:

```
P2PChat/
├── P2PChat.Core/      classlib  net8.0          modelos, protocolo, interfaces
├── P2PChat.Net/       classlib  net8.0          descoberta, transporte
├── P2PChat.Media/     classlib  net8.0          vazio nesta fase
└── P2PChat.App/       wpf       net8.0-windows  UI
```

Referências: `Net → Core`, `Media → Core`, `App → Core, Net, Media`.

Pacotes: `MessagePack` no Core, `CommunityToolkit.Mvvm` no App.

**Regra que não pode ser quebrada:** Core, Net e Media têm target `net8.0` puro,
sem `-windows`. Nenhum `using System.Windows` fora do projeto App. Se precisar de
algo da UI dentro do Net, o design está errado — use um evento ou callback.

---

## 2. P2PChat.Core

### Identidade

```csharp
public sealed record PeerId(Guid Value);
```

Serviço `IIdentityStore` com implementação que persiste em
`%APPDATA%/P2PChat/identity.json`:

- No primeiro boot gera um `Guid` novo e um nickname padrão (`"user-" + 4 hex chars`).
- Nos boots seguintes carrega o mesmo arquivo.
- O `PeerId` **nunca** é derivado do IP. O IP muda quando o Radmin entra em cena;
  o ID não pode mudar junto.

### Protocolo

Envelope único serializado com MessagePack:

```csharp
[MessagePackObject]
public sealed class Envelope
{
    [Key(0)] public byte Version { get; init; }        // 1
    [Key(1)] public MessageType Type { get; init; }
    [Key(2)] public Guid SenderId { get; init; }
    [Key(3)] public long TimestampUnixMs { get; init; }
    [Key(4)] public byte[] Payload { get; init; }
}

public enum MessageType : byte
{
    Announce = 1,
    Goodbye  = 2,
}
```

Payload do `Announce`, também MessagePack:

```csharp
[MessagePackObject]
public sealed class AnnouncePayload
{
    [Key(0)] public string Nickname { get; init; }
    [Key(1)] public int SessionPort { get; init; }   // porta TCP, usada na fase 1
}
```

Classe estática `ProtocolCodec` com `Encode(Envelope) → byte[]` e
`TryDecode(ReadOnlySpan<byte>, out Envelope)`. O decode **nunca lança** — retorna
`false` em pacote malformado, versão desconhecida ou tipo desconhecido. Isso é rede
aberta; qualquer coisa pode chegar na porta.

### Modelo de peer

```csharp
public sealed class DiscoveredPeer
{
    public PeerId Id { get; }
    public string Nickname { get; set; }
    public IPAddress Address { get; set; }
    public int SessionPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
```

---

## 3. P2PChat.Net

### Configuração

```csharp
public sealed class NetworkOptions
{
    public int DiscoveryPort { get; init; } = 47100;   // UDP
    public int SessionPort   { get; init; } = 47101;   // TCP, só reservado nesta fase
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout      { get; init; } = TimeSpan.FromSeconds(8);
}
```

Portas vêm de linha de comando (`--discovery-port`, `--session-port`) com fallback
para o default. **Isso é obrigatório**, não opcional: preciso rodar duas instâncias
na mesma máquina para testar.

### DiscoveryService

```csharp
public interface IDiscoveryService : IAsyncDisposable
{
    IReadOnlyCollection<DiscoveredPeer> Peers { get; }
    event Action<DiscoveredPeer> PeerAppeared;
    event Action<DiscoveredPeer> PeerUpdated;
    event Action<PeerId>         PeerLost;
    Task StartAsync(CancellationToken ct);
}
```

Comportamento:

- Socket UDP com `SO_REUSEADDR` **e** `SO_BROADCAST`, bind em `IPAddress.Any`.
- Envia `Announce` a cada `AnnounceInterval` para o broadcast de cada interface
  ativa (ver abaixo).
- Escuta continuamente; para cada `Envelope` válido recebido:
  - ignora se `SenderId` for o próprio,
  - se o peer é novo, adiciona e dispara `PeerAppeared`,
  - se já existe, atualiza `LastSeen`/`Address`/`Nickname` e dispara `PeerUpdated`.
- Timer de varredura remove peers com `LastSeen` mais antigo que `PeerTimeout` e
  dispara `PeerLost`.
- No dispose, envia um `Goodbye` antes de fechar o socket.

**Broadcast por interface — este é o ponto crítico.** Mandar só para
`255.255.255.255` frequentemente não atravessa a adaptadora virtual do Radmin.
Enumere as interfaces com `NetworkInterface.GetAllNetworkInterfaces()`, filtre as
que estão `Up` e não são loopback, e para cada endereço IPv4 unicast calcule o
broadcast dirigido a partir do IP e da máscara (`ip | ~mask`). Envie o announce
para todos eles, mais `255.255.255.255` como rede de segurança. A adaptadora do
Radmin aparece como uma interface normal com IP `26.x.x.x`, então ela entra nessa
lista automaticamente.

### ManualPeerConnector

Método que aceita um `IPAddress` digitado pelo usuário e envia um `Announce`
unicast direto para lá, adicionando o peer se houver resposta.

Existe porque broadcast em rede virtual é imprevisível — algumas configurações do
Radmin filtram. Preciso desse fallback disponível desde o primeiro dia, não depois
de descobrir que a descoberta automática não funciona.

### Concorrência

Callbacks de socket vêm de threads de pool. A coleção interna de peers deve ser
thread-safe (`ConcurrentDictionary`). Os eventos são disparados na thread do
socket — quem marshaliza para a UI é o ViewModel, não o serviço.

---

## 4. P2PChat.App

WPF com MVVM via `CommunityToolkit.Mvvm`. Sem framework de DI adicional —
`Microsoft.Extensions.DependencyInjection` configurado no `App.xaml.cs` basta.

Janela única, layout em duas colunas:

- **Esquerda (~240px):** meu nickname e ID curto no topo; abaixo, `ItemsControl`
  ligado a `ObservableCollection<PeerViewModel>` mostrando nickname, IP e um ponto
  colorido de status. Rodapé com campo de IP + botão "Conectar manualmente".
- **Direita:** placeholder. `TextBlock` centralizado dizendo que a conversa chega
  na fase 1. Não invente uma UI de chat aqui.

O `MainViewModel` assina os eventos do `IDiscoveryService` e faz o marshal para a
thread de UI com `Dispatcher.Invoke`. Os eventos **não** chegam na thread certa
sozinhos; se você esquecer isso o app quebra de forma intermitente.

Barra de status no rodapé mostrando as portas em uso e a contagem de peers ativos.

---

## 5. Testes

Projeto xUnit `P2PChat.Tests` cobrindo:

- Round-trip do `ProtocolCodec` (encode → decode preserva todos os campos).
- `TryDecode` retorna `false` para: array vazio, bytes aleatórios, versão 99,
  `MessageType` inexistente. Nenhum desses caso pode lançar exceção.
- `IdentityStore` gera ID novo em diretório limpo e recarrega o mesmo em segunda
  chamada.
- Cálculo do endereço de broadcast: dado IP `192.168.1.50` e máscara
  `255.255.255.0`, resultado `192.168.1.255`. Inclua um caso com `26.x.x.x` e
  máscara `/8`.

Descoberta em si não precisa de teste automatizado — é validação manual.

---

## 6. Critério de aceite

Rode duas instâncias na mesma máquina:

```
dotnet run --project P2PChat.App -- --discovery-port 47100 --session-port 47101
dotnet run --project P2PChat.App -- --discovery-port 47100 --session-port 47201
```

Cada uma deve aparecer na lista da outra em até 4 segundos, com nickname e IP
corretos. Fechar uma delas deve removê-la da lista da outra em até 8 segundos.

Se isso funciona, a fase 0 está pronta.

---

## 7. Entrega

Escreva também um `README.md` na raiz com: como buildar, como rodar duas
instâncias, quais portas o app usa e uma nota sobre liberar essas portas no
Firewall do Windows na primeira execução (o Windows vai perguntar; se o usuário
clicar em "Cancelar" a descoberta silenciosamente não funciona — vale um aviso).

Faça commits pequenos e nomeados por etapa, não um commit único no final.
