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
so it must never hold a secret.

Every call is made from a Server Component, so the browser issues no cross-origin request
and the API's CORS allow-list (`Cors:AllowedOrigins`) is not on the critical path today. It
still needs this app's origin in it before anything moves client-side.

## Routes

| Route | Contents |
| --- | --- |
| `/` | KPI tiles, trend charts, stored-history counts, per-source collection health (FR-10) |
| `/series` | The catalogue — seven series with unit, frequency and latest value (FR-11) |
| `/series/[seriesKey]` | One series: headline figures, trend, and the observation rows behind them, with range, bucket, period-type, revision and sort filters |
| `/collection` | Collection health and the log of every attempt, filterable by status and source (FR-2) |
| `/sources` | Publishers, polling settings and terms-of-use links (FR-7, SOW 3) |

Every route is server-rendered on demand. Filters live in the query string, so any view is
a link that can be pasted into a ticket.

## Layout

| Path | Contents |
| --- | --- |
| `src/app/` | Routes, layouts, and pages |
| `src/components/` | Reusable UI — KPI tiles, charts, filters |
| `src/lib/` | API client, formatting, query-string helpers |
| `src/types/` | Shared TypeScript types mirroring the API contract |

## Decisions worth knowing

**No charting library.** Trend charts are plain SVG rendered on the server
(`src/components/trend-chart.tsx`). Chart.js and Recharts were the Phase 3 candidates;
neither earned its place. Server-rendered SVG puts the whole chart in the first HTML
response — most of the 3-second dashboard budget (NFR Performance) for free — and keeps the
runtime dependency surface at Next and React. Revisit if interaction beyond hover tooltips
is needed.

**Units are never shared between axes.** Values are stored exactly as published and never
rescaled, so SOFR volume (billions of dollars) and the CPI (an index) have no axis in
common. `groupByUnit` in `src/lib/series.ts` splits trend lines by unit and each group gets
its own chart. Ignoring this produces a chart that looks fine and means nothing.

**Types are hand-written, not generated.** The API's OpenAPI document is only served in
Development, and a frontend build must not depend on a running backend. `src/types/api.ts`
mirrors `DataIntelligence.Core/Dtos` and has to be updated alongside it.

**No caching.** Every fetch is `no-store`. A dashboard showing quietly stale numbers is
indistinguishable from one whose numbers have not changed, which is the failure this
platform exists to make visible.

**Panels fail independently.** `attempt()` returns a result rather than throwing, so one
dead endpoint costs its own panel and not the page.

## Not built yet

- Authentication (FR-9). Every API endpoint is anonymous, which is why `/sources` is
  read-only here even though the API accepts `PATCH` on a source's polling settings.
- The AI query assistant (FR-13 – FR-16).
- A `notFound()` inside a streamed response returns HTTP 200 with the not-found UI — Next's
  documented behaviour when a `loading.js` boundary is in play. Fine for humans, wrong for
  uptime monitoring; revisit if that matters.
