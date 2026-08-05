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
| `GET /api/dashboard/summary` | Catalogue counts, span of stored history per dataset, and per-source collection health — the whole landing page in one call. `?windowDays=` (default 30) |
| `GET /api/dashboard/kpis?seriesKeys=cpi,sofr` | Latest value, change since the previous release, change year over year. Up to 20 series |
| `GET /api/dashboard/trend?seriesKeys=cpi,sofr&from=&to=&granularity=` | Trend lines over a shared range. Up to 10 series |

`granularity` defaults to `Auto`: points stay unbucketed until the range would produce more of
them than a chart can usefully draw, then widen to `Month`, `Quarter`, or `Year`. Where a bucket
holds several observations, `value` is the mean and `minimum`/`maximum` carry the spread. The
width actually used comes back on each line.

Units travel with each line and are **not** interchangeable — SOFR volume is in billions of
dollars, CPI is an index. Two series with different `unit` values must not share an axis.

### Catalogue (FR-7)

| Endpoint | Purpose |
| --- | --- |
| `GET /api/sources`, `GET /api/sources/{id}` | Publishers, with the number of series they provide |
| `PATCH /api/sources/{id}` | Polling settings only: enabled state, interval, timeout, retries, user agent, terms link |
| `GET /api/series` | Filter by `dataSourceId`, `dataset`, `search`; paged |
| `GET /api/series/{key}` | One series, with its latest value |

A **series key** names one chartable measure. There are seven, fixed in code:

| Key | Reads |
| --- | --- |
| `cpi` | `core.CpiObservation.IndexValue` — BLS series `CUUR0000SA0` |
| `sofr` | `core.SofrDailyRate.RatePercent` |
| `sofr.volume` | `VolumeUsdBillions` |
| `sofr.p1` · `sofr.p25` · `sofr.p75` · `sofr.p99` | The rate distribution across the underlying trades |

The six SOFR keys read six columns of the *same* row: a business day is one record, and its
measures are columns rather than rows. Keys are case-insensitive and stable — they are what a
saved dashboard view stores.

The catalogue is **read-only**, and there are no categories. Each dataset is its own table with
its series pinned by a CHECK constraint, so what exists is a fact about the schema rather than
rows that could be edited into disagreement with the collector.

### Time series and collection log

| Endpoint | Purpose |
| --- | --- |
| `GET /api/series/{key}/observations` | `from`, `to`, `periodType`, `includeRevisions`, `asOfUtc`, `sort`, paging |
| `GET /api/collection/runs` | Every cycle, newest first. `failuresOnly=true` for the operations panel |
| `GET /api/collection/runs/{id}` | One run |
| `GET /api/collection/health` | Rolling-window success rate and consecutive failures per source |

Three behaviours worth knowing before wiring up a chart:

- **Current values only, and for CPI, monthly figures only.** BLS publishes an annual average
  (`M13`) and two semiannual averages alongside the twelve months, and they are averages *of*
  those months — plotted unfiltered the annual row becomes a thirteenth month, and aggregated it
  counts the year twice. Pass `periodType=Annual` or `Semiannual` to read them. Ignored for SOFR,
  where every row is one business day.
- **Revisions are additive, not destructive** (FR-4). `includeRevisions=true` returns superseded
  vintages; `asOfUtc` reads what the platform believed at an instant — *"what did we think June's
  CPI was, on 15 July?"* Both are off by default, so a chart plots each period once.
- **Observations are read-only.** They are written solely by the collector; an endpoint that
  could edit them would undo the trustworthiness the append-only design exists to provide.

Paging is uniform: `?page=` (1-based) and `?pageSize=`, answered with `items`, `page`,
`pageSize`, `totalCount`, `totalPages`, `hasNextPage`, `hasPreviousPage`. Out-of-range values are
clamped rather than rejected — 500 per page for catalogue lists, 2000 for observations.

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

SOFR has no window setting: the adapter asks for the current calendar year every cycle, which is
the annual extract the schema is written against and means a gap left by an outage or a late
revision is repaired by the next run.

Endpoints, cadence and enabled state are **not** configuration — they live in
`collect.DataSource`, seeded by the migration. Disable a publisher by clearing its `IsEnabled`
row (or `PATCH /api/sources/{id}`), not by editing a config file.

```powershell
dotnet user-secrets set "Collection:Bls:ApiKey" "<key>" --project src\DataIntelligence.Worker
```

Every `Collection:*` setting is documented inline in
[src/DataIntelligence.Worker/appsettings.json](src/DataIntelligence.Worker/appsettings.json).

## Data collection

Two datasets from two publishers, one table each, both over official JSON APIs — the scope,
units and history windows are documented in
[docs/README.md](../docs/README.md#what-the-platform-stores).

The pipeline (FR-1 – FR-4) runs in the Worker, once per enabled source per cycle:

```text
open run → build request → fetch (retry/backoff) → store raw payload
         → skip if body unchanged → parse → validate → dedupe/revise → close run
```

Every stage writes to `collect.CollectionRun`, so a failure is recorded with a category rather
than taking the scheduler down (FR-2), and one publisher failing cannot affect the other.

Two roles, both resolved by source code. **Everything publisher-specific lives in one
`ISourceAdapter`**, which owns both halves of the contract with the publisher — how to ask, and
how to read the answer. **Everything table-specific lives in one `IDatasetWriter`**, which owns
deduplication and the revision rule for its own natural key:

| Source | Request | Response shape | Writes |
| --- | --- | --- | --- |
| `BlsCpiAdapter` | `POST` with `{seriesid:["CUUR0000SA0"], startyear, endyear, registrationkey}` | `Results.series[].data[]`; values are **strings**, `"-"` means suppressed, footnote `"R"` means revised | `CpiObservationWriter` → `core.CpiObservation`, keyed on (year, period code) |
| `SofrAdapter` | `GET .../sofr/search.json?startDate=&endDate=` for the current year | `refRates[]`; one record becomes one row, `revisionIndicator` marks a correction | `SofrDailyRateWriter` → `core.SofrDailyRate`, keyed on effective date |

The run lifecycle is identical for both publishers and persistence is not, which is why the two
are split: keeping the shared half shared and the different half separate is the point of the
two-table schema.

Three details are load-bearing:

- **BLS returns HTTP 200 with a failure envelope**, so `status` is checked before the data —
  otherwise a quota rejection would be recorded as a healthy run that collected nothing.
- **BLS period tokens are not months.** `M13` is the annual average and `S01`/`S02` the halves,
  so `PeriodCode` is stored verbatim and `PeriodType` says what it means. `M13` and `M01` share
  a reference date, which is why the key is (year, period code) and not the date.
- **Four other rates share the SOFR payload.** EFFR, OBFR, TGCR and BGCR arrive every business
  day and are out of scope; they are logged as `UnknownSeries` rejections rather than dropped, so
  the exclusion is visible in the data instead of invisible in a filter. Expect four per day.

Adding a dataset is a new adapter, a new writer, a table, and a `collect.DataSource` row.

### Deduplication and revisions (FR-3, FR-4)

Two layers, cheapest first:

1. **Whole-response** — the SHA-256 of the body is compared with the previous run's. Identical
   means the publisher released nothing; the run succeeds without parsing anything.
2. **Per-row** — a SHA-256 over the row's measures is compared with the current vintage for that
   period. For CPI that is the index level and the footnotes; for SOFR it is *every* measure, so
   a restatement that moved only the volume still counts as one.

That yields exactly three outcomes per record:

| Comparison | Result |
| --- | --- |
| No row for the period | Insert as revision 0 |
| Hash matches | Write nothing, count as unchanged |
| Hash differs | Supersede the current vintage, insert the next revision |

The old value is never overwritten. `UQ_CpiObservation_Current` and `UQ_SofrDailyRate_Current` —
unique on the period where `IsCurrent = 1` — make a botched revision fail loudly instead of
silently double-counting. The annotation is inside the hash on purpose: BLS flips a footnote to
`R` without always moving the number, and that transition is itself meaningful.

Values are hashed with `"G29"` formatting, so `333.95` and `333.950` — the same number — do not
register as a revision when a publisher changes its formatting.

## Database

[docs/database-schema.sql](../docs/database-schema.sql) is the design of record; EF Core
migrations are the deployment mechanism. The two are verified to produce identical `collect` and
`core` schemas — every column, index, filter and check constraint — by creating a database from
each and diffing `sys.*`. The only systematic difference is cosmetic: EF renders a scalar default
as `CONVERT([tinyint],(1))` where the script writes `1`.

```powershell
# Applies migrations. Reads ConnectionStrings__DataIntelligenceDb from the environment.
$env:ConnectionStrings__DataIntelligenceDb = "<connection string>"
dotnet ef database update --project src\DataIntelligence.Infrastructure
```

Integration tests create and drop their own uniquely named database, so they need a
reachable SQL Server (SOW 11.2). They default to a local default instance; set
`DATAINTELLIGENCE_TEST_SQL` to point elsewhere.

### Accuracy against the published extracts

The sample downloads in [docs/example_data/](../docs/example_data/) are not illustration — they
are the reference dataset for a set of tests that check the collector's output against the
publishers' own files, figure by figure. `tests/Shared/PublishedData.cs` reads both CSVs and
rebuilds each publisher's JSON payload from them; both test projects link it.

| Test class | Covers |
| --- | --- |
| `PublishedDataAccuracyTests` (unit) | Every one of the 1,559 CPI cells and 146 SOFR days parses to the value, period and date the file has. No database needed |
| `PublishedDataCollectionTests` (integration) | The same figures survive validation, the writers and the schema's constraints into SQL Server |
| `PublishedDataRevisionTests` (integration) | Deduplication and revision handling at full volume — reissuing 1,559 figures writes nothing; one restated month produces exactly one revision |

These catch what a hand-written fixture cannot: a period column read one place to the left, a
value truncated at a decimal place the sample happened not to have, a row lost somewhere in a
century of history. The cases are real ones — CPI at one decimal place before 2007 and three
after, a month BLS has not released yet, a half-day with no SOFR figure, and four out-of-scope
rates interleaved with SOFR on every business day.

`TheExtractsHaveTheExpectedShape` pins the row counts. It is meant to fail when the extracts are
refreshed — check the difference is new data rather than a parsing change, then update it.

**What this does not prove:** that the JSON field names match the live APIs. The extracts are CSV
downloads, so the payloads are reconstructed from them — the figures are real, the envelope is
not. Field names are confirmed by running against the live endpoints.

## Not yet implemented

Authentication (FR-9) and the AI orchestration layer (FR-13 – FR-16) are the remaining
backend work for Phase 4.
