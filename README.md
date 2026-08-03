# SECMC-Internship

## Project Overview

This repository contains the Data Intelligence Platform: a system that automatically
collects data from a designated online source on an hourly schedule, stores it in
Microsoft SQL Server, and exposes it through analytics dashboards and a natural-language
AI query assistant.

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

Project scaffolding only. Both applications build and run; no functional requirement is
implemented yet. Two SOW items remain open before Phase 3 requirements gathering
(SOW 0.1):

- The designated data source is still `[DATA SOURCE — TBD]`.
- Schema design, architecture doc, and risk log are Phase 3 deliverables.

## Team

| Role | Owner |
| --- | --- |
| Project Sponsor / Reviewer | Jalab Khan |
| .NET / Backend Developer | Rafay Shahid |
| Frontend Developer & Database Owner | Sahil Kotak |
| QA / DevOps (shared) | Rafay & Sahil |
