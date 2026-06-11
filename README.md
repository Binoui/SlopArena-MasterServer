# SlopArena Master Server

Backend API for SlopArena — matchmaking, player accounts, and game server registration.

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
├── Data/           # EF Core DbContext + migrations
├── DTOs/           # API request/response models
├── Program.cs      # ASP.NET entry point
└── appsettings.json
```

Depends on `SlopArena.Shared` NuGet package from the main [SlopArena](https://github.com/Binoui/SlopArena) repo.
