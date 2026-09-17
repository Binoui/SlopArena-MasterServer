# SlopArena Master Server

Backend API for SlopArena: guest sessions, game server registration, the server browser, SignalR lobbies, and game-wide chat.
Matchmaking is planned but **not yet implemented**; the server browser is the current entry point.

## Scope

Runs the SlopArena pre-match flow: players self-select a fresh, non-full game server from the
browser, join its lobby, and the host starts the match. No matchmaking exists yet — there is no
queue, no pairing, and no skill/MMR matching (`User.Mmr` is stored but never used).

| Endpoint | Purpose |
| --- | --- |
| `POST /auth/guest`, `GET /auth/me` | Guest JWT creation and player info |
| `PUT /auth/name`, `POST /auth/refresh` | Chosen name and same-identity token renewal |
| `POST /servers/register` | Game server registration (IP, port, region, capacity) |
| `POST /servers/{id}/heartbeat` | Game server liveness + load report |
| `GET /servers` | Server browser: heartbeat-fresh, non-full game servers |
| `POST /match/result` | Match result reporting (roster, winner) |
| `/lobby` (SignalR) | Lobby control, Global Chat, Server Chat, Direct Messages, and online presence |

## Tech Stack

- **.NET 8** — ASP.NET Core Web API
- **PostgreSQL** — Entity Framework Core
- **JWT** — Authentication
- **SignalR** — Authenticated lobby and chat transport; long polling is supported

## Quick Start

```bash
git clone https://github.com/Binoui/SlopArena-MasterServer.git
cd SlopArena-MasterServer

# Create your local env file
cp .env.example .env
# Edit .env with your PostgreSQL password

dotnet restore
dotnet run
```

Requires PostgreSQL and the ASP.NET Core 8 runtime. Development settings come from
`appsettings.Development.json`; production settings use environment variables.
Copying `.env.example` does not load its values into ASP.NET automatically.

## Secrets (Production)

All secrets are stored in **GitHub Secrets** and injected at deploy time.  
Local dev uses `appsettings.Development.json` (committed with dev-only values).

| Secret | Env Variable | Purpose |
|--------|-------------|---------|
| JWT key | `Jwt__Secret` | Signs auth tokens |
| DB connection | `ConnectionStrings__DefaultConnection` | PostgreSQL |
| Steam API | `Steam__ApiKey` | Steam auth (future) |

To use in production:
```bash
export Jwt__Secret="$(openssl rand -base64 64)"
export ConnectionStrings__DefaultConnection="Host=your-host;Database=sloparena;..."
dotnet run
```

## Architecture

```
SlopArena-MasterServer/
├── Data/           # EF Core DbContext + migrations + models
├── DTOs/           # API request/response models
├── Chat/           # Bounded chat state, immutable wire records, validation, quotas
├── Hubs/           # Authenticated LobbyHub: chat + per-server lobby flow
├── Lobbies/        # LobbyManager (in-memory lobby authority) + HTTP match launcher
├── Program.cs      # ASP.NET entry point
└── appsettings.json
```

Master does not depend on the Shared simulation or a private NuGet feed.
Gameplay remains in the GameServer and Shared simulation.

## Chat client contract

This is the Master-side contract for [SlopArena chat](https://github.com/Binoui/SlopArena/issues/206).
Unity ownership, UI, local mute, drafts, scrollback, and input isolation are separate client work.

### Guest lifecycle

1. Choose or load a Display Name before online use. Do not block local play.
2. Call `POST /auth/guest` once per game launch. It returns
   `{ token, steamId, expiresAt }`. The legacy `steamId` field is a guest ID, not
   a verified Steam identity.
3. With that JWT, call `PUT /auth/name` with `{ \"displayName\": \"Alex\" }`.
   Wait for success before starting the hub. This updates the existing user's
   name; it does not create another account.
4. Register push handlers, connect to `/lobby`, then call `GetChatState`.
   A completed transport handshake alone does not confirm chat admission.
5. Before `expiresAt`, call authenticated `POST /auth/refresh`. It returns a
   new JWT for the same identity. Supply the current token through the SignalR
   access-token provider. An expired token returns HTTP 401; do not silently
   create another guest.

Use Bearer authentication for HTTP and SignalR. The existing hub query-token
path remains supported. Do not log tokens or token-bearing URLs.
`GET /auth/me` returns `{ steamId, username, mmr, sessionTag }`.

Names allow duplicates. They contain 1–24 Unicode scalar values after trimming.
Blank or malformed names, control characters, and line separators are rejected.
Formatting controls are rejected except Unicode joiners used in names.
Chat, lobby, and match setup use the same stored `User.Username`.

`PUT /auth/name` also handles later rename. It returns HTTP 409
`{ \"error\": \"joined_server\" }` if any connection of the identity is joined.
Renaming preserves the ID and tag. Already-accepted messages retain their old
sender profile.

### Wire records

JSON uses camelCase. The C# records are in `Chat/ChatModels.cs`.

| Record | Fields |
| --- | --- |
| `ChatPlayer` | `playerId` (opaque string), `displayName`, `sessionTag` |
| `ChatMessage` | `messageId` (GUID), `sequence`, `channel`, `serverId` (nullable GUID), `recipientId` (nullable string), `sender` (`ChatPlayer`), `text`, `sentAt` (UTC timestamp) |
| `ChatPresence` | `player` (`ChatPlayer`), `online` |
| `ServerChatState` | `serverId` (nullable GUID), `messages` (`ChatMessage[]`) |
| `ChatSnapshot` | `self` (`ChatPlayer`), `globalMessages`, `server` (`ServerChatState`) |

Channel values are `global`, `server`, and `direct`. Master sets sender identity,
profile, IDs, sequence, and time. Never select a Direct recipient by name.
Session Tags encode the full identity in uppercase base 36. New guest tags have
at most eight characters; larger existing identities can have longer tags.

`messageId` remains the deduplication key across live pushes, send responses, and
public history. `sequence` orders acceptance within one Master process and resets
on restart. Do not compare sequences from different Master lifetimes.

### Hub calls and pushes

| Call | Arguments | Return |
| --- | --- | --- |
| `GetChatState` | none | `ChatSnapshot` for this connection |
| `GetOnlinePlayers` | none | `ChatPlayer[]`, one entry per online identity |
| `SendGlobal` | `text` | Accepted `ChatMessage` |
| `SendServer` | `serverId`, `text` | Accepted `ChatMessage` |
| `SendDirect` | `playerId`, `text` | Accepted `ChatMessage` |

Existing lobby calls remain: `JoinLobby`, `LeaveLobby`, `HostStart`,
`SelectCharacter`, and `StartMatch`. Their existing events and payload keys remain.

| Push | Payload | Meaning |
| --- | --- | --- |
| `ChatMessage` | `ChatMessage` | A live message, including the sender's echo |
| `ChatPresenceChanged` | `ChatPresence` | First connection, last disconnect, or online rename |
| `ChatServerChanged` | `ServerChatState` | The caller's successful join/switch/leave and current server backlog |

Presence pushes are change notifications, not a durable ordered directory stream.
Use `GetOnlinePlayers` for current state and after reconnect. Coalesce refreshes
within the control budget. A failed query is not an empty directory.

Global reaches every chat-connected participant. Server Chat reaches connections
currently joined to that GameServer through `LobbyManager`, not a match group.
An identity can join through only one connection at a time. Join requires a
registered GameServer with a heartbeat no older than 15 seconds and an available
lobby slot. Unknown/stale admission leaves existing membership unchanged.

Server membership survives selection and match launch. Leave/switch updates the
authoritative membership before later messages can be accepted. Disconnect drops
that connection's membership; reconnect must explicitly rejoin a valid server.
Do not trust a locally remembered server ID. Messages accepted before a departure
can arrive late: inspect `serverId` and never display them as the new server's chat.

Direct reaches the sender's and target identity's live connections, once each.
An offline target fails. There is no server-side Direct history or offline queue,
and a new guest with the same name is not the old recipient.

### Limits and failures

| Resource | Ceiling |
| --- | --- |
| Online identities / connections per identity | 256 / 4 |
| Message length | 500 Unicode scalar values, not UTF-16 units |
| Send attempts | 5 per sliding 5 seconds, shared across channels and connections |
| Other hub calls | 20 per sliding 10 seconds per identity |
| Public backlog | 50 Global messages; 50 per retained GameServer |
| Retained GameServer backlogs | 256; evict the least-recently-used idle channel, not an active one |
| Retained quota entries | 1,024 per budget; expire old entries before admitting more |

Quota time is monotonic. Reconnect does not reset either budget. Invalid text,
unauthorized server targets, and offline Direct attempts consume send allowance.
HTTP write limits remain separate: by default, 10 requests per 10 seconds per IP
in each guest/name/refresh/negotiate/control category. Only the `/lobby` transport
path is exempt; negotiation is not. Configure the HTTP count with
`RateLimit:MaxRequestsPerWindow`. HTTP exhaustion returns 429.

Messages must contain non-whitespace content and valid Unicode. Tabs and line
breaks remain literal; other control characters are rejected. Master does not
parse markup or links. The client must render names and text literally.

Hub failures use `invalid_message`, `rate_limited`, `control_rate_limited`,
`not_connected`, `not_in_server`, `recipient_offline`, `chat_capacity`,
`already_joined`, or `server_unavailable`. The framework can wrap these codes in
its error text. Invalid names return HTTP 400 `{ \"error\": \"invalid_name\" }`.
Capacity rejection during connection setup closes the hub connection.
Existing lobby-specific failures remain separate.

Public history and presence exist only in this Master process. Restart clears
them; chat bodies are not persisted or logged. A successful send response means
Master accepted the message, not that a person received or read it. The sender
also receives a push; deduplicate it. If the connection fails after acceptance,
the result can be uncertain. Never automatically replay an unconfirmed send.

## Verification

Run the actual test project, not the web project:

```bash
dotnet test MasterServer.Tests/MasterServer.Tests.csproj --nologo
```

The chat and lobby integration tests use official SignalR clients over long
polling through the real ASP.NET pipeline. They use isolated EF InMemory stores.
The match-launch test replaces only the external GameServer launch boundary.

If the SDK is installed without the ASP.NET runtime, an isolated self-contained
test build can restore the existing framework runtime packs instead:

```bash
dotnet test MasterServer.Tests/MasterServer.Tests.csproj --runtime linux-x64 -p:SelfContained=true --nologo
```

These checks do not verify PostgreSQL deployment, Unity presentation/input, or a
real GameServer process. The Master-only delivery also requires a headless smoke
against a real local Kestrel listener; TestServer alone is not TCP delivery proof.
