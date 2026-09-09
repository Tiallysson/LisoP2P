# Fase 6 — Acabamento

Continuação do P2PChat. As fases anteriores entregaram todo o funcional: descoberta
(0), sessão e chat (1), captura/encode de tela (2), transmissão de vídeo (3), voz
(4) e grupo em mesh (5). O app já faz o que se propõe a fazer.

Esta fase não adiciona capacidade nova — **fecha as arestas que ficaram soltas de
propósito** ao longo das fases anteriores para não travar o cronograma: telas de
configuração reais em vez de defaults fixos, identidade com verificação visível,
tratamento de erro que não seja exceção crua na tela, e um artefato que alguém
consiga instalar sem ter o SDK do .NET.

Não é uma fase de "polimento infinito". Trate os itens abaixo como a lista
fechada de escopo, não como ponto de partida para inventar mais.

---

## 1. Tela de Configurações (P2PChat.App)

Até aqui, monitor/resolução/dispositivo de áudio/portas viviam espalhados: a aba
de teste de captura da fase 2 tinha um dropdown de monitor solto, as portas
vinham só de flag de linha de comando desde a fase 0, o dispositivo de áudio da
fase 4 não tinha seletor persistente. Esta fase consolida tudo numa tela só,
persistida.

### Modelo

```csharp
[MessagePackObject]
public sealed class AppSettings
{
    [Key(0)]  public int DiscoveryPort { get; set; } = 47100;
    [Key(1)]  public int SessionPort   { get; set; } = 47101;
    [Key(2)]  public int MediaPort     { get; set; } = 47102;
    [Key(3)]  public int PreferredMonitorIndex { get; set; } = 0;
    [Key(4)]  public string CaptureResolution { get; set; } = "Native"; // ou "1920x1080" etc.
    [Key(5)]  public int CaptureFps { get; set; } = 30;
    [Key(6)]  public string? AudioInputDeviceId { get; set; }   // null = default do sistema
    [Key(7)]  public string? AudioOutputDeviceId { get; set; }
    [Key(8)]  public AudioCaptureMode AudioMode { get; set; } = AudioCaptureMode.Microphone;
    [Key(9)]  public bool PushToTalkEnabled { get; set; } = true;
    [Key(10)] public string PushToTalkKey { get; set; } = "LeftCtrl";
    [Key(11)] public string Nickname { get; set; } = "";
}
```

Persistido em `%APPDATA%/P2PChat/settings.json`, separado do `identity.json` da
fase 0 — identidade é imutável por natureza (é a chave), configurações mudam a
qualquer momento.

**As portas de linha de comando da fase 0 continuam funcionando e têm
prioridade sobre o arquivo de configurações** — isso preserva o critério de
aceite da fase 0/1 que dependia de `--discovery-port`/`--session-port` para
rodar duas instâncias na mesma máquina em teste manual. Ordem de resolução:
flag de linha de comando > `settings.json` > default do código.

### UI

Janela de configurações com abas ou seções:

- **Geral**: nickname (o mesmo usado no `HelloPayload` da fase 1 — mudar aqui
  reflete na próxima conexão, não retroativamente nas já abertas).
- **Vídeo**: dropdown de monitor (reusa `IScreenCapture.AvailableMonitors` da
  fase 2), resolução, FPS alvo. Mostra a tabela de bitrate por degrau da fase 5
  como referência somente leitura, não editável nesta fase.
- **Áudio**: dropdown de dispositivo de entrada e saída (enumere via NAudio,
  `MMDeviceEnumerator`), modo (microfone / loopback / ambos), tecla de PTT com
  captura de tecla ao clicar num botão "definir" (grava o próximo keydown).
- **Rede**: as três portas, com validação de range (1024-65535) e aviso inline
  se a porta já está em uso no momento de salvar (tente bind rápido e libere,
  só para checar).
- **Identidade**: seção descrita na próxima parte.

Mudança de porta exige reiniciar os serviços de rede (`IDiscoveryService`,
`ISessionManager`, `IMediaReceiver`) — não precisa reiniciar o processo inteiro,
mas avise na UI que sessões ativas serão encerradas ao aplicar. Aplique só ao
confirmar, não em tempo real por campo.

---

## 2. Identidade e fingerprint (P2PChat.Core)

A fase 0 já gera um `Guid` como `PeerId`. Isso nunca foi uma identidade
criptográfica de verdade — era só um identificador estável. Esta fase corrige
isso.

### Troca de GUID por par de chaves

```csharp
public interface IIdentityStore
{
    PeerIdentity Current { get; }
}

public sealed class PeerIdentity
{
    public byte[] PublicKey { get; init; }    // Ed25519, 32 bytes
    public string Fingerprint { get; init; }  // derivado do PublicKey, formato abaixo
    public string Nickname { get; set; }
}
```

Use `System.Security.Cryptography` (suporte a Ed25519 nativo no .NET 8+ via
`SignatureAlgorithm`/`AsymmetricAlgorithm`, ou pacote `NSec.Cryptography` se a
API nativa exigir mais boilerplate que o razoável). Chave privada persistida
com `ProtectedData` (DPAPI do Windows) em vez de texto plano em
`identity.json` — é local ao usuário do Windows, não precisa de senha adicional,
mas não deixe a chave privada em arquivo legível por qualquer processo.

**Migração**: quem já tem `identity.json` da fase 0 com só `Guid` + nickname
perde a identidade antiga ao atualizar — isso é aceitável neste estágio do
projeto (não há usuários em produção ainda), mas documente no README como
breaking change. Gere novo par de chaves na primeira execução pós-atualização.

`PeerId` em todo o código das fases 0-5 (usado como chave de dicionário,
campo em mensagens, etc.) passa a ser derivado do `PublicKey`, não mais um GUID
aleatório — mesmo formato de uso (`record` comparável, serializável), origem
diferente:

```csharp
public sealed record PeerId(byte[] PublicKeyBytes)
{
    public override string ToString() => Convert.ToHexString(PublicKeyBytes)[..16];
}
```

### Fingerprint

Formato de exibição: hash SHA-256 da chave pública, apresentado em grupos de 4
caracteres hex separados por espaço, estilo comparação manual (mesmo padrão
usado por apps de mensagem para verificação de segurança):

```
A1B2 C3D4 E5F6 ... (16 grupos, 64 caracteres)
```

Exibido em dois lugares:
- Configurações → Identidade: seu próprio fingerprint, com botão de copiar.
- Ao conectar com um peer pela primeira vez (handshake `Hello`/`HelloAck` da
  fase 1): mostra o fingerprint dele numa notificação não-bloqueante, algo como
  "Conectado com `nickname` — fingerprint `A1B2 C3D4...`". **Não bloqueie a
  conexão esperando confirmação manual** — isso é verificação por transparência
  (o usuário pode conferir por fora, ex. lendo em voz alta numa chamada), não
  um gate de aprovação. Adicionar um fluxo de "confirmar identidade" bloqueante
  é escopo de segurança maior que esta fase cobre.

Não é assinatura de mensagens nesta fase — isso seria expandir escopo
consideravelmente (assinar cada envelope, verificar em cada recepção). O
par de chaves aqui serve para dar uma identidade estável e verificável
visualmente; autenticação criptográfica de cada mensagem fica documentada como
possível fase futura, não implementada agora.

---

## 3. Tratamento de erro (P2PChat.App, transversal)

Até aqui, exceção não tratada em qualquer camada provavelmente derruba o app ou
aparece como stack trace cru. Esta fase estabelece uma política consistente:
**erro de rede nunca é modal bloqueante, sempre é estado visível.**

### Categorias e resposta esperada

| Situação | Onde já existe o sinal (fases anteriores) | Como aparece agora |
|---|---|---|
| Peer cai durante chat | `SessionState → Reconnecting/Closed` (fase 1) | Cabeçalho da conversa já mostrava estado — garanta que "Reconectando" e "Desconectado" têm cor/ícone distintos, não só texto |
| Peer cai durante compartilhamento de tela | Sessão TCP cai, `ScreenShareStop` implícito (fase 3) | Área de vídeo mostra overlay "Conexão perdida com `nickname`" em vez de congelar no último frame ou piscar preto |
| Peer cai durante chamada de voz | Sessão cai, áudio para de chegar | Indicador de fala do peer apaga, ícone de mic dele fica acinzentado com tooltip "desconectado" |
| Falha ao iniciar captura de tela (GPU sem suporte, driver quebrado) | `IScreenCapture` já reportava isso na fase 2 | Diálogo (não crash) explicando a falha específica retornada, com sugestão básica ("verifique se há atualização de driver de vídeo disponível") |
| Ambos os encoders (hardware e software) falham | `VideoEncoderFactory` (fase 2) | Compartilhar tela fica desabilitado com tooltip explicando; resto do app continua usável |
| Porta já em uso ao iniciar | `TcpListener`/socket UDP lançam na fase 0/1 | Tela inicial impede de prosseguir, mostra qual porta conflitou, com atalho direto para Configurações → Rede |
| Reconexão esgota as 5 tentativas (fase 1) | Já existia o limite, faltava reação de UI | Sessão marcada como `Closed` mostra botão "Tentar novamente" manual na conversa, não desaparece silenciosamente |
| Sala perde maioria dos membros (fase 5) | Gossip já detecta saída | Sem tratamento especial — é só a lista de membros encolhendo, já coberto pela fase 5 |

### Padrão de implementação

Não é uma reescrita de cada camada — é garantir que os eventos que **já
existem** desde as fases anteriores (`StateChanged`, `PeerLost`, exceptions de
inicialização de captura/encoder) tenham um assinante na camada de UI que
traduza em algo visível, em vez de serem só logados ou ignorados.

Adicione um `IErrorPresenter` simples na App:

```csharp
public interface IErrorPresenter
{
    void ShowTransient(string message, ErrorSeverity severity);  // toast, some sozinho
    void ShowPersistent(string message, string? actionLabel, Action? action); // fica até resolver
}
```

`ShowTransient` para coisas que se resolvem sozinhas (reconectando).
`ShowPersistent` para coisas que exigem ação do usuário (porta em uso, encoder
indisponível). Nenhuma delas é um `MessageBox` modal que trava a thread de UI —
isso é o anti-padrão específico que esta seção existe para evitar.

### O que não entra nesta fase

Não implemente telemetria, relatório de erro remoto, ou log estruturado
exportável. Log em arquivo local simples (`%APPDATA%/P2PChat/app.log`, rotação
por tamanho) é suficiente — é para você depurar durante uso real, não para
suporte a usuários externos.

---

## 4. Empacotamento

### Formato

**Single-file executable** via publish do .NET, não instalador MSI/Setup
completo — para este estágio (uso pessoal/entre amigos, não distribuição
ampla), um `.exe` que roda sem instalação prévia é suficiente e muito mais
simples de manter que um instalador.

```
dotnet publish P2PChat.App -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

`--self-contained true` é obrigatório: quem vai rodar o app não necessariamente
tem o .NET 8 runtime instalado, e não faz sentido pedir isso para um projeto
pessoal.

### Verificações antes de publicar

- Confirme que `Vortice.Windows`, `Concentus`, `NAudio` e o binding de
  OpenH264/Media Foundation sobrevivem ao trim/single-file — bibliotecas com
  interop nativo às vezes precisam de exclusão explícita do trimming
  (`<TrimmerRootAssembly>` no `.csproj`) ou de `IncludeNativeLibrariesForSelfExtract`
  para não quebrar em runtime silenciosamente. Teste o `.exe` publicado numa
  máquina limpa, sem SDK do .NET instalado, não só na máquina de
  desenvolvimento.
- `%APPDATA%/P2PChat/` (identity, settings, chat.db, log) precisa existir e ser
  criado no primeiro boot se não existir — confirme que isso não dependia
  implicitamente de rodar via `dotnet run` alguma vez antes.

### Primeira execução

Tela simples de boas-vindas na primeira vez que `identity.json`/chave nova é
gerada: nickname inicial, aviso sobre liberar portas no Firewall do Windows
(o mesmo aviso que o README já tinha desde a fase 0, agora também na UI, já que
nem todo mundo lê o README). Não é wizard de múltiplas telas — uma tela, alguns
campos, botão "Começar".

### Ícone e metadata

Ícone `.ico` simples (não é o foco do projeto, algo básico serve), e
preenchimento de `AssemblyInfo`/`PublishProfile` com nome do produto, versão,
descrição — para o `.exe` não aparecer com metadata em branco no Explorer/Task
Manager.

---

## 5. Testes

Esta fase tem menos superfície testável por unidade que as anteriores — é
majoritariamente integração/UI/empacotamento. O que vale cobrir:

- Serialização round-trip de `AppSettings` (mesmo padrão de teste das fases
  anteriores para os payloads MessagePack).
- Ordem de precedência de configuração: flag de linha de comando > arquivo >
  default — teste com combinações das três fontes presentes/ausentes.
- Geração de fingerprint: mesma chave pública sempre produz o mesmo fingerprint
  formatado; chaves diferentes produzem fingerprints diferentes (sem colisão
  nos casos de teste, óbvio, mas confirme o formato de agrupamento de 4
  caracteres está correto).
- `PeerId` derivado de `PublicKeyBytes`: igualdade e hash code consistentes
  (importante — isso é usado como chave de `Dictionary`/`ConcurrentDictionary`
  em várias camadas desde a fase 1, uma implementação de `Equals`/`GetHashCode`
  incorreta quebra silenciosamente comparações e lookups).

---

## 6. Critério de aceite

1. `.exe` publicado roda numa máquina Windows limpa (sem SDK .NET) e abre
   direto na tela de primeira execução.
2. Configurações → Rede: mudar a porta de sessão, aplicar, e confirmar que uma
   nova conexão de outro peer usa a porta nova (a antiga não aceita mais
   conexão).
3. Configurações → Áudio: trocar dispositivo de saída aplicado imediatamente,
   sem precisar reiniciar o app.
4. Fingerprint exibido em Configurações bate com o mostrado no peer quando
   ele se conecta (compare visualmente entre as duas instâncias).
5. Derrubar a rede do peer durante compartilhamento de tela mostra o overlay de
   "conexão perdida" em até a janela de timeout já estabelecida (fase 1/3), não
   trava a área de vídeo no último frame indefinidamente.
6. Iniciar duas instâncias com a mesma porta de sessão (conflito proposital) —
   a segunda mostra a tela de erro de porta em uso, com atalho para
   Configurações, em vez de crashar ou falhar silenciosamente.
7. Log em `app.log` contém entradas legíveis dos eventos de erro acima —
   suficiente para você depurar um problema relatado sem precisar reproduzir do
   zero.

Se os 7 itens passam, o cronograma das 6 fases está completo.

---

## 7. Entrega

Atualize `README.md` com uma seção final: como publicar o single-file
executable, checklist de teste em máquina limpa, localização de todos os
arquivos de dados (`%APPDATA%/P2PChat/*`), e uma nota clara sobre a mudança de
identidade (quem tinha `identity.json` de fases anteriores precisa saber que a
chave é nova). Adicione também uma seção "Limitações conhecidas" consolidando o
que já foi documentado nas fases 2, 3 e 5 (DRM na captura, teto do mesh em ~3-4
pessoas, ausência de assinatura de mensagem) num único lugar, para não ficar
espalhado em READMEs de fase que não existem mais como arquivos separados.

Commits pequenos: modelo e persistência de `AppSettings`, tela de configurações,
troca de identidade para par de chaves, fingerprint na UI, `IErrorPresenter` e
seus pontos de uso, publish single-file e verificação em máquina limpa.
