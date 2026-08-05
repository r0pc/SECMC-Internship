# Documentation

Home for the Phase 3 and Phase 6 artifacts called for in the Scope of Work.

| Document | SOW reference | Status |
| --- | --- | --- |
| [Database schema (DDL)](database-schema.sql) | Phase 3 deliverable | Drafted — verified against SQL Server, matches the EF migration |
| ERD — [SVG](database-erd.svg) · [PDF](database-erd.pdf) | Phase 3 deliverable | Drafted — 13 tables, 15 relationships |
| Architecture document | Phase 3 deliverable | Not started |
| Risk log | Phase 3 deliverable | Not started |
| API contract (published from Swagger) | SOW 3 — Maintainability | Not started |
| Test plan / NL test question set | SOW 11.2 | Not started |
| User and technical documentation | Phase 6 deliverable | Not started |

The ERD is generated from [erd/generate_erd.py](erd/generate_erd.py), which emits both the SVG
and the PDF from one model so the two cannot disagree. Re-run it after any schema change:

```powershell
python docs\erd\generate_erd.py
```

## What the platform stores

Two publishers signed off under SOW 0.1, and **ten series** between them. Both are consumed
through official JSON APIs rather than scraped (SOW 9: "prefer an official API if one exists").

### US Consumer Price Index — U.S. Bureau of Labor Statistics

`POST https://api.bls.gov/publicAPI/v2/timeseries/data/` · published monthly

| Series code | Description | Unit |
| --- | --- | --- |
| `CUUR0000SA0` | CPI-U, all items, US city average, **not** seasonally adjusted | Index 1982-84=100 |
| `CUSR0000SA0` | CPI-U, all items, US city average, seasonally adjusted | Index 1982-84=100 |
| `CUUR0000SA0L1E` | Core CPI (all items less food and energy), not seasonally adjusted | Index 1982-84=100 |
| `CUSR0000SA0L1E` | Core CPI, seasonally adjusted | Index 1982-84=100 |

Both adjustments are tracked because they answer different questions: seasonally adjusted is
the right basis for month-over-month change, unadjusted for year-over-year.

### Secured Overnight Financing Rate — Federal Reserve Bank of New York

`GET https://markets.newyorkfed.org/api/rates/secured/sofr/last/{n}.json` · published each
business day, ~08:00 ET, for the prior business day

| Series code | Description | Unit |
| --- | --- | --- |
| `SOFR` | Overnight rate | Percent per annum |
| `SOFR_VOL` | Transaction volume | **USD billions** |
| `SOFR_P1` · `SOFR_P25` · `SOFR_P75` · `SOFR_P99` | Rate distribution across underlying trades | Percent per annum |

One API record carries all six measures, so each becomes its own series. That keeps
`core.Observation` a plain (series, date) → value table instead of one with five columns that
would be null for every CPI row. `SOFR_VOL` is the odd one out — a quantity, not a rate.

**Units are not interchangeable.** `core.Series.Unit` travels with every value and nothing is
rescaled on the way in, because a chart that silently mixes an index with billions of dollars is
wrong by a factor no one can see in the data.

### How much, and how far back

| | Requested per cycle | Why |
| --- | --- | --- |
| BLS | Current and previous calendar year | Covers the year-over-year comparison, plus BLS's annual revision of seasonally adjusted series |
| SOFR | Last 10 business days | A weekend, holiday, or short outage is backfilled by the next cycle rather than leaving a permanent hole |

Collection runs hourly (FR-1), so most cycles find nothing new — CPI is monthly and SOFR is
business-daily. That is handled rather than wasted: an unchanged value writes no row, and a
byte-identical response is recognised before it is even parsed.

Revisions are kept, never overwritten (FR-4). Each correction is a new vintage of the same
period; the newest is flagged `IsCurrent` and dashboards read only those.

### Adding to it

Registering another **BLS** series is data only — insert a row in `core.Series` with
`DataSourceId = 1` and the next cycle collects it. The adapter posts whatever active series
exist and is not CPI-specific, so any BLS series ID works. Other **NY Fed** rates (EFFR, OBFR,
BGCR, TGCR) need adapter work: the SOFR adapter deliberately filters to `type == "SOFR"` so a
payload change cannot file another rate's values against SOFR's series. A new **publisher**
needs one new `ISourceAdapter` and a `collect.DataSource` row; nothing downstream changes.

Note that the collector **rejects** an unregistered series code rather than creating it, so
register first, then collect.

The approved Scope of Work itself lives at
[Scope_of_Work_Data_Intelligence_Platform.pdf](../Scope_of_Work_Data_Intelligence_Platform.pdf).
