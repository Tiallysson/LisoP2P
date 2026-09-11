# Front-end WPF — layout estilo Discord

Este prompt implementa a **camada visual** do P2PChat.App conforme o wireframe
já aprovado (rail de salas, painel de canal/voz, chat central com área de vídeo,
painel de membros). Não é uma fase nova de funcionalidade — é a reestruturação
da UI que hoje está espalhada entre a janela simples da fase 1, a aba de teste
da fase 2 e a integração ad-hoc da fase 3, consolidada no layout final que vai
receber tudo que as fases 0-6 já produziram.

**Não implemente lógica de rede, protocolo ou persistência aqui.** Toda a
camada de `Core`/`Net`/`Media`/`Storage` já existe. Este prompt é
exclusivamente XAML, `UserControl`s, `DataTemplate`s, conversores e os
ViewModels que já deveriam existir (ou precisam de pequeno ajuste) para
alimentar essa UI. Se durante a implementação faltar um evento ou propriedade
em algum ViewModel das fases anteriores, adicione o mínimo necessário — não
redesenhe a camada de domínio.

---

## 1. Estrutura de janela

`MainWindow` vira um `Grid` de 4 colunas fixas + flexível, substituindo o
layout de duas colunas da fase 0/1:

```
[ Rail 56px ] [ Painel de sala 184px ] [ Chat central * ] [ Membros 168px ]
```

```xml
<Grid>
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="56" />
    <ColumnDefinition Width="184" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="168" />
  </Grid.ColumnDefinitions>
  <local:RoomRailView Grid.Column="0" />
  <local:RoomSidebarView Grid.Column="1" />
  <local:ChatView Grid.Column="2" />
  <local:MemberPanelView Grid.Column="3" />
</Grid>
```

A coluna de membros (168px) tem `Visibility` ligada a
`MainViewModel.IsMemberPanelVisible` — some quando não há sala ativa (conversa
1:1 direta, herdada da fase 1, continua existindo e não mostra esse painel).

Redimensionamento: janela com `MinWidth="820"`, `MinHeight="480"`. Nenhuma
coluna encolhe abaixo do valor fixo — só a coluna central (`*`) respira.

---

## 2. Tema — recursos compartilhados

Crie `Themes/DiscordDark.xaml` como `ResourceDictionary` mesclado no
`App.xaml`. Centralize aqui toda cor/espaçamento usado nas views — nenhuma
`UserControl` deve ter cor hardcoded solta em `Background="#..."` fora deste
arquivo.

### Paleta (referência do mockup, adapte tom se necessário)

```xml
<SolidColorBrush x:Key="Surface0" Color="#1A1A1A" />   <!-- rail -->
<SolidColorBrush x:Key="Surface1" Color="#232323" />   <!-- sidebar, membros -->
<SolidColorBrush x:Key="Surface2" Color="#2B2B2B" />   <!-- chat central -->
<SolidColorBrush x:Key="Surface3" Color="#1E1E1E" />   <!-- área de vídeo -->
<SolidColorBrush x:Key="BorderSubtle" Color="#38383A" />
<SolidColorBrush x:Key="TextPrimary" Color="#F2F2F2" />
<SolidColorBrush x:Key="TextSecondary" Color="#A8A8A8" />
<SolidColorBrush x:Key="TextMuted" Color="#6E6E6E" />
<SolidColorBrush x:Key="AccentBlue" Color="#378ADD" />
<SolidColorBrush x:Key="StatusSuccess" Color="#3BA55D" />
<SolidColorBrush x:Key="StatusWarning" Color="#E8A33D" />
<SolidColorBrush x:Key="StatusDanger" Color="#D64545" />
```

Essas cores mapeiam diretamente para estados que já existem no domínio — não
são decorativas soltas:

| Cor | Estado que representa | Origem |
|---|---|---|
| `StatusSuccess` | `SessionState.Connected`, `RoomMember` online | fase 1 |
| `StatusWarning` | `SessionState.Reconnecting` | fase 1 |
| `StatusDanger` | `SessionState.Closed` | fase 1 |
| `AccentBlue` | usuário atual, elementos de destaque/mensagem própria | — |

Estilos base: `Style` para `TextBlock` (tamanhos 11/12/13/14, conforme o
mockup), `Style` para os "chips" de avatar circular (usar `Ellipse` com
`ImageBrush` ou `Border` com `CornerRadius` igual à metade da largura), e um
`Style` de item de lista selecionável (hover + seleção) reaproveitado no rail,
na lista de salas e na lista de membros — não duplique esse estilo três vezes.

---

## 3. RoomRailView (56px)

```csharp
public sealed class RoomRailViewModel : ObservableObject
{
    public ObservableCollection<RoomSummaryViewModel> Rooms { get; }
    public RoomSummaryViewModel? SelectedRoom { get; set; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand CreateRoomCommand { get; }
}

public sealed class RoomSummaryViewModel : ObservableObject
{
    public RoomId Id { get; }
    public string Initials { get; }     // derivado do nome da sala, 2 letras
    public bool HasUnread { get; set; }
}
```

`ItemsControl` com `ItemsPanel` em `StackPanel` vertical, `ItemTemplate`
mostrando o círculo de iniciais. Item selecionado ganha indicador lateral
(barra vertical de 4px na borda esquerda do item, característica visual do
Discord) — use um `Border` fino posicionado à esquerda do círculo, não
`BorderThickness` no próprio avatar.

Botão "+" abaixo da lista dispara `CreateRoomCommand` → abre o fluxo de criar
sala já especificado na fase 5 (convite). Ícone de engrenagem fixo no rodapé
do rail (não rola junto com a lista de salas) abre a janela de Configurações
da fase 6.

---

## 4. RoomSidebarView (184px)

Dois blocos empilhados verticalmente num único `DockPanel`/`Grid` de linhas:

**Cabeçalho da sala**: nome da sala atual, `TextTrimming="CharacterEllipsis"`
se não couber.

**Bloco "canal de texto"**: item único fixo representando o chat da sala
(não é uma lista extensível — o protocolo da fase 5 não modela múltiplos
canais de texto, não invente essa capacidade na UI). Estilo de item
selecionável igual ao usado no rail.

**Bloco "voz — N"**: cabeçalho com contagem dinâmica
(`{Binding VoiceMembers.Count}`), seguido de `ItemsControl` de
`RoomMemberViewModel` (mesmo view model do painel de membros à direita —
**não duplique a classe**, é a mesma lista de sala em duas apresentações
diferentes: aqui compacta, no painel direito com mais detalhe).

```csharp
public sealed class RoomMemberViewModel : ObservableObject
{
    public PeerId Id { get; }
    public string Nickname { get; }
    public string Initials { get; }
    public SessionState ConnectionState { get; set; }
    public bool IsSpeaking { get; set; }
    public bool IsSharingScreen { get; set; }
    public bool IsCurrentUser { get; }
}
```

Item de membro nesta lista compacta: avatar 22px, nome, ícone de
compartilhamento (`ti-screen-share` equivalente — ver seção 8 sobre ícones) se
`IsSharingScreen`, sem indicador de status de conexão detalhado (isso fica
para o painel da direita).

**`IsSpeaking` → anel colorido no avatar.** Implemente como `Border` de 2px
`StatusSuccess` ao redor do círculo de avatar, com `Visibility` ligada a
`IsSpeaking` via `BooleanToVisibilityConverter`. **Não anime com
`DoubleAnimation`/pulsar** — troca de `Visibility` é suficiente, o sinal já
chega da fase 5 no ritmo de start/stop de fala, uma animação contínua gastaria
ciclo de CPU à toa num elemento que pisca dezenas de vezes por minuto numa
chamada longa.

Rodapé fixo (não rola): card do usuário atual com avatar, nickname, e três
ícones de ação — mic (toggle mute, ligado ao mesmo comando de PTT/mute da fase
4), headphone (toggle deafen — **novo**, silencia a saída de áudio local sem
parar de transmitir a própria voz; adicione esse comando ao ViewModel de áudio
existente da fase 4 se ainda não existir) e engrenagem (atalho para
Configurações).

Status de conexão do próprio usuário (`Conectado · 14 ms`) já existe como
propriedade calculada a partir de `RoundTripTime` — reaproveite o mesmo padrão
de exibição de RTT da conversa 1:1 da fase 1, não recrie o cálculo.

---

## 5. ChatView (coluna central, flexível)

`Grid` de 4 linhas: cabeçalho, área de vídeo (colapsável), lista de mensagens
(`*`), campo de entrada.

### Cabeçalho

Nome da sala/conversa, indicador "`{Nickname}` compartilhando" quando
`IScreenShareSession` está ativo — `Visibility` condicional, não deixe o
espaço reservado vazio quando ninguém compartilha.

### Área de vídeo

```xml
<Border x:Name="VideoContainer" Background="{StaticResource Surface3}"
        CornerRadius="8" Margin="12,12,12,4" Height="220"
        Visibility="{Binding IsScreenShareActive, Converter={StaticResource BoolToVis}}">
  <Grid>
    <Image Source="{Binding ScreenShareD3DImage}" Stretch="Uniform" />
    <TextBlock Text="{Binding ScreenShareStatusText}"
               Foreground="{StaticResource TextMuted}"
               HorizontalAlignment="Center" VerticalAlignment="Center"
               Visibility="{Binding IsScreenShareFrameStale, Converter={StaticResource BoolToVis}}" />
  </Grid>
</Border>
```

`ScreenShareD3DImage` é o `D3DImage` populado pelo pipeline da fase 3 — este
prompt só faz o binding, a superfície em si já existe desde então. O
`TextBlock` de status sobreposto é o overlay de "conexão perdida" especificado
no tratamento de erro da fase 6 — reaproveite `IErrorPresenter`/o estado já
exposto, não crie um novo mecanismo de detecção de queda aqui.

Altura fixa de 220px nesta view (o mockup usava 180px num container menor);
ajuste proporcional ao `Height` real da janela é refinamento visual futuro,
não bloqueador deste prompt.

### Lista de mensagens

`ItemsControl` com `VirtualizingStackPanel` (`VirtualizingPanel.IsVirtualizing`
`= True`, `VirtualizingPanel.VirtualizationMode="Recycling"`) — a fase 1 já
exigia isso para histórico longo, mantenha aqui.

`DataTemplate` de mensagem: avatar do remetente (26px) + coluna com
nome+horário na primeira linha, texto abaixo. Mensagem do usuário atual usa
`AccentBlue` no avatar/nome; mensagens de outros membros usam a cor neutra do
tema. **Mostre o nome do remetente em toda mensagem** — diferente da conversa
1:1 da fase 1, onde isso era implícito pelo lado do balão, em sala isso é
obrigatório (já especificado na fase 5).

Indicador de entrega (pendente/entregue) da fase 1 continua existindo como
ícone pequeno após o horário, só nas mensagens do próprio usuário.

Auto-scroll: reaproveite o comportamento já especificado na fase 1 (rola para
o fim em mensagem nova, exceto se o usuário rolou manualmente para cima) —
comportamento idêntico, só a `UserControl` mudou.

### Campo de entrada

`TextBox` multiline com `AcceptsReturn="True"`, `Enter` sem modificador
dispara envio (`InputBinding` de `KeyBinding` com `Key="Return"` e um
`CommandParameter` que distingue de `Shift+Enter`, que insere quebra de
linha). Placeholder via `TextBlock` sobreposto com `Visibility` condicional a
`Text.Length == 0` (WPF não tem placeholder nativo de `TextBox`).

---

## 6. MemberPanelView (168px)

Lista completa de `RoomMemberViewModel` da sala (mesma coleção da seção 4,
apresentação mais detalhada):

- Avatar 28px com indicador de status (bolinha 9px sobreposta no canto
  inferior direito): verde para `Connected`, amber para `Reconnecting`,
  cinza/`TextMuted` para `Closed`.
- Nome, e uma segunda linha condicional: "compartilhando" (`AccentBlue`) se
  `IsSharingScreen`, "reconectando" (`StatusWarning`) se
  `ConnectionState == Reconnecting`, ausente caso contrário — nunca as duas ao
  mesmo tempo, priorize `Reconnecting` sobre `IsSharingScreen` se ambos forem
  tecnicamente possíveis (compartilhamento não sobrevive à queda de sessão,
  fase 3, então na prática não deveria colidir — mas trate a prioridade
  explicitamente no `DataTrigger`/conversor mesmo assim).

Ordenação da lista: `IsCurrentUser` não teve tratamento especial no mockup,
mantenha assim — usuário atual aparece na posição natural da lista, não
fixado no topo.

---

## 7. Conversores necessários (`P2PChat.App/Converters`)

- `SessionStateToBrushConverter` — mapeia `SessionState` para os brushes de
  status da seção 2. Um único conversor reaproveitado no rodapé do usuário
  (seção 4) e no painel de membros (seção 6), não implemente a lógica de cor
  duas vezes.
- `NicknameToInitialsConverter` — primeiras letras do nickname (mesma lógica
  para salas e para membros, um conversor só).
- `BoolToVisibilityConverter` — padrão, mas confirme que já não existe um
  equivalente antes de criar (frameworks MVVM às vezes já trazem um).
- `SpeakingToBorderBrushConverter` (ou `DataTrigger` direto no XAML, sem
  conversor — prefira `DataTrigger` aqui, é mais simples que criar classe para
  isso).

---

## 8. Ícones

Não há dependência de fonte de ícones no projeto ainda. Duas opções, escolha
uma e documente no README:

1. **`Segoe Fluent Icons`/`Segoe MDL2 Assets`** (já instalada no Windows 10/11,
   zero dependência nova) — use `FontFamily="Segoe Fluent Icons"` com os
   glyphs Unicode correspondentes (microfone, headset, engrenagem,
   compartilhamento de tela, usuários, mais). É a opção mais simples de
   empacotar (fase 6, single-file) porque não adiciona arquivo de fonte.
2. Pacote de ícones vetoriais (`Material.Icons.WPF` ou similar) se preferir
   consistência visual entre Windows 10 e 11.

Nesta implementação, **prefira a opção 1** — o projeto já tem dependências
nativas suficientes (Vortice, Concentus, NAudio) para o empacotamento da fase
6 se preocupar; evitar mais uma reduz risco ali.

---

## 9. O que NÃO fazer nesta tarefa

- Não implemente múltiplos canais de texto por sala.
- Não implemente grade de vídeo com múltiplos compartilhamentos simultâneos —
  a regra de "um por vez" da fase 5 continua valendo, a UI só precisa refletir
  isso (botão desabilitado com tooltip, já especificado).
- Não crie um novo sistema de notificação/toast — reaproveite
  `IErrorPresenter` da fase 6.
- Não implemente drag-and-drop de arquivos, emoji picker, ou reações a
  mensagem — fora do escopo do projeto inteiro, não só desta tarefa.
- Não mude nada em `Core`/`Net`/`Media`/`Storage`. Se um ViewModel precisar de
  uma propriedade nova que só existe como campo privado hoje, exponha; não
  redesenhe a interface pública dessas camadas.

---

## 10. Critério de aceite

1. Janela abre no novo layout de 4 colunas, sem regressão nas conversas 1:1
   da fase 1 (que continuam acessíveis, só sem o painel de membros).
2. Entrar numa sala com 3 peers conectados (setup manual de teste da fase 5)
   mostra os 3 no painel de voz da sidebar e no painel de membros da direita,
   com dados consistentes entre as duas listas (mesmo `RoomMemberViewModel`).
3. Pressionar PTT em qualquer instância acende o anel verde no avatar
   correspondente nas outras instâncias em tempo real.
4. Iniciar compartilhamento de tela mostra a imagem na área de vídeo e o badge
   "compartilhando" no cabeçalho, sidebar e painel de membros simultaneamente.
5. Derrubar a rede de um peer (mesmo teste da fase 6) reflete
   `Reconnecting`/`Closed` nos três lugares onde o status aparece (rodapé
   próprio se for o peer local, sidebar, painel de membros) sem exigir reload
   manual da tela.
6. Redimensionar a janela abaixo de `MinWidth` não é possível; acima disso,
   só a coluna de chat cresce.
7. Nenhuma cor hardcoded fora de `DiscordDark.xaml` — grep rápido por
   `Color="#` ou `Background="#` fora do arquivo de tema não deve retornar
   nada nas novas views.

---

## 11. Entrega

Atualize `README.md` com uma captura de tela (ou descrição, se captura não for
viável no ambiente) do novo layout e a decisão tomada na seção 8 sobre fonte
de ícones. Commits pequenos: tema/recursos compartilhados, RoomRailView,
RoomSidebarView, ChatView, MemberPanelView, conversores, integração final no
MainWindow.
