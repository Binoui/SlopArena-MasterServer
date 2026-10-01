# SlopArena Master Server

Backend API for Steam Playtest sessions (or explicit development guests), GameHost registration, the public Room browser, SignalR Room preparation, and game-wide chat.
There is no matchmaking queue; players create or join named Rooms.

## Scope

The normal public entry path is Master-managed Rooms. A Room holds membership and
Server Chat independently of physical GameHosts. The leader selects Characters and
Arena, then Master assigns a compatible GameHost at Match launch. Authenticated
GameHost completion/cancellation returns only the matching Room to Lobby with
preparation cleared; membership and chat survive Results and rematch. Physical
host/address lookup and waiting-roster control remain development-only.

| Endpoint | Purpose |
| --- | --- |
| `POST /auth/steam`, `POST /auth/guest`, `GET /auth/me` | Verified Playtest login, development-only guest login, current player |
| `PUT /auth/name`, `POST /auth/refresh` | Chosen name and same-identity renewal (Steam requires a fresh web ticket) |
| `POST /servers/register` | Trusted provisioned GUID + current Steam GameHost identity, protocol and process instance (development: IP:port) |
| `POST /servers/{id}/heartbeat` | GameHost liveness/load and current identity check |
| `GET /servers` | Physical GameHost lookup in development only; VPS returns 404 |
| `POST /match/result`, `POST /match/cancel` | Authenticated terminal Match report, matching Room return; cancellation has no winner |
| `/lobby` (SignalR) | Authenticated public Rooms, Room chat and scoped Match route; development-only physical lobbies |

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

## Deployment profiles and credentials

`Deployment:Profile` is required; there is no implicit production fallback.
Local development explicitly selects `development` in
`appsettings.Development.json`. Production VPS instances must select `vps`.

| Setting | Purpose |
| --- | --- |
| `Deployment__Profile` | `development` or `vps`; required |
| `Jwt__Secret` | Application JWT signing key; separate from both host credentials |
| `ConnectionStrings__DefaultConnection` | PostgreSQL |
| `ApprovedHost__Id` | Provisioned, non-empty host GUID; becomes the stable browser `serverId` |
| `ApprovedHost__RegistrationKey` | Bearer credential accepted only by VPS registration |
| `ApprovedHost__PublicHost` | Trusted public DNS/IP metadata; Steam gameplay uses its verified identity instead |
| `ApprovedHost__PublicPort` | Private GameHost control base port (legacy development UDP still uses port allocation) |
| `Proxy__TrustedAddress` | Exact private IPv4 address of the reverse proxy trusted to set forwarded headers; required in VPS mode |
| `ApprovedHost__ControlUrl` | Private HTTP control endpoint on the base port, path `/match/start`; not public gameplay ingress |
| `Auth__Mode`, `Steam__ApiKey`, `Steam__AppId`, `Steam__Identity` | `steam` required on VPS; publisher key stays on Master, explicit Playtest AppID and fixed identity `sloparena-playtest` |

Example VPS configuration (replace placeholders in the secret/configuration
manager; do not put real credentials in source control):

```text
Deployment__Profile=vps
Auth__Mode=steam
Steam__ApiKey=<managed-publisher-key>
Steam__AppId=<verified-playtest-app-id>
Steam__Identity=sloparena-playtest
ApprovedHost__Id=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
ApprovedHost__RegistrationKey=<managed-registration-secret>
ApprovedHost__PublicHost=gameserver.example.net
ApprovedHost__PublicPort=9876
ApprovedHost__ControlUrl=http://gameserver.internal:9876/match/start
Proxy__TrustedAddress=172.30.11.10
MatchControl__Key=<different-managed-match-control-secret>
Jwt__Secret=<different-managed-jwt-secret>
ConnectionStrings__DefaultConnection=<postgres-connection-string>

```

VPS startup fails closed when a required value is missing or malformed, if
the registration/control keys are not 32–4096 character bearer tokens, if
the JWT secret is shorter than 32 characters, if the secrets are reused,
or if the control URL is not a private absolute HTTP endpoint with the
configured base port and exact `/match/start` path. Use an RFC1918/ULA IP
or a private DNS name (a single-label name or one ending in `.internal`,
`.local`, `.localhost`, `.lan`, or `.svc`) whose host differs from
`ApprovedHost:PublicHost`; the request body can never select this route.
Public VPS auth is Steam-only. Startup requires `Auth__Mode=steam` plus the
publisher key, positive Playtest AppID, and exact `Steam__Identity`. Configure
the actual AppID against operator-owned Steamworks settings before deployment;
the development/SteamPipe candidate is `5325920`, not a deployed guarantee.
`appsettings.Development.json` explicitly selects `development-guest`; a
development Steam test can instead select `Auth__Mode=steam` with the same
required Steam settings. Guest JWTs, including ones issued before this cutover,
fail public REST, SignalR, and refresh. No publisher key, ticket, or
credential-bearing Valve URL belongs in logs or client assets. Apply the
`AddSteamAuthIdentity` and `AddSteamMatchRouting` database migrations before a compatible cutover.

The advertised gameplay host currently supports IPv4 or DNS, not an IPv6
literal.

In VPS mode, `/servers/register` requires the provisioned host GUID,
`Authorization: Bearer <ApprovedHost:RegistrationKey>`, canonical decimal-string
GameHost `steamId`, `protocolVersion: 4`, nonempty per-process `instanceId` GUID,
and lowercase SHA-256 `catalogHash` of the admitted immutable content map.
Master ignores request IP/UDP address and official flag. A heartbeat with a
changed identity or catalog hash immediately makes the old browser entry
ineligible. A fresh registration under the same host GUID pins the change,
cancels its open matches and notifies rostered clients only when the host
identity changes. Every successful registration issues a new API token; a
same-identity refresh preserves match load but revokes the previous token.
No Steam ID is substituted into an IP field. Browser entries publish the
current `serverSteamId` and protocol
only while heartbeat-fresh. Legacy imported rows cannot join, heartbeat or
complete VPS matches.

Master creates the authoritative Match row and roster before its private
`POST /match/start`, passing the registered catalog hash as the exact expected
content identity. GameHost rejects a changed local map before allocation and
responds with cooked content, the matching digest and its own verified Steam
identity. Only that roster receives a `MatchStarted.descriptor` (`steam-p2p`,
host identity, GUID, virtual port 0, protocol 4, content digest, admission
deadline); old clients are denied a VPS lobby slot. GameHost controls admission
and authoritative simulation.
It reports normal results once, or calls authenticated `POST /match/cancel`
with `unfilled`, `absent`, `host_restart`, `host_shutdown` or
`content_unavailable`. Cancellation records reason/time with no winner/MMR,
signals `MatchAborted` to the roster, and retains Global/Server/Direct chat
membership. Neither a temporary Steam web-auth outage nor Master unavailability
ends a match already admitted at GameHost. Do not deploy these changes without
a compatible Master/GameHost/client release and the DB migrations.

`Proxy:TrustedAddress` must be one static IPv4 address (for example, the local
Compose Caddy proxy at `172.30.11.10`); wildcard addresses and IPv6 are rejected.
Only that source may supply one `X-Forwarded-For` and `X-Forwarded-Proto` hop.
Forwarded headers are ignored in explicit development mode. Forwarded client IP
is applied before the per-IP HTTP rate limiter; do not expose the application
port around the trusted proxy.

`GET /health` is dependency-free process liveness. `GET /ready` checks database
connectivity and pending EF migrations, returns 503 on failure or schema drift,
and is bounded to three seconds. It never applies migrations. Graceful host
shutdown is bounded to 15 seconds.


`development` remains the explicit local mode: it retains unauthenticated
IP:port upsert behavior and does not require `hostId`.

The registration key, API token, and match-control key have separate roles.
The GameServer uses the registration key only for registration; it uses
the API token returned by Master for heartbeat, match-result, and cancellation
requests. Master stores only its SHA-256 digest in `GameServers.ApiTokenHash`;
the bearer is returned once per registration and is never recoverable from the
database. Do not log credentials or place them in URLs.

Generate each secret independently, for example with
`openssl rand -base64 48`, and store it only in the secret manager.

Rotate the registration key by provisioning a new value on Master and the
GameServer together, then restarting the host; this does not independently
invalidate the active API token. Rotate the match-control key by updating
Master and the GameServer control listener together. To revoke an API token,
re-register with the approved registration key; the previous bearer stops
authenticating immediately. Drain active matches before intentionally rotating
credentials on a live host.

Apply `HashGameServerApiTokens` before deploying this Master version: it
converts existing plaintext values in PostgreSQL without disconnecting
running hosts. The migration is irreversible; an older Master cannot use
the hashed column, so do not roll back the application without an
operator-reviewed database recovery and host re-enrollment.

## Container release and migrations

The `Container images` workflow tests and publishes only for a manual dispatch
from `main` or a published GitHub release. Manual dispatch requires a `release_id`
matching `[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}`. It publishes independent Linux amd64
images to GHCR: the ASP.NET Core 8 application and a self-contained EF migration
runner built from the same source revision. The images use SDK
`8.0.425-bookworm-slim` and ASP.NET/runtime-deps
`8.0.31-bookworm-slim`; migrations target PostgreSQL 15. The workflow summary
reports immutable image digests, source revision, exact runtime, and release
identity. Deploy by digest rather than a tag.

The Docker context is an explicit source allowlist. It includes `Rooms/` alongside
Chat, lobby, host, DTO and migration code; add newly required source directories
to `.dockerignore` rather than broadening the context to runtime configuration or
credentials. A successful workstation build does not prove the filtered image
context is complete.

The image smoke applies the built migration bundle to an isolated PostgreSQL
15.19 container pinned to the deployment image digest, then checks application
`/ready`. It supplies explicit nonsecret Room admission IDs and keeps database
ports private. A transient `/health` response with a missing database is not
readiness evidence.

Supply `Deployment__Profile=vps`, `Proxy__TrustedAddress` set to the exact Caddy
IPv4, every `ApprovedHost__*` value, `MatchControl__Key`, `Jwt__Secret`,
`Room__AdmittedCharacters__*`, `Room__AdmittedArenas__*`, `Room__CatalogHash`, and
`ConnectionStrings__DefaultConnection` through the deployment platform's
configuration/secret manager. Room characters must match the deployed
Manki/FightGuy/Wibou/Bonk catalog and its exact match-catalog digest; an old Kistu
selector or catalog hash prevents compatible matchmaking. Neither local settings
nor secrets are included
in the images. The application listens on port 8080 and does not apply
migrations during startup. Run migrations separately before deploying the
application, passing the connection as an environment secret:

```bash
docker run --rm --platform linux/amd64 --read-only \
  --tmpfs /tmp:rw,uid=1654,gid=1654,size=128m \
  --env ConnectionStrings__DefaultConnection \
  "ghcr.io/binoui/sloparena-masterserver-migrations@${MIGRATION_DIGEST}"
```

The self-contained EF bundle extracts to `/tmp/bundle`; the bounded tmpfs is
the only writable path. The migration runner reads the connection variable
without putting it in its command line. Then run the application image by its
reported digest, supplying the complete VPS configuration externally and
publishing port 8080.

## Architecture

```
SlopArena-MasterServer/
├── Data/           # EF Core DbContext + models
├── Migrations/     # Versioned PostgreSQL schema
├── DTOs/           # API request/response models
├── Chat/           # Bounded chat state, immutable wire records, validation, quotas
├── Hubs/           # Authenticated LobbyHub: Rooms, chat + per-server lobby flows
├── Rooms/          # RoomManager (in-memory public Room authority)
├── Steam/          # Playtest ticket verification and durable replay store
├── Program.cs      # ASP.NET entry point
└── appsettings.json
```

Master does not depend on the Shared simulation or a private NuGet feed.
Gameplay remains in the GameServer and Shared simulation.


## Public Room client contract

The authenticated `/lobby` SignalR hub exposes a public Room browser and membership API. Rooms
are owned by Master memory and do not reserve a GameHost or simulation slot.

| Call | Arguments | Return |
| --- | --- | --- |
| `GetRooms` | none | `RoomSummary[]`, public Rooms only |
| `CreateRoom` | `name` | `RoomSnapshot`; creator is the first member and leader |
| `JoinRoom` | `roomId` | `RoomSnapshot`; joining an existing Room is idempotent for its current member |
| `GetMyRoom` | none | This Steam identity's `RoomSnapshot`, or `null` |
| `LeaveRoom` | none | no return; explicitly leaves the current Room |
| `RoomStartCharacterSelect` | none | leader advances Lobby → Character Select (2–4 members) |
| `RoomSelectCharacter` | admitted Character selector | member locks in a Character in Character Select |
| `RoomStartStageSelect` | none | leader advances after every member locks in |
| `RoomChooseArena` | admitted Arena name | leader sets the Arena in Stage Select; does not launch a Match |
| `RoomStartMatch` | none | leader allocates a compatible GameHost for a prepared Room and returns the Match route |

`RoomDirectoryChanged` is a payload-free push to connected browser clients when
a public Room summary changes (creation, membership, phase, leadership, expiry
or terminal return). Clients re-read `GetRooms`; it never carries chat, a Match
descriptor or an unauthorized member roster. `RoomUpdated` remains Room-scoped.

`RoomSummary` contains `id`, `name`, `phase`, `leaderSteamId`, `memberCount`, `capacity`, and
`joinable`. `RoomSnapshot` adds `arenaName`, deployment-pinned `admittedCharacters` and
`admittedArenas`, and ordered `members`; each member contains `steamId`, `name`, `isLeader`,
`characterSelection`, and `lockedIn`. Phases are `Lobby`, `Character Select`,
`Stage Select`, `Match Starting`, and `In Match`; snapshots also expose `activeMatchId`
while launching/fighting. Only Lobby Rooms are joinable. Names are trimmed, literal text with
1–24 valid Unicode scalar values; blank, malformed, control-bearing, and line-separator
names are rejected; formatting controls are rejected except Unicode joiners. Duplicate
Room names are allowed and each Room has its own GUID.

Master allows at most five Rooms and four members per Room. Membership is keyed by the verified
Steam identity, never by a connection or caller-supplied ID. Reconnecting before the 15-second
deadline reattaches without allocating another slot or changing join order. A disconnected leader
keeps leadership during grace, including when another player joins. At the deadline the member
expires and the oldest connected remaining member takes over; a later return joins at the end
without reclaiming leadership. Explicit `LeaveRoom` transfers leadership immediately to the
oldest eligible remaining member. A Room with no members remains listed and joinable for 60
seconds, with `leaderSteamId` of `0`, then expires and frees its slot. Members
disconnected during an active Match retain their place until the Match terminates.
Explicit development GameHost-lobby membership cannot overlap a Room; production
has no physical-lobby admission.
Room preparation and broadcasts use the Room GUID, never a GameServer lobby. The leader
advances phases; every member may lock in or change an admitted Character during Character
Select. Stage Select requires 2–4 locked-in members. Only the leader may choose an admitted
Arena. Room membership dropping below two resets preparation to Lobby and clears picks;
other departures clear the Arena and require the leader to choose it again.
`RoomStartMatch` atomically freezes the selected roster; only its Room group receives
the `MatchStarted` payload with a nonempty root `matchId`, the physical `serverId`,
content map and Steam descriptor in VPS mode (development sends the selected
GameHost's `serverAddress` plus assigned UDP `matchPort`). When present, the
Steam descriptor carries the same Match ID. Explicit development physical-lobby
launches also carry the GameHost's root Match ID. The Match row records nullable
`roomId` separately from physical `serverId` (older rows remain null). Launch
failure returns to Stage Select with selections and Arena intact. A Room launch
passes a 60-second admission deadline; an unfilled Match expires even if a
failed launch could not deliver the abort command. Disconnected members are not
pruned during an active Match. A GameHost result or cancellation updates only the
Room with that exact active Match ID, once: Lobby phase, no active ID, Arena or
Character Lock-in, with current membership and Server Chat intact. A late or
duplicate report cannot reset a rematch. Cancellations notify only the affected
Match roster with `MatchAborted` and record no winner. Unity keeps Results
viewable until the player elects to return and checks current Room membership.

When an open Match's GameHost has missed heartbeats for at least 60 seconds,
Master probes its control `/health` with a two-second bound. Only an unreachable
host whose heartbeat and process identity remain unchanged is canceled as
`host_unavailable`; a healthy GameHost is not canceled solely because Master
missed heartbeats. This uses the same idempotent terminal transition and
preserves unrelated Rooms and chats.

`Room:AdmittedCharacters` and `Room:AdmittedArenas` are deployment-pinned selectors.
`Room:CatalogHash` defaults to the current cooked roster's 64-character GameHost
content hash; update it with each accepted content release or override via
`Room__CatalogHash` using the admitted GameHost's registered hash. Master filters
hosts by fresh heartbeat, matching hash and protocol, approved-host policy in
VPS mode, and free capacity; lowest occupied/capacity ratio wins with a stable
GUID tie-break.
GameHost returns its actual content hash at launch; Master rejects/aborts a
development Match if it differs from the pin. Keep selectors and hash aligned
with shipped cooked content and available PvP arenas. Master refuses to start
without nonempty selector lists and rejects unknown picks; GameHost still
validates content again at launch.


Server Chat is Room-scoped. `ChatServerChanged` publishes the Room ID and that
Room's bounded history when an identity creates or joins a Room and whenever a
connection reconnects. Leaving or losing Room membership clears the chat state
with a null Room ID before `RoomMembershipRevoked`. The 15-second disconnect
grace reattaches the same Room membership; reconnecting after expiry receives
null state and cannot send to the old Room.

While a player is in a Room, authenticated `PUT /auth/name` updates that member's roster name
and pushes the refreshed snapshot to Room members.

`RoomUpdated` pushes the full `RoomSnapshot` to the Room group after roster, phase,
Character Lock-in, Arena, or leader changes.
`RoomMembershipRevoked` pushes the Room GUID to every active connection of an
identity after a null `ChatServerChanged` and before its Room-group attachments
are removed. `RoomDeleted` pushes the Room GUID when an empty Room expires.
Hub errors include stable prefixes and guidance: `invalid_room_name`, `room_limit`,
`already_in_room`, `room_not_found`, `room_full`, `room_selecting`, `not_in_room`,
`not_leader`, `invalid_phase`, `room_not_ready`, `character_not_admitted`, and
`arena_not_admitted`.

## Chat client contract

This is the Master-side contract for [SlopArena chat](https://github.com/Binoui/SlopArena/issues/206).
Unity ownership, UI, local mute, drafts, scrollback, and input isolation are separate client work.

### Application session lifecycle

1. A Steam client calls `GetAuthTicketForWebApi("sloparena-playtest")`, waits
   for its callback, and sends `{ "ticket": "<hex>" }` to HTTPS
   `POST /auth/steam`. Master validates the ticket and current Playtest
   ownership through Valve, durably records the consumed ticket digest,
   and returns `{ token, steamId, expiresAt }` for the verified identity.
   Usernames are presentation; an existing guest row is never merged.
2. The Steam JWT expires after one hour. Before expiry, call authenticated
   `POST /auth/refresh` with a **fresh** web ticket and the same account;
   current ownership is checked again. Replayed, wrong-account or
   unentitled tickets fail; an expired bearer cannot renew. A fresh ticket
   can start a new session for the same account. During a Valve outage,
   existing JWTs remain valid until expiry, but login and renewal fail closed.
3. Explicit `development-guest` mode retains `POST /auth/guest` and its
   ticketless refresh for Editor/local development only. VPS rejects guest
   issuance and previously signed guest tokens; there is no public fallback.
4. Both modes apply `PUT /auth/name` to the same account before connecting
   the single `/lobby` SignalR client. Save the display name independently
   of account identity. Names allow duplicates; HTTP and SignalR use the
   same bearer token, and the hub query-token route remains supported.
   Master closes even an established WebSocket when that JWT expires; it does
   not trust the client to reconnect voluntarily. A fresh valid token starts a
   new hub connection, reattaches Room membership within its grace period,
   and can separately revalidate remembered GameServer admission with
   `ResumeServer`. Physical admission never grants Server Chat.
   Hub expiration never calls GameHost match cancellation.

Do not log tokens or token-bearing URLs. `GET /auth/me` returns
`{ steamId, username, mmr, sessionTag }`.

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
| `ChatMessage` | `messageId` (GUID), `sequence`, `channel`, `roomId` (nullable GUID), `recipientId` (nullable string), `sender` (`ChatPlayer`), `text`, `sentAt` (UTC timestamp) |
| `ChatPresence` | `player` (`ChatPlayer`), `online` |
| `ServerChatState` | `roomId` (nullable GUID), `messages` (`ChatMessage[]`) |
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
| `SendServer` | `roomId`, `text` | Accepted `ChatMessage` |
| `SendDirect` | `playerId`, `text` | Accepted `ChatMessage` |

Existing lobby calls remain: `JoinLobby`, `ResumeServer`, `LeaveLobby`, `HostStart`,
`SelectCharacter`, `StartStageSelect`, and `StartMatch(arenaName)`. Their existing
events and payload keys remain; `MatchStarted` additionally carries the
authoritative GameServer `content` JSON element alongside `matchPort` and
`arenaName`.

| Push | Payload | Meaning |
| --- | --- | --- |
| `ChatMessage` | `ChatMessage` | A live message, including the sender's echo |
| `ChatPresenceChanged` | `ChatPresence` | First connection, last disconnect, or online rename |
| `ChatServerChanged` | `ServerChatState` | Current Room chat state after create/join/reconnect, or null Room ID and empty messages after leave/revocation |

Presence pushes are change notifications, not a durable ordered directory stream.
Use `GetOnlinePlayers` for current state and after reconnect. Coalesce refreshes
within the control budget. A failed query is not an empty directory.

Global reaches every chat-connected participant. Server Chat reaches only
connections whose verified identity is actively attached to the same Room in
`RoomManager`. The caller-supplied `roomId` never grants access. Physical
GameServer lobby membership, match groups, and remembered GameServer admission
do not grant Server Chat; `JoinLobby` and `ResumeServer` never change Room chat
state. `GetChatState` and `SendServer` use active Room membership, and an
unauthorized or guessed Room ID is rejected with `not_in_room`.

Room creation/join, reconnect attachment, leave, and expiry serialize with
Server Chat sends and delivery. A send accepted before leave is queued before the
revocation push; a send after leave is rejected. A Room's backlog is bounded
and keyed by its own GUID; deleting the Room also deletes its chat history.

Physical lobby admission is limited to explicit development host/address
workflows. VPS rejects `JoinLobby` and `ResumeServer` with
`physical_admission_disabled`, and does not publish `GET /servers`.
Development physical membership never grants Server Chat; the normal
browser and Room preparation use Room methods instead.

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
| Public backlog | 50 Global messages; 50 per retained Room |
| Room backlogs | At most five, matching the live Room limit; deleted with their Room |
| Retained quota entries | 1,024 per budget; expire old entries before admitting more |
| Remembered Server admissions | 1,024 identities; 24-hour expiry/LRU eviction; explicit Leave clears |
Quota time is monotonic. Reconnect does not reset either budget. Invalid text,
unauthorized Room targets, and offline Direct attempts consume send allowance.
HTTP write limits remain separate: by default, 10 requests per 10 seconds per IP
in each guest/name/refresh/negotiate/control category. Only the `/lobby` transport
path is exempt; negotiation is not. Configure the HTTP count with
`RateLimit:MaxRequestsPerWindow`. HTTP exhaustion returns 429.

Messages must contain non-whitespace content and valid Unicode. Tabs and line
breaks remain literal; other control characters are rejected. Master does not
parse markup or links. The client must render names and text literally.

Hub failures use `invalid_message`, `rate_limited`, `control_rate_limited`,
`not_connected`, `not_in_room`, `recipient_offline`, or `chat_capacity`. Physical
lobby operations retain their separate failures such as `already_joined`,
`server_unavailable`, `lobby_full`, and `not_admitted`. The framework can wrap
these codes in its error text. Invalid names return HTTP 400
`{ "error": "invalid_name" }`.

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

SignalR integration tests exercise the live ASP.NET pipeline over TestServer and
use isolated EF InMemory stores. Registration tests cover development and VPS
HTTP behavior, hash-only persistence, rotation on re-registration, and duplicate
refreshes. The in-memory provider does **not** prove PostgreSQL uniqueness,
migration conversion, or concurrent insert arbitration. Verify those against
PostgreSQL before claiming relational behavior.
Launcher tests replace the external GameServer HTTP boundary and assert
private VPS routing plus bearer authentication.

Room reconnect tests synchronize on the server's offline `ChatPresenceChanged`
push before advancing an injected clock. Client `StopAsync` alone does not
acknowledge completion of `OnDisconnectedAsync`; advancing first can start the
15-second grace period after the intended deadline and miss leader promotion.

If the SDK is installed without the ASP.NET runtime, an isolated self-contained
test build can restore the existing framework runtime packs instead:

```bash
dotnet test MasterServer.Tests/MasterServer.Tests.csproj --runtime linux-x64 -p:SelfContained=true --nologo
```

These checks do not verify PostgreSQL deployment, Unity presentation/input, or a
real GameServer process. The Master-only delivery also requires a headless smoke
against a real local Kestrel listener; TestServer alone is not TCP delivery proof.
