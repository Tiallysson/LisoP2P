# LisoP2P

App de comunicação P2P para Windows, estilo Discord, que conecta máquinas
diretamente pela LAN (real ou virtual via Radmin VPN), sem servidor central.

Este repositório está na **fase 0 de 6**: solution, protocolo de mensagens e
descoberta de peers na rede. Chat, áudio, vídeo e captura de tela vêm nas
fases seguintes — veja `LisoP2P.App/Docs/fase0-prompt.md` para a especificação
completa desta fase.

## Estrutura

- `LisoP2P.Core` — identidade de peer, protocolo de mensagens (MessagePack).
- `LisoP2P.Net` — descoberta de peers via broadcast UDP, conector manual.
- `LisoP2P.Media` — reservado para fases futuras, vazio nesta fase.
- `LisoP2P.App` — interface WPF (MVVM).
- `LisoP2P.Tests` — testes de unidade (xUnit).

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

Cada instância deve aparecer na lista de peers da outra em poucos segundos.
Fechar uma delas deve removê-la da lista da outra em até 8 segundos.

## Testes

```
dotnet test
```

## Portas usadas

- **47100/UDP** — descoberta de peers (broadcast), configurável via
  `--discovery-port`.
- **47101/TCP** — sessão (reservada nesta fase, ainda não usada), configurável
  via `--session-port`.

## Firewall do Windows

Na primeira execução, o Windows deve perguntar se permite que o LisoP2P se
comunique em redes privadas/públicas. **Isso precisa ser aceito** — se você
clicar em "Cancelar", a descoberta automática de peers para de funcionar
silenciosamente, sem nenhum erro visível na interface. Se isso acontecer,
libere o app manualmente em
`Configurações do Windows > Rede e Internet > Firewall do Windows Defender >
Permitir um aplicativo através do firewall`.
