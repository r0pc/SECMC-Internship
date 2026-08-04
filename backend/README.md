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
| `Collection:SourceUrl` | Worker | Blocked on the `[DATA SOURCE — TBD]` sign-off (SOW 0.1). Empty means the Worker starts, logs, and idles. |
| `Collection:IntervalMinutes` | Worker | 60 (hourly, FR-1). Cycles align to the wall clock unless `AlignToClock` is false. |
| `Collection:Parser` | Worker | XPath selector profile for the source. See below. |

Every `Collection:*` setting is documented inline in
[src/DataIntelligence.Worker/appsettings.json](src/DataIntelligence.Worker/appsettings.json).

## Data collection

The collection pipeline (FR-1 – FR-4) is implemented and runs in the Worker:

```text
robots.txt check → fetch (retry/backoff) → store raw payload → parse → validate → dedupe → persist → record the run
```

Every stage writes to `collect.CollectionRun`, so a failure is logged with a category
rather than taking the scheduler down (FR-2). The only source-specific part is the
**selector profile** in `Collection:Parser` — `RecordSelector` is XPath matching one node
per record, and each field's `Selector` is XPath relative to that node:

```json
"Parser": {
  "RecordSelector": "//div[@class='listing']",
  "Fields": {
    "SourceKey":    { "Selector": ".", "Attribute": "data-id", "Required": true },
    "Title":        { "Selector": ".//h3", "Required": true },
    "PrimaryValue": { "Selector": ".//span[@class='price']", "Type": "Decimal", "StripCharacters": "£," }
  }
}
```

Keys that match a snapshot column (`SourceKey`, `Title`, `CategoryCode`, `SourceUrl`,
`PrimaryValue`, `SecondaryValue`, `Quantity`, `StatusText`, `CurrencyCode`,
`PublishedAtUtc`) map to it; any other key is stored as an extension attribute. Because
this is configuration, confirming the source — or repairing a selector after the site's
markup shifts — needs no code change (SOW 9, Risk 1).

Deduplication (FR-3) compares a SHA-256 hash of each record's measures against that item's
last snapshot. Unchanged items update `Item.LastSeenAtUtc` without spending a fact row;
set `Collection:StoreUnchangedSnapshots` to `true` to write one every cycle instead.

## Database

[docs/database-schema.sql](../docs/database-schema.sql) is the design of record; EF Core
migrations are the deployment mechanism. The two are verified to produce identical
`collect` and `core` schemas — columns, indexes, and check constraints.

```powershell
# Applies migrations. Reads ConnectionStrings__DataIntelligenceDb from the environment.
$env:ConnectionStrings__DataIntelligenceDb = "<connection string>"
dotnet ef database update --project src\DataIntelligence.Infrastructure
```

Integration tests create and drop their own uniquely named database, so they need a
reachable SQL Server (SOW 11.2). They default to a local default instance; set
`DATAINTELLIGENCE_TEST_SQL` to point elsewhere.

## Not yet implemented

Authentication (FR-9), the dashboard and reporting endpoints (FR-7, FR-10 – FR-12), and
the AI orchestration layer (FR-13 – FR-16) are the remaining Phase 4 work.
