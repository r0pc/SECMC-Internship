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
| `/assistant` | Ask questions in plain language, against either model. Each answer carries the SQL that produced it, its bound parameters, the model's explanation, and which model wrote it. Past conversations are listed alongside and resume in place (FR-13 – FR-16) |
| `/collection` | Collection health and the log of every attempt, filterable by status and source (FR-2) |
| `/sources` | Publishers, polling settings and terms-of-use links (FR-7, SOW 3) |
| `/admin/users` | Accounts, roles, deactivation and password resets. Administrator only (FR-9) |
| `/login` · `/logout` | Sign in, and the Route Handler that clears the session cookie (FR-9) |

Every route is server-rendered on demand. Filters live in the query string, so any view is
a link that can be pasted into a ticket.

`/assistant` is the one page with client state — a conversation is state — so the chat itself is a
Client Component. It still does not call the API from the browser: the two mutations go through
Server Functions in `src/app/assistant/actions.ts`, which keeps the rule that only the server talks
to the backend and means no CORS allowance is needed for the app's first interactive page.

The transcript is deliberately not persisted to `localStorage`. Sessions are server-side — one JSON
document per conversation in `ai.AssistantSession` — and that document is the durable record; a
second copy in the browser would put users' questions somewhere nobody has agreed they should be,
for no benefit a reload does not already provide.

The model picker defaults to **Cloud** and is per question rather than per conversation, so a chat
can mix the two. Each answer carries back which one wrote it and the transcript keeps that on the
turn, because "the platform said X" and "the 4B model running on the API box said X" are not the
same claim. Picking Local where no model server is running returns a 503 naming the command that
fixes it — never a silent fallback to Cloud, which would attribute a hosted answer to the choice
the user did not make.

## Authentication (FR-9)

Sign-in posts to a Server Function, which calls the API and stores the returned bearer token in an
**HttpOnly** cookie. The token never reaches the browser as script-readable state, so an XSS bug in
a chart component cannot walk off with a valid credential, and `src/lib/api.ts` attaches it to
every server-side call.

Three layers, and only one of them is a control:

| Layer | What it does | What it is worth |
| --- | --- | --- |
| `src/proxy.ts` | Redirects a visitor with no session cookie to `/login` | A redirect, not a guard. It reads the cookie and nothing else — it runs on every request including prefetches, so it must not call the API, and it cannot verify a signature because the signing key belongs to the API alone |
| `requireSession` / `requireRole` in each page and Server Function | Decides what to render, and refuses to act | Close to the data, as the Next.js guide asks. Server Functions check for themselves because each one is a public endpoint with a generated name — reaching it does not mean anyone rendered the page that offers it |
| The API | Verifies the signature, re-reads the account, compares its security stamp | **This is the control.** A forged cookie earns a rendered login page and 401s from everything behind it |

`proxy.ts`, not `middleware.ts`: Next 16 renamed the convention.

When the API refuses a token this app still holds — the session expired, or an administrator
disabled the account, or its password or roles changed — `src/lib/api.ts` redirects to `/logout`,
which clears the cookie and sends the visitor to `/login` with a message saying the session ended.
The detour through a Route Handler is not decoration: a Server Component cannot delete a cookie, so
redirecting straight to `/login` would leave the dead cookie in place and bounce the visitor
between the two pages.

Roles decide what the navigation offers — Viewers see no Assistant link, non-administrators no
Users link — which is a courtesy to the person who cannot use them rather than a control. The API
refuses the request either way.

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
