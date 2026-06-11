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
dotnet restore
dotnet run
```

Requires PostgreSQL running locally. Configure connection string in `appsettings.Development.json`.

## Architecture

```
SlopArena-MasterServer/
├── Data/           # EF Core DbContext + migrations
├── DTOs/           # API request/response models
├── Program.cs      # ASP.NET entry point
└── appsettings.json
```

Depends on `SlopArena.Shared` NuGet package from the main [SlopArena](https://github.com/Binoui/SlopArena) repo.
