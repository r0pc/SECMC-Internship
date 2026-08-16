# Backend — Data Intelligence Platform

.NET 10 / ASP.NET Core solution, laid out in the layers described in SOW 4.

> The SOW specifies .NET 8. We build on **.NET 10** instead: .NET 9 is an STS release that
> left support in May 2026, and .NET 8's LTS window closes in November 2026 — about a month
> after go-live. .NET 10 is the current LTS, supported through November 2028.

## Current implementation status

The platform's core backend, data collection, database, dashboard APIs, AI assistant, chat
history, token tracking and authentication (FR-9) are implemented. Every endpoint but `/health`
and the login now requires a bearer token, roles gate what each one reaches, and the assistant
records questions against the account that actually asked them rather than a placeholder.

## Projects

| Project | Layer | Responsibility |
| --- | --- | --- |
| `src/DataIntelligence.Core` | Domain | Entities, DTOs, interfaces. No outward dependencies. |
| `src/DataIntelligence.Infrastructure` | Data / Integrations | EF Core `DbContext` and migrations, source collector, LLM client. |
| `src/DataIntelligence.Api` | Presentation | REST endpoints for the dashboards and the AI assistant. |
| `src/DataIntelligence.Worker` | Scheduling | Hosted service running the hourly collection cycle (FR-8). |
| `tests/DataIntelligence.UnitTests` | Test | Services, validation, NL-to-SQL logic (SOW 11.1). |
| `tests/DataIntelligence.IntegrationTests` | Test | Collector → database → API flow (SOW 11.1). |

Dependency direction is `Api`/`Worker` → `Infrastructure` → `Core`. The API and the Worker are
separate deployable units (SOW 4.2) but can share a host in early phases.

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

# Hourly collection, until stopped (FR-1, FR-8)
dotnet run --project src\DataIntelligence.Worker

# One cycle over every enabled source, then exit
dotnet run --project src\DataIntelligence.Worker -- --once

# Load history, then exit
dotnet run --project src\DataIntelligence.Worker -- --backfill              # both datasets
dotnet run --project src\DataIntelligence.Worker -- --backfill-cpi          # CPI only
dotnet run --project src\DataIntelligence.Worker -- --backfill-sofr         # SOFR only
dotnet run --project src\DataIntelligence.Worker -- --backfill --from 2000  # CPI from 2000
```

`--once` collects immediately rather than waiting for the next scheduled slot, prints what each
source did, and shuts down. Use it for a manual collection, a smoke test after deployment, or a
backfill. The run is recorded with `triggerType = Manual`, so the collection log distinguishes it
from a cycle the timer produced.

Without `--once` the Worker waits for the next boundary — with the default hourly interval and
clock alignment that is the top of the next hour, so it can sit idle for up to an hour before
doing anything. That is correct for a service and unhelpful when you want data now.

The backfill flags load the history the scheduled cycle never asks for. The cycle requests a
narrow, recent window — two years of CPI, the current year of SOFR — because that is what the
dashboards read; re-requesting decades of settled figures every hour would be absurd.

| Flag | Loads | Requests |
| --- | --- | --- |
| `--backfill` | Both | ~7 |
| `--backfill-cpi` | CPI, 1913 to now (~1,474 figures) | 6 |
| `--backfill-sofr` | SOFR, 2 Apr 2018 to now (~2,083 days) | 1 |

`--from <year>` sets the first **CPI** year and applies to `--backfill` and `--backfill-cpi`. It
is refused with `--backfill-sofr`, and refused on its own, rather than being silently ignored —
SOFR has one start date and no chunking, so there is nothing for it to choose.

CPI is chunked because BLS caps a request at 20 years; SOFR is not, because the NY Fed endpoint
takes an arbitrary range and returns the whole series in one ~450 KB response. Every request is
its own run in the collection log, so a failure part way through says exactly what landed, and
re-running is safe: figures already stored hash as unchanged and write nothing.

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

### AI query assistant (FR-13 – FR-16)

| Endpoint | Purpose |
| --- | --- |
| `POST /api/assistant/ask` | A question in, a natural-language answer out, with the SQL that produced it, its parameters, the model's explanation, and the rows it returned |
| `GET /api/assistant/sessions` | The caller's own conversations, newest first, each with its running token total |
| `GET /api/assistant/sessions/{sessionId}` | One conversation, replayed turn by turn |
| `GET /api/assistant/queries` | The audit log, newest first. `rejectedOnly`, `outcome`, `userId`, `fromUtc`, `toUtc`, paged. Administrator only |
| `GET /api/assistant/queries/{id}` | One audit record in full. Administrator only |

The round trip is question → SQL → **validate** → execute read-only → results back to the model →
answer. It is natural-language-to-SQL, not retrieval over documents: the data is numeric time
series, so the model writes a query rather than reading passages.

A question is capped at **100 words** — one thing at a time. Longer is a `400` naming the count,
not a truncation, because silently answering half a question is worse than declining it.

#### The two models

The assistant offers two, and the choice is per question — `"model": "Cloud"` or `"model":
"Local"` on `/assistant/ask`. Whichever answered is recorded on the turn and returned with the
answer, so a transcript says which one produced each figure.

| | Setting | Default | The trade |
| --- | --- | --- | --- |
| **Cloud** | `Assistant:Model` | `deepseek-v4-flash` | The default. Better SQL, answers in seconds, billed per token, and the question leaves the machine |
| **Local** | `Assistant:Local:Model` | `qwen3.5:4b` | Served by [Ollama](https://ollama.com) on the API's own machine. No key, no per-token cost, nothing leaves the box — and a small model writes worse SQL and refuses more often |

The local option is **not** configured to `qwen3.5:2b` and should not be lightly set back to it:
the 2B model could not resolve a date the question itself stated, answering *"what is the cpi in
june 2025"* with a query for the current month. That failure is invisible from outside — a query
for the wrong period returns nothing, and nothing then gets explained as though the platform did
not hold the figure. The reasoning behind every `Assistant:Local:*` value is documented inline in
[appsettings.json](src/DataIntelligence.Api/appsettings.json); `Assistant:Local:ContextTokens` in
particular is the setting that decides whether the local option works at all.

Leave Ollama off entirely and nothing degrades: `Cloud` is the default and is unaffected. Choosing
`Local` on a machine with no server running is **not** quietly answered by the cloud model — that
would attribute a hosted answer to the choice the user did not make. It is a `503` naming the fix,
and the two ways of getting there are told apart: nothing listening on the port says to start
Ollama, while a `404` says the server is up and lacks that model, with the `ollama pull` command
for it. Model names include the tag — `qwen3.5` and `qwen3.5:4b` are different requests.

#### Conversations and token tracking

A conversation is one row in `ai.AssistantSession`, and its `TranscriptJson` column holds **the
whole chat as a single JSON document** — every turn, in order, with the question, the SQL it
became, the bound parameters, the outcome, the answer, which model produced it, token usage and
timings. That document is the record: `ai.AssistantQuery` is gone, and what is not in the
transcript was not kept.

Result rows are deliberately excluded. A turn may return up to 2,000 of them, and a document
rewritten on every turn would grow by megabytes to hold what re-running the stored statement
reproduces exactly.

Two costs come with keeping a conversation as a document rather than as rows, and both are real:

- **Writes are read-modify-write of the whole thing.** Two questions answered against one session
  at the same instant will both load it, and the later save drops the other's turn. A row insert
  could not lose a turn.
- **Nothing in it can be indexed.** The review queue shreds every transcript in the table with
  `OPENJSON` on each call, where it used to seek an index.

Turn ids come from the `ai.AssistantTurnId` sequence rather than an identity column, because they
have to be unique across sessions: this is the id `GET /assistant/queries/{id}` takes, and
numbering turns 1..n per conversation would have every session claiming turn 1.

`TotalTokens` on the session is a denormalisation — the turns' own totals added up, rewritten on
every save. It exists because the chat list shows it for every conversation a user has, and
deriving it there would mean shredding each of their transcripts to draw a list. `AssistantService`
is the single writer; anything that edits `TranscriptJson` without going through it leaves this
stale and nothing in the database will notice. `NULL` means no turn reported usage — *not known*,
as opposed to a conversation that cost nothing.

#### What the model is shown

The model is shown the schema it may query, and that description is **read from the database** —
the views and their columns come from `INFORMATION_SCHEMA`, filtered to the allow-list, so it
cannot drift from what is actually there. Only the parts metadata cannot supply are hand-written:
that `M13` is an annual average rather than a thirteenth month, that volume is in billions, that
the dialect is T-SQL. Every one of those lines exists because the model got something wrong
without it.

Two of those parts are worth naming, because both turned refusals into answers:

- **Today's date and the coverage window**, rebuilt per question rather than cached. Without a
  notion of "now" the model cannot resolve *"the average SOFR rate last month"* — not because the
  query is hard, but because "last month" has no referent — so it correctly refuses, which is the
  right failure and a useless answer. The coverage window matters for the same reason in reverse:
  a publisher releases in arrears, so "last month" and "the most recent month with data" are often
  different months, and a model told only the date confidently returns an empty result.
- **A vocabulary mapping** from the words questions are actually asked in to the columns that
  answer them — "inflation" to `YearOverYearPct`, "interest rate" to `RatePercent`, "is collection
  working" to `vw_CollectionHealth`. None of these appear as a column name. Before the mapping,
  *"how has inflation moved over the last 6 months?"* was refused while *"what is the CPI trend
  this year?"* was answered from the same view.

What that prompt costs, and the four things that keep it down, are in the root
[README](../README.md#prompt-size). The eval suite that makes any of it falsifiable — remove a
rule, see which cases fail — is [tools/prompt-eval](../tools/prompt-eval/README.md).

#### Between the model and the database

The model returns a **parameterised** statement, its parameter values, and its own explanation of
what it wrote. The values never enter the SQL text — they are bound to `SqlCommand`, so a value
containing SQL is data and is never parsed. All three are stored, because a reviewer reading a
surprising query needs to know what the model believed it was writing; the statement and the
explanation disagreeing is itself the finding.

Two independent controls stand between the model and the database, and neither is sufficient alone:

- **`ISqlSafetyValidator`** — a single `SELECT`, one statement, and only the nine `analytics.*`
  views. Comments are stripped and string literals blanked *before* inspection, so a keyword
  hidden in a comment cannot dodge the scan and a literal containing the word `delete` cannot
  trigger a false rejection. `CROSS APPLY` is checked alongside `FROM` and `JOIN` — it introduces
  a table expression the same way — and an unqualified name is rejected rather than unmatched.
  Placeholders and supplied values must be the same set: a placeholder with no value would fail at
  the database, and a value with no placeholder means the statement is not the one described.
- **Execution as a restricted principal.** Whichever of these is configured:

  | | How | When |
  | --- | --- | --- |
  | Dedicated connection | `ConnectionStrings:DataIntelligenceDbReadOnly`, a login in the `di_ai_readonly` role | Strongest. Needs mixed-mode authentication, since a role has no login of its own |
  | `EXECUTE AS USER` | `Assistant:ExecuteAsUser`, a database user in that role | Works under Windows authentication, no server reconfiguration. The default |

  Both end at a principal holding `SELECT` on `analytics.*` and `DENY SELECT` on `sec` and `ai`.
  Configure neither and the assistant **refuses to execute** rather than falling back to the
  application's own connection — a fallback there would run model-written SQL with `INSERT` and
  `UPDATE` rights and say nothing about it.

Results are capped twice: the validator injects `TOP (2000)` when the model set no limit, and the
executor stops reading at the same ceiling — an injected `TOP` binds to one `SELECT`, so a `UNION`
would otherwise return the cap per branch.

Every turn is written to the transcript with its outcome, so a rejected query is on the record
with the reason it was refused (NFR Auditability). `CK_AssistantSession_TranscriptJson` is the
database's own backstop against a malformed document: checked on write rather than trusted from
the application, because otherwise a broken transcript is found by whoever reads it back, long
after the write that broke it.

`GET /api/assistant/queries?rejectedOnly=true` is the review queue, and it deliberately excludes
`NotADataQuestion`. A greeting and an attempt to read the password hashes both produce no SQL, but
only one of them is a finding — filed together, the volume of the first buries the second, which is
exactly what the queue exists to prevent.

The cloud model needs `Assistant:ApiKey`. Without it the API still starts and the dashboards still
work — `/api/assistant/ask` answers `503` naming the missing setting. A missing LLM key is not a
reason for the whole API to be down.

### Authentication and authorization (FR-9)

| Endpoint | Purpose |
| --- | --- |
| `POST /api/auth/login` | Email and password in, a bearer token and its expiry out. Anonymous |
| `GET /api/auth/me` | The caller's own account, read from the token's claims |
| `POST /api/auth/password` | Change your own password |
| `GET /api/users`, `GET /api/users/{id}` | Accounts. Administrator only |
| `POST /api/users` | Create an account. Administrator only |
| `PATCH /api/users/{id}` | Display name, active state, roles. Administrator only |
| `POST /api/users/{id}/password` | Set someone else's password. Administrator only |
| `GET /api/users/roles` | The three role names, for populating a form |

`POST /api/auth/login` exchanges an email and password for a bearer token; every other endpoint
requires one. The two exceptions are that call itself and `/health`, which is anonymous because a
load balancer cannot sign in and "the process is up and can see its database" is not worth a
credential. The requirement is declared once, on the `/api` group, so an endpoint added later is
protected by being added rather than by someone remembering to protect it.

Three roles, seeded by the migration, and each tier's roles are a subset of the one below it:

| Policy | Roles | Reaches |
| --- | --- | --- |
| `ReadDashboards` | all three | Dashboards, catalogue, observations, sources, collection log |
| `UseAssistant` | Administrator, Analyst | `POST /assistant/ask` and the caller's own conversations |
| `Administer` | Administrator | `/api/users`, `PATCH /api/sources/{id}`, the assistant audit log |

Viewers are kept out of the assistant deliberately: a question costs model tokens and writes an
audit record naming who asked, which is more than "read-only dashboards" grants. The audit log is
administrator-only because it exposes more than the rest of the API does — every question every
user has asked, and the SQL it became.

Tokens are signed with HMAC-SHA256 (`Auth:SigningKey`) and live eight hours — a working day, so an
analyst signs in once in the morning. That is affordable only because they are **revocable**: each
token carries the account's security stamp, and `OnTokenValidated` re-reads the account and
compares it on every request. Disable someone, change their password, or change their roles, and
their open sessions stop working on their next call rather than eight hours later. Without that
check the lifetime would have to be minutes, with refresh tokens to match.

Passwords are hashed by ASP.NET Identity's `PasswordHasher<T>` — PBKDF2, in the v3 format
`docs/database-schema.sql` specifies for the column. Nothing here computes a hash itself. A
sign-in for an unknown address still pays for a verification against a throwaway hash, so the
timing does not reveal whether an address has an account here, and the response is identical
either way.

Accounts are created by administrators; there is no self-registration. The first one comes from
`Auth:SeedAdministrator` at startup, which is the bootstrap problem and nothing else — it does
nothing once any active administrator exists. Two edits are refused: deactivating or demoting
yourself, and removing the last active administrator, because both leave a platform nobody can
administer and no endpoint that could fix it.

`ai.AssistantSession.UserId` now has its foreign key to `sec.AppUser`, which
`docs/database-schema.sql` always specified and the audit-log migration had to leave out. The
migration that adds it back-fills a deactivated, no-login account for any user id already in the
assistant's history, so questions asked under the old placeholder id keep their record instead of
blocking the constraint. Accounts are retired with `IsActive`, never deleted: the rows pointing at
them are the record of what was asked.

## Configuration and secrets

Connection strings and API keys are never committed (SOW 3 — Security). `appsettings.json`
holds empty placeholders; supply real values through user secrets locally:

```powershell
dotnet user-secrets set "ConnectionStrings:DataIntelligenceDb" "<connection string>" --project src\DataIntelligence.Api
dotnet user-secrets set "ConnectionStrings:DataIntelligenceDb" "<connection string>" --project src\DataIntelligence.Worker
```

The API **will not start** without `Auth:SigningKey` (FR-9). That is deliberate: every endpoint
needs it, so a process that booted without one would serve nothing but 401s while looking healthy.
On a fresh machine, set it and the bootstrap administrator:

```powershell
# 48 random bytes, base64 — 64 characters, comfortably over the 32-character floor.
# RNGCryptoServiceProvider, not Get-Random: the latter is a deterministic PRNG seeded from the
# system clock, which is fine for a sample and is not what a signing key should be drawn from.
$bytes = New-Object byte[] 48
$rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
$rng.GetBytes($bytes); $rng.Dispose()

dotnet user-secrets set "Auth:SigningKey" ([Convert]::ToBase64String($bytes)) --project src\DataIntelligence.Api
dotnet user-secrets set "Auth:SeedAdministrator:Email" "you@example.com" --project src\DataIntelligence.Api
dotnet user-secrets set "Auth:SeedAdministrator:DisplayName" "Your Name" --project src\DataIntelligence.Api
dotnet user-secrets set "Auth:SeedAdministrator:Password" "<at least 12 characters>" --project src\DataIntelligence.Api
```

Start the API once, sign in, change that password, and remove the `SeedAdministrator` settings —
they are ignored from then on anyway, since an active administrator exists.

Use environment variables in deployed environments.

| Setting | Where | Notes |
| --- | --- | --- |
| `ConnectionStrings:DataIntelligenceDb` | Api, Worker | SQL Server connection string. |
| `Auth:SigningKey` | Api | HMAC key every access token is signed with, 32 characters minimum. **Never committed**, and **required** — the API refuses to start without it. Changing it signs everyone out, which is the lever to pull if it leaks. |
| `Auth:TokenLifetimeMinutes` | Api | 480. Long because tokens are revocable on every request; see the authentication section. |
| `Auth:Issuer` · `Auth:Audience` | Api | Written into each token and required to match when one is presented. |
| `Auth:SeedAdministrator:*` | Api | `Email`, `DisplayName`, `Password` for the first administrator, created at startup when the platform has none. **Never committed.** Ignored once an active administrator exists. |
| `Cors:AllowedOrigins` | Api | Frontend origins. Defaults to `http://localhost:3000` in Development. |
| `Collection:Bls:ApiKey` | Api, Worker | BLS registration key. **Never committed** — user secrets or environment only. Optional: unregistered v2 calls still work under a much smaller daily quota, so an absent key degrades rather than stops collection. |
| `Collection:IntervalMinutes` | Worker | 60 (hourly, FR-1). Cycles align to the wall clock unless `AlignToClock` is false. |
| `Collection:Bls:YearsOfHistory` | Worker | 2 — current and previous calendar year per request. |
| `ConnectionStrings:DataIntelligenceDbReadOnly` | Api | Optional. A login in the `di_ai_readonly` role. Leave empty on a Windows-authentication-only instance, where that role has no login to authenticate as. **Never the app's read-write login** — that removes the second of the two controls without changing anything visible. |
| `Assistant:ExecuteAsUser` | Api | Database user switched to before generated SQL runs, used when the above is empty. Defaults to `di_ai_user`, created by section 6 of `docs/database-schema.sql`. Set both to empty and the assistant refuses to execute at all. |
| `Assistant:ApiKey` | Api | DeepSeek platform key. **Never committed.** Required for the `Cloud` model; without it `/assistant/ask` answers 503 for that choice. |
| `Assistant:BaseUrl` · `Assistant:Model` | Api | `https://api.deepseek.com/` and `deepseek-v4-flash`. The id carries no vendor prefix — `deepseek/deepseek-v4-flash` is the OpenRouter spelling and is rejected here. |
| `Assistant:RequestTimeoutSeconds` · `SqlExecutionTimeoutSeconds` · `MaxOutputTokens` | Api | 30 / 10 / 3000. The response-time budget FR-15 asks for. |
| `Assistant:ReasoningEffort` | Api | `none` — the model's thinking off. Measured, not assumed: on one CPI question `deepseek-v4-flash` spent 147 of 208 completion tokens deliberating before writing essentially the statement it writes with thinking off. Empty omits the field, for a gateway that rejects it rather than ignoring it. |
| `Assistant:HistoryTurns` · `VerbatimHistoryTurns` | Api | 6 / 2. How many prior turns are replayed at all, and how many of those go in whole rather than compressed to a line. |
| `Assistant:MaxSummaryRows` | Api | 60. Above this a result is *described* to the answering model — min, max and mean per column across every row, plus the five at each end — rather than listed. Accuracy before economy: a model shown the first 60 days of a year of SOFR answers for 60 days, confidently and wrongly. |
| `Assistant:Local:*` | Api | The second model. `BaseUrl` `http://localhost:11434/`, `Api` `Ollama`, `Model` `qwen3.5:4b`, `ContextTokens` 16384, `ReasoningEffort` `none`. Nothing here is a secret, so unlike `ApiKey` it is committed. Every value is documented inline in `appsettings.json`, including why the native Ollama API is used rather than its OpenAI-compatible one. |

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
[src/DataIntelligence.Worker/appsettings.json](src/DataIntelligence.Worker/appsettings.json), and
every `Assistant:*` setting in
[src/DataIntelligence.Api/appsettings.json](src/DataIntelligence.Api/appsettings.json).

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
  a reference date, which is why the key is (year, period code) and not the date. The request
  must set `annualaverage=true` or the API returns `M01`-`M12` only and no annual row ever
  arrives — a gap that looks like missing data rather than an unasked question.
- **The semiannual figures are not available from the API.** `S01`/`S02` appear in the CSV
  download's HALF1/HALF2 columns, and the table and adapter handle them, but no request
  parameter makes api.bls.gov serve them for this series — confirmed by probing it. A collected
  database therefore holds monthly and annual rows only; the ~85 semiannual figures would need
  the CSV route.
- **The SOFR rate-type filter is defensive, not routine.** `search.json` under
  `/rates/secured/sofr/` returns SOFR alone — verified against the live API — so the filter
  normally rejects nothing. It matters because the same record shape carries EFFR, OBFR, TGCR and
  BGCR elsewhere: the CSV download has all five, and the rate endpoints differ only by path. A
  rejection here means the URL or the contract moved.

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

Ten tables across four schemas — `collect`, `core`, `sec`, `ai` — and nine `analytics.*` views.
The views and the least-privilege roles exist **only in the script**, not in any migration, so a
migration-built database lacks them, including the views the assistant queries. Run sections 5
and 6 of the script by hand after `database update` until that is closed.

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
not. Field names are confirmed by running against the live endpoints, which has been done; a
stored `collect.RawPayload` can be decompressed to check what a publisher actually sent.

Two differences between the CSV downloads and the JSON APIs are worth knowing, both found that
way: the SOFR API returns SOFR rows only where the CSV carries all five rates, and it sends no
footnote field where the CSV has a Footnote ID column, so `FootnoteId` is always null from the
API path.
