# SECMC-Internship

## Project Overview

This repository contains the Scope of Work for a Data Intelligence Platform project. The platform is designed to automatically collect data from a designated online source on an hourly schedule, store it in Microsoft SQL Server, and expose it through analytics dashboards and a natural-language AI query assistant.

## Key Objectives

- Automate hourly data collection with failure logging and deduplication
- Retain full historical snapshots for time-series analysis
- Expose data through a .NET Web API and interactive dashboards
- Enable natural-language questions to be translated into SQL queries and answered by AI
- Keep secrets outside source control and implement role-based access for protected endpoints

## Functional Scope

- Hourly scheduled data collection from a public source
- Structured, versioned storage in SQL Server
- .NET backend with API and worker service
- React/Next.js dashboards with KPIs, trend charts, and filters
- AI assistant for NL-to-SQL translation and natural-language answers

## Technology Stack

- Backend: .NET 8 / ASP.NET Core
- Database: Microsoft SQL Server
- ORM: Entity Framework Core
- Frontend: React or Next.js
- AI: LLM API integration for NL query translation
- CI/CD: GitHub Actions or equivalent

## Notes

The full Scope of Work is available in `Scope_of_Work_Data_Intelligence_Platform.pdf`. The current SOW is Phase 2 and includes open items for finalizing the data source and confirming the team size/timeline.
