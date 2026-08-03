# Frontend — Data Intelligence Platform

Next.js 16 (App Router) + TypeScript + Tailwind CSS. Deployed independently of the API
(SOW 4.2); it talks to the backend over HTTP only.

## Setup

```powershell
npm install
Copy-Item .env.example .env.local
npm run dev
```

Runs on <http://localhost:3000>. The API must be running separately — see
[../backend/README.md](../backend/README.md).

## Scripts

| Command | Purpose |
| --- | --- |
| `npm run dev` | Development server with hot reload |
| `npm run build` | Production build |
| `npm run start` | Serve the production build |
| `npm run lint` | ESLint |

## Configuration

`NEXT_PUBLIC_API_BASE_URL` points at the backend. It is inlined into the browser bundle,
so it must never hold a secret. The API's CORS allow-list (`Cors:AllowedOrigins` in the
API's appsettings) must include this app's origin.

## Layout

| Path | Contents |
| --- | --- |
| `src/app/` | Routes, layouts, and pages |
| `src/components/` | Reusable UI — KPI tiles, charts, filters |
| `src/lib/` | API client and shared helpers |
| `src/types/` | Shared TypeScript types mirroring the API contract |

Dashboards (FR-10 to FR-12) and the AI assistant UI (FR-13 to FR-16) are Phase 4 work.
A charting library is not installed yet — the choice (Chart.js or Recharts) is confirmed
during Phase 3.
