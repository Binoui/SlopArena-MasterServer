# Architecture Decision Records

This repo does **not** host the ADR series. ADRs live in the main SlopArena repo:

- **Series index:** <https://github.com/Binoui/SlopArena/tree/main/docs/adr>
- **ADR-0004 — Master Server Manages Lobbies via SignalR:** <https://github.com/Binoui/SlopArena/blob/main/docs/adr/0004-master-server-signalr-lobby.md>
- **ADR-0008 — Lobby Room Match Flow:** <https://github.com/Binoui/SlopArena/blob/main/docs/adr/0008-lobby-room-match-flow.md>

## ADRs cited in this repo

| ADR | Title | Cited in |
| --- | --- | --- |
| ADR-0004 | Master Server Manages Lobbies via SignalR (in-memory lobby authority; lobbies ephemeral) | `Hubs/LobbyHub.cs`, `Lobbies/LobbyManager.cs`, `Lobbies/LobbyModels.cs` |
| ADR-0008 | Lobby Room Match Flow (game server stateless between matches; HTTP match start, UDP match port) | `Hubs/LobbyHub.cs`, `Lobbies/HttpMatchLauncher.cs` |

When code comments cite an ADR, resolve them here. Do not duplicate or renumber the series in this repo — it stays in the main repo.
