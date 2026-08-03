# Backend — Data Intelligence Platform

.NET 10 / ASP.NET Core solution, laid out in the layers described in SOW 4.

> The SOW specifies .NET 8. We build on **.NET 10** instead: .NET 9 is an STS release that
> left support in May 2026, and .NET 8's LTS window closes in November 2026 — about a month
> after go-live. .NET 10 is the current LTS, supported through November 2028.

## Projects

| Project | Layer | Responsibility |
| --- | --- | --- |
| `src/DataIntelligence.Core` | Domain | Entities, DTOs, interfaces. No outward dependencies. |
| `src/DataIntelligence.Infrastructure` | Data / integrations | EF Core `DbContext` and migrations, source collector, LLM client. |
| `src/DataIntelligence.Api` | Presentation | REST endpoints for the dashboards and the AI assistant. |
| `src/DataIntelligence.Worker` | Scheduling | Hosted service running the hourly collection cycle (FR-8). |
| `tests/DataIntelligence.UnitTests` | Test | Services, validation, NL-to-SQL logic (SOW 11.1). |
| `tests/DataIntelligence.IntegrationTests` | Test | Collector → database → API flow (SOW 11.1). |

Dependency direction is `Api`/`Worker` → `Infrastructure` → `Core`. The API and the
Worker are separate deployable units (SOW 4.2) but can share a host in early phases.

## Prerequisites

- .NET SDK 10.x — pinned in [../global.json](../global.json). The SDK ships the matching
  runtime, so this is the only .NET install needed.
- Microsoft SQL Server (local instance, LocalDB, or a container).

## Build and test

```powershell
dotnet build DataIntelligence.sln
dotnet test DataIntelligence.sln
```

## Run

```powershell
dotnet run --project src\DataIntelligence.Api
dotnet run --project src\DataIntelligence.Worker
```

The API listens on `http://localhost:5063`. `GET /health` is the only endpoint wired up
so far. In Development it also serves:

| Path | Purpose |
| --- | --- |
| `/openapi/v1.json` | OpenAPI document — the frontend generates TypeScript types from this |
| `/swagger` | Browsable UI over that document |

## Configuration and secrets

Connection strings and API keys are never committed (SOW 3 — Security). `appsettings.json`
holds empty placeholders; supply real values through user secrets locally:

```powershell
dotnet user-secrets set "ConnectionStrings:DataIntelligenceDb" "<connection string>" --project src\DataIntelligence.Api
dotnet user-secrets set "ConnectionStrings:DataIntelligenceDb" "<connection string>" --project src\DataIntelligence.Worker
```

Use environment variables in deployed environments.

| Setting | Where | Notes |
| --- | --- | --- |
| `ConnectionStrings:DataIntelligenceDb` | Api, Worker | SQL Server connection string. |
| `Cors:AllowedOrigins` | Api | Frontend origins. Defaults to `http://localhost:3000` in Development. |
| `Collection:SourceUrl` | Worker | Blocked on the `[DATA SOURCE — TBD]` sign-off (SOW 0.1). |
| `Collection:CronSchedule` | Worker | Hourly by default. |

## Not yet implemented

Schema and migrations, the collector, authentication (FR-9), and the AI orchestration
layer are Phase 4 work and depend on the Phase 3 schema design.
