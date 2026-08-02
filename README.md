# SlopArena Master Server

Backend API for SlopArena — game server registration, the server browser, and SignalR lobbies.
Matchmaking is planned but **not yet implemented**; the server browser is the current entry point.

## Scope

Runs the SlopArena pre-match flow: players self-select a fresh, non-full game server from the
browser, join its lobby, and the host starts the match. No matchmaking exists yet — there is no
queue, no pairing, and no skill/MMR matching (`User.Mmr` is stored but never used).

| Endpoint | Purpose |
| --- | --- |
| `POST /auth/guest`, `GET /auth/me` | Guest JWT auth and player info (player accounts) |
| `POST /servers/register` | Game server registration (IP, port, region, capacity) |
| `POST /servers/{id}/heartbeat` | Game server liveness + load report |
| `GET /servers` | Server browser: heartbeat-fresh, non-full game servers |
| `POST /match/result` | Match result reporting (roster, winner) |
| `/lobby` (SignalR) | Per-game-server lobbies: join/leave, host start, character select, match launch |

## Tech Stack

- **.NET 8** — ASP.NET Core Web API
- **PostgreSQL** — Entity Framework Core
- **JWT** — Authentication
- **GitHub Packages** — NuGet (SlopArena.Shared)

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

Requires PostgreSQL running locally. The `.env` file is gitignored — never commit real secrets.

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
├── Hubs/           # SignalR LobbyHub (per-server lobby flow)
├── Lobbies/        # LobbyManager (in-memory lobby authority) + HTTP match launcher
├── Program.cs      # ASP.NET entry point
└── appsettings.json
```

Depends on `SlopArena.Shared` NuGet package from the main [SlopArena](https://github.com/Binoui/SlopArena) repo.
