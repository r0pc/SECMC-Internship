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
| AI | LLM API for NL-to-SQL translation and NL answers (provider TBD) |
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
| AI query assistant (FR-13 – FR-16) | Not started. Schema and audit tables exist |
| Frontend dashboards | Not started |

Known gaps worth tracking:

- The `analytics.*` views and least-privilege roles exist only in
  [docs/database-schema.sql](docs/database-schema.sql), not in the EF migration, so a
  migration-built database lacks them.
- The API host registers one read-write `DbContext`. The AI assistant's separate read-only
  connection (SOW 9, Risk 3) is not wired up — it belongs with the SQL-safety work.
- The Scope of Work still describes a single scraped source; it needs refreshing against the
  signed-off API sources, and against the narrowing to one CPI series and the current year of SOFR.
- Architecture document and risk log (Phase 3 deliverables) are not started.
- No performance measurement has been taken against the 3-second dashboard budget; there is no
  seeded staging environment to take it in yet.

## Team

| Role | Owner |
| --- | --- |
| Project Sponsor / Reviewer | Jalab Khan |
| .NET / Backend Developer | Rafay Shahid |
| Frontend Developer & Database Owner | Sahil Kotak |
| QA / DevOps (shared) | Rafay & Sahil |
