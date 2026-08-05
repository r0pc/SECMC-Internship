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

The API listens on `http://localhost:5063`. In Development it also serves:

| Path | Purpose |
| --- | --- |
| `/openapi/v1.json` | OpenAPI document — the frontend generates TypeScript types from this |
| `/swagger` | Browsable UI over that document |

## HTTP API

Every endpoint lives under `/api` and returns JSON. Enums serialise as their names
(`"Monthly"`, not `3`) and every timestamp carries an explicit `Z`, so JavaScript cannot parse
it as local time. Failures are [ProblemDetails](https://datatracker.ietf.org/doc/html/rfc9457) —
one error shape for the whole API. Worked examples of every call are in
[DataIntelligence.Api.http](src/DataIntelligence.Api/DataIntelligence.Api.http).

### Dashboards (FR-10, FR-11)

| Endpoint | Purpose |
| --- | --- |
| `GET /api/dashboard/summary` | Catalogue counts, span of stored history, and per-source collection health — the whole landing page in one call. `?windowDays=` (default 30) |
| `GET /api/dashboard/kpis?seriesIds=2,5` | Latest value, change since the previous release, change year over year. Up to 20 series |
| `GET /api/dashboard/trend?seriesIds=2,5&from=&to=&granularity=` | Trend lines over a shared range. Up to 10 series |

`granularity` defaults to `Auto`: points stay unbucketed until the range would produce more of
them than a chart can usefully draw, then widen to `Month`, `Quarter`, or `Year`. Where a bucket
holds several observations, `value` is the mean and `minimum`/`maximum` carry the spread. The
width actually used comes back on each line.

Units travel with each line and are **not** interchangeable — SOFR volume is in billions of
dollars, CPI is an index. Two series with different `unit` values must not share an axis.

### Catalogue (FR-7)

| Endpoint | Purpose |
| --- | --- |
| `GET /api/sources`, `GET /api/sources/{id}` | Publishers, with their active series count |
| `PATCH /api/sources/{id}` | Polling settings only: enabled state, interval, timeout, retries, user agent, terms link |
| `GET /api/categories`, `GET /api/categories/{id}` | Drill-down groupings, flat; hierarchy is in `parentCategoryId` |
| `POST` / `PUT` / `DELETE /api/categories` | Full CRUD. Delete is refused while series or child categories still reference the row |
| `GET /api/series` | Filter by `dataSourceId`, `categoryId`, `frequency`, `seasonalAdjustment`, `isActive`, `search`; paged |
| `GET /api/series/{id}` | One series, with its latest value and concurrency token |
| `PUT /api/series/{id}` | Title, category, decimal places, active state |

### Time series and collection log

| Endpoint | Purpose |
| --- | --- |
| `GET /api/series/{id}/observations` | `from`, `to`, `periodType`, `includeRevisions`, `asOfUtc`, `sort`, paging |
| `GET /api/collection/runs` | Every cycle, newest first. `failuresOnly=true` for the operations panel |
| `GET /api/collection/runs/{id}` | One run |
| `GET /api/collection/health` | Rolling-window success rate and consecutive failures per source |

Three behaviours worth knowing before wiring up a chart:

- **Current values only, in the series' own period length.** A BLS response can carry an annual
  average (M13) alongside monthly rows; plotted unfiltered it becomes a thirteenth month, and
  averaged it double-counts the year. Pass `periodType` explicitly to read those rows.
- **Revisions are additive, not destructive** (FR-4). `includeRevisions=true` returns superseded
  vintages; `asOfUtc` reads what the platform believed at an instant — *"what did we think June's
  CPI was, on 15 July?"* Both are off by default, so a chart plots each period once.
- **Observations are read-only.** They are written solely by the collector; an endpoint that
  could edit them would undo the trustworthiness the append-only design exists to provide.

Paging is uniform: `?page=` (1-based) and `?pageSize=`, answered with `items`, `page`,
`pageSize`, `totalCount`, `totalPages`, `hasNextPage`, `hasPreviousPage`. Out-of-range values are
clamped rather than rejected — 500 per page for catalogue lists, 2000 for observations.

`PUT /api/series/{id}` takes the `rowVersion` from the series you read. Send it back and a
concurrent edit returns 409 instead of silently overwriting; omit it to skip the check.

Authentication is not wired up yet (FR-9). Every endpoint is currently anonymous.

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
| `Collection:Bls:ApiKey` | Api, Worker | BLS registration key. **Never committed** — user secrets or environment only. Optional: unregistered v2 calls still work under a much smaller daily quota, so an absent key degrades rather than stops collection. |
| `Collection:IntervalMinutes` | Worker | 60 (hourly, FR-1). Cycles align to the wall clock unless `AlignToClock` is false. |
| `Collection:Bls:YearsOfHistory` | Worker | 2 — current and previous calendar year per request. |
| `Collection:Sofr:LookbackBusinessDays` | Worker | 10 — so a weekend or short outage is backfilled by the next cycle. |

Endpoints, cadence and enabled state are **not** configuration — they live in
`collect.DataSource`, seeded by the migration. Disable a publisher by clearing its `IsEnabled`
row (or `PATCH /api/sources/{id}`), not by editing a config file.

```powershell
dotnet user-secrets set "Collection:Bls:ApiKey" "<key>" --project src\DataIntelligence.Worker
```

Every `Collection:*` setting is documented inline in
[src/DataIntelligence.Worker/appsettings.json](src/DataIntelligence.Worker/appsettings.json).

## Data collection

Ten series from two publishers, both over official JSON APIs — the catalogue, units and
history windows are documented in [docs/README.md](../docs/README.md#what-the-platform-stores).

The pipeline (FR-1 – FR-4) runs in the Worker, once per enabled source per cycle:

```text
open run → build request → fetch (retry/backoff) → store raw payload
         → skip if body unchanged → parse → validate → dedupe/revise → close run
```

Every stage writes to `collect.CollectionRun`, so a failure is recorded with a category rather
than taking the scheduler down (FR-2), and one publisher failing cannot affect the other.

**Everything publisher-specific lives in one `ISourceAdapter` per source**, which owns both
halves of the contract — how to ask, and how to read the answer:

| Adapter | Request | Response shape |
| --- | --- | --- |
| `BlsCpiAdapter` | `POST` with `{seriesid[], startyear, endyear, registrationkey}` | `Results.series[].data[]`; values are **strings**, `"-"` means suppressed, footnote `"R"` means revised |
| `SofrAdapter` | `GET .../sofr/last/{n}.json` | `refRates[]`; one record yields six observations, `revisionIndicator` marks a correction |

Two BLS details are load-bearing. The API returns **HTTP 200 with a failure envelope**, so
`status` is checked before the data — otherwise a quota rejection would be recorded as a healthy
run that collected nothing. And period tokens are not months: `M13` is the annual average, so
treating it as a thirteenth month would add a phantom point to every year.

Adding a publisher is a new adapter plus a `collect.DataSource` row. Nothing downstream changes.

### Deduplication and revisions (FR-3, FR-4)

Two layers, cheapest first:

1. **Whole-response** — the SHA-256 of the body is compared with the previous run's. Identical
   means the publisher released nothing; the run succeeds without parsing anything.
2. **Per-observation** — `SHA-256(Value + SourceAnnotation)` is compared with the current vintage
   for that (series, period).

That yields exactly three outcomes per record:

| Comparison | Result |
| --- | --- |
| No row for the period | Insert as revision 0 |
| Hash matches | Write nothing, count as unchanged |
| Hash differs | Supersede the current vintage, insert the next revision |

The old value is never overwritten. `UQ_Observation_Current` — unique on (series, period) where
`IsCurrent = 1` — makes a botched revision fail loudly instead of silently double-counting a
period. The annotation is inside the hash on purpose: BLS flips a footnote to `R` without always
moving the number, and that transition is itself meaningful.

Values are hashed with `"G29"` formatting, so `333.95` and `333.950` — the same number — do not
register as a revision when a publisher changes its formatting.

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

Authentication (FR-9) and the AI orchestration layer (FR-13 – FR-16) are the remaining
backend work for Phase 4.
