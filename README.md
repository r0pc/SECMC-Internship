# SECMC-Internship

## Project Overview

This repository contains the Data Intelligence Platform: a system that automatically
collects US economic data on an hourly schedule, stores it in Microsoft SQL Server, and
exposes it through analytics dashboards and a natural-language AI query assistant.

Two publishers were designated under SOW 0.1, and one dataset is collected from each:

| Source | Scope | Cadence | Stored in |
| --- | --- | --- | --- |
| **Consumer Price Index** — U.S. Bureau of Labor Statistics | Series `CUUR0000SA0`: all items, U.S. city average, not seasonally adjusted | Monthly, plus the annual and semiannual averages published alongside | `core.CpiObservation` |
| **Secured Overnight Financing Rate** — Federal Reserve Bank of New York | Rate type SOFR for the current calendar year — the rate, its volume, and four percentiles | Business-daily | `core.SofrDailyRate` |

Each dataset gets its own table. With the scope fixed at one CPI series and one rate, a generic
series registry with a shared fact table would have added a join to every query and bought
nothing back; the six SOFR measures are columns of one business day rather than six rows.

Both publish official JSON APIs, so the platform consumes those rather than scraping HTML
(SOW 9: "prefer an official API if one exists"). The full scope, units, and history
windows are in [docs/README.md](docs/README.md#what-the-platform-stores), and sample extracts of
exactly what arrives are checked in under [docs/example_data/](docs/example_data/).

The approved blueprint is
[Scope_of_Work_Data_Intelligence_Platform.pdf](Scope_of_Work_Data_Intelligence_Platform.pdf)
(Phase 2). Requirement IDs referenced in code comments and docs (FR-1, SOW 4.2, …) point
back to that document.

## Key Objectives

- Automate hourly data collection with failure logging and deduplication
- Retain full historical snapshots for time-series analysis
- Expose data through a .NET Web API and interactive dashboards
- Enable natural-language questions to be translated into SQL queries and answered by AI
- Keep secrets outside source control and implement role-based access for protected endpoints

## Repository Layout

```text
backend/    .NET 10 solution — API, worker service, domain, infrastructure, tests
frontend/   Next.js 16 app — dashboards and AI assistant UI
docs/       Phase 3 and Phase 6 documentation artifacts
```

Backend and frontend are independent applications with no build-time coupling. They are
developed, built, and deployed separately and communicate only over HTTP.

## Technology Stack

| Component | Technology |
| --- | --- |
| Backend | .NET 10 / ASP.NET Core (Web API + Worker Service) |
| Database | Microsoft SQL Server |
| ORM | Entity Framework Core 10 |
| Frontend | Next.js 16 (App Router), TypeScript, Tailwind CSS |
| AI | DeepSeek (`deepseek-v4-flash`) or a local Ollama model (`qwen3.5:2b`), chosen per question, for NL-to-SQL translation and NL answers |
| CI/CD | GitHub Actions or equivalent |

## Getting Started

Prerequisites: .NET SDK 10.x (pinned in [global.json](global.json)), Node.js 20+, and
SQL Server.

```powershell
# Backend — http://localhost:5063
cd backend
dotnet build DataIntelligence.sln
dotnet run --project src\DataIntelligence.Api

# Frontend — http://localhost:3000
cd frontend
npm install
Copy-Item .env.example .env.local
npm run dev
```

Per-side detail lives in [backend/README.md](backend/README.md) and
[frontend/README.md](frontend/README.md).

### Optional: the local assistant model

The assistant can answer from a model running on your own machine instead of the hosted
one, chosen per question in the chat. All it needs is [Ollama](https://ollama.com) running
with the model pulled:

```powershell
ollama pull qwen3.5:2b
ollama serve
```

No server-side configuration. The API talks to Ollama's native `/api/chat` specifically so
it can set the context window per request (`Assistant:Local:ContextTokens`, default 16384).
That is not a tuning detail — Ollama loads a model at **4096 tokens** by default and the
schema prompt alone was roughly 3,900 at the time, leaving about 200 for the answer, so any
longer query used to be cut off mid-JSON and recorded as `RejectedUnreadableResponse` after
a wait of minutes. Measured on one question and one model, changing only this: at 4096 the
reply stopped on `length` with the token total pinned at exactly 4096 and would not parse;
at 16384 it finished and parsed. Ollama's *OpenAI-compatible* endpoint accepts the same
`options.num_ctx` and silently ignores it, which is why the native API is used instead.

The prompt has since been trimmed to roughly 3,300 tokens and the conversation replayed
with it compressed (see [Prompt size](#prompt-size)), which widens the same margin rather
than replacing it: a long answer on a long conversation still needs the larger window.

Point `Assistant:Local` at another OpenAI-compatible server (llama.cpp, LM Studio, vLLM) by
setting `Api` to `OpenAi`; its context then has to be raised on the server, since that
endpoint gives no way to ask.

Expect it to be slow — minutes per question on a CPU, mostly spent reading the prompt — and
to write worse SQL than the hosted model. Nothing leaves the machine and nothing is billed,
which is the trade. Leave Ollama off entirely and the cloud model, which is the default, is
unaffected.

### Prompt size

Every question costs two model calls — one to write the SQL, one to describe the result —
and each carries the schema, the conversation so far and, for the second, the rows that came
back. That total is billed at the hosted gateway and competes for the context window at the
local one, where running out of room is not an expense but a failed answer. Four things keep
it down, none of which changes what the assistant can answer:

| | What it does | Setting |
| --- | --- | --- |
| **Schema prompt** | States the rules and worked examples the model needs without the prose explaining *why* each exists — that reasoning lives in the source comments and git history, where a reader can reach it and the model does not have to pay for it. Every fact, exact value and example survives. | — |
| **Conversation** | The two most recent turns are replayed whole; older ones are compressed to a line each — the question, the views that answered it, the values it was bound to. That is everything a follow-up like *"and the year before that?"* resolves against; what is dropped is the statement, which is the expensive part and the part nothing points back at. | `Assistant:VerbatimHistoryTurns` |
| **Result set** | Sent columnar — names once, then a positional array per row — instead of repeating every column name on every row. A result over 60 rows is *described* rather than listed: min, max and mean per column computed across every row, plus the five at each end. That one is accuracy before economy — a model shown the first 60 days of a year of SOFR and asked how far the rate moved answers for 60 days, confidently and wrongly, where a statistic over the whole result is exact however long the series is. The browser still receives every row, keyed as before. | `Assistant:MaxSummaryRows` |
| **Reasoning** | Off. The local model would otherwise spend its entire budget deliberating and return nothing; the hosted one has the setting available for a gateway whose model thinks, since thinking tokens are billed and nothing here benefits from them. | `Assistant:ReasoningEffort`, `Assistant:Local:ReasoningEffort` |
| **Prompt order** | Everything fixed — the output contract, the views, the rules and examples — sits at the front, and the only part that moves, today's date and the coverage window, at the very end. Both providers reuse a prompt by its prefix and stop at the first byte that differs, so this leaves about 2,700 tokens reusable and roughly 200 that are not. A hosted gateway bills a cached prefix at a fraction of the input rate; a local server can skip re-reading one whose KV cache it still holds, which on a CPU is most of the wait. | — |
| **Coverage window** | Sent to the answer step only when the result is empty, which is the only case it explains. Saves a database round trip as well as the tokens. | — |

Measured against the audit log's own token counts — a greeting costs one model call and
nothing else, so it reads the schema prompt directly — this text runs at about 3.3 characters
per token rather than the 4 a prose estimate assumes. On that basis a greeting or other short
turn went from about 4,320 tokens to 3,600, and a follow-up several turns into a conversation
over a result of a few hundred rows from about 13,200 to 3,900.

Below roughly 3,600 tokens a question there is little left to take: the views, the rules and
the worked examples are ~3,300 of it, they are re-sent by definition, and every line of them
was added because the model got something wrong without it. The one remaining idea is not to
send them at all when the question plainly is not a data question — a greeting currently pays
the full schema prompt to be classified as a greeting — which trades a cheap pre-filter
against the risk of misreading a real question as chatter.

## Deviations from the SOW

| SOW says | We use | Why |
| --- | --- | --- |
| .NET 8 | .NET 10 | .NET 9 left support in May 2026 and .NET 8's LTS window closes Nov 2026, roughly a month after go-live. .NET 10 is the current LTS, supported to Nov 2028. |
| React or Next.js | Next.js | SOW 5 left the choice to Phase 3; the SSR/static build matches the independent-deployment model in SOW 4.2. |

## Current Status

The data source gate (SOW 0.1) is closed — CPI and SOFR are signed off — and the collection
pipeline is running against both live APIs.

| Area | State |
| --- | --- |
| Database schema + ERD | Delivered. DDL verified against SQL Server, loaded with the real published extracts, and diffed against the EF migration |
| Data collection (FR-1 – FR-4, FR-8) | Implemented. Hourly, per source, with failure categories, deduplication and revision history |
| Read API (FR-7, FR-10 – FR-12) | Dashboard, catalogue, observation and collection-log endpoints |
| Authentication (FR-9) | Not started — every endpoint is currently anonymous |
| AI query assistant (FR-13 – FR-16) | Delivered. Question to SQL, safety validation, read-only execution, natural-language answer, and a full audit record of every turn |
| Frontend dashboards (FR-10 – FR-12) | Delivered. Dashboard, series catalogue, series detail, collection log and sources, server-rendered against the live API |
| Frontend assistant (FR-13 – FR-16) | Delivered. Chat with resumable conversations, the generated SQL behind a disclosure, and per-chat token cost |

The assistant answers only from collected data. A question becomes a read-only `SELECT` over the
`analytics.*` views, the statement is validated before it runs, and the answer is written from the
rows that came back — a question that cannot be expressed against the data is refused rather than
answered from the model's own memory. Every turn is recorded, refusals included: the question, the
SQL, the parameters, the outcome, the answer, token usage and timings (NFR Auditability). Both are
visible in the UI — the SQL behind each answer, and what each conversation has cost in tokens.

Timestamps in the database are Pakistan Standard Time (UTC+05:00) wall-clock readings, named with
an `...AtPkt` suffix. The frontend offers a light/dark theme toggle, remembered per browser and
defaulting to the operating system's setting.

Known gaps worth tracking:

- The `analytics.*` views and least-privilege roles exist only in
  [docs/database-schema.sql](docs/database-schema.sql), not in the EF migration, so a
  migration-built database lacks them — including the views the assistant queries.
- The Scope of Work still describes a single scraped source; it needs refreshing against the
  signed-off API sources, and against the narrowing to one CPI series and the current year of SOFR.
- Architecture document and risk log (Phase 3 deliverables) are not started.
- Nothing in CI runs against `frontend/`, and the OpenAPI document is not published as a versioned
  artifact under `docs/`.
- No performance measurement has been taken against the 3-second dashboard budget; there is no
  seeded staging environment to take it in yet.

## Team

| Role | Owner |
| --- | --- |
| Project Sponsor / Reviewer | Jalab Khan |
| .NET / Backend Developer | Rafay & Sahil |
| Frontend Developer & Database Owner | Sahil & Rafay |
| QA / DevOps (shared) | Rafay & Sahil |
