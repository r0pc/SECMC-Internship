# Documentation

Home for the Phase 3 and Phase 6 artifacts called for in the Scope of Work.

| Document | SOW reference | Status |
| --- | --- | --- |
| [Database schema (DDL)](database-schema.sql) | Phase 3 deliverable | Drafted — created on SQL Server 2025, loaded with the sample extracts below, and verified against the EF migration |
| ERD — [SVG](database-erd.svg) · [PDF](database-erd.pdf) | Phase 3 deliverable | Drafted — 12 tables, 11 relationships |
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

Two publishers signed off under SOW 0.1, and **one dataset each**. Both are consumed through
official JSON APIs rather than scraped (SOW 9: "prefer an official API if one exists"). Sample
extracts of exactly what arrives are checked in under [example_data/](example_data/), and the
schema is written against them.

Each dataset gets its own table. With the scope fixed at one CPI series and one rate, a generic
series registry with a shared fact table would have added a join to every query and bought
nothing back.

### US Consumer Price Index — U.S. Bureau of Labor Statistics

`POST https://api.bls.gov/publicAPI/v2/timeseries/data/` · published monthly ·
→ `core.CpiObservation` · sample: [CPI.csv](example_data/CPI.csv)

| Series code | Description | Unit |
| --- | --- | --- |
| `CUUR0000SA0` | CPI-U, all items, U.S. city average, **not** seasonally adjusted | Index 1982-84=100 |

That is the whole of it — one series, pinned by a CHECK constraint rather than left as a
convention. Not seasonally adjusted is the right basis for the year-over-year comparison that
is the headline inflation number.

BLS publishes more than twelve figures a year for it, and the CSV shows all of them: twelve
monthly index levels, an **Annual** average, and **HALF1** / **HALF2** semiannual averages. All
are stored, one row per period, tagged with the publisher's own period token:

| `PeriodCode` | `PeriodType` | CSV column |
| --- | --- | --- |
| `M01` … `M12` | `Month` | Jan … Dec |
| `M13` | `Annual` | Annual |
| `S01` · `S02` | `Semiannual` | HALF1 · HALF2 |

The distinction matters: the annual and semiannual figures are averages *of* the monthly ones,
so anything that summed a year's rows without filtering would count the same numbers three
times. Every read model filters on `PeriodType` explicitly.

### Secured Overnight Financing Rate — Federal Reserve Bank of New York

`GET https://markets.newyorkfed.org/api/rates/secured/sofr/search.json?startDate=&endDate=` ·
published each business day, ~08:00 ET, for the prior business day ·
→ `core.SofrDailyRate` · sample: [SOFR.csv](example_data/SOFR.csv)

One row per business day of the current calendar year, rate type **SOFR** only. A day's record
is one row with its measures as columns:

| Column | Description | Unit |
| --- | --- | --- |
| `RatePercent` | The overnight rate | Percent per annum |
| `Percentile1/25/75/99Percent` | Rate distribution across the underlying trades | Percent per annum |
| `VolumeUsdBillions` | Transaction volume | **USD billions** |
| `Average30/90/180DayPercent`, `SofrIndexValue` | The NY Fed's own compounded averages and index | Percent per annum / index |

The download carries four other rates — **EFFR, OBFR, TGCR, BGCR** — and they are out of scope.
The adapter filters to `type == "SOFR"` and the rest are logged as `UnknownSeries` rejections,
so the exclusion is visible in the data rather than invisible in the code. Expect four
rejections per business day; anything else in there is worth looking at.

**Units are not interchangeable** and nothing is rescaled on the way in — volume stays in
billions because that is what the publisher publishes. A chart that silently mixes an index
with billions of dollars is wrong by a factor no one can see in the data.

### How much, and how far back

| | Requested per cycle | Why |
| --- | --- | --- |
| BLS | Current and previous calendar year | Covers the year-over-year comparison and any restatement of recent months |
| SOFR | 1 January of the current year to today | The annual extract. Requesting the whole year rather than the last few days means a gap from an outage or a late revision is repaired by the next cycle instead of persisting |

Collection runs hourly (FR-1), so most cycles find nothing new — CPI is monthly and SOFR is
business-daily. That is handled rather than wasted: an unchanged value writes no row, and a
byte-identical response is recognised before it is even parsed.

Revisions are kept, never overwritten (FR-4). Each correction is a new vintage of the same
period; the newest is flagged `IsCurrent` and dashboards read only those. Gaps are absent
rather than zero — CPI.csv currently has no October 2025 value, so no row is written and the
chart shows a gap instead of a fabricated level.

### Adding to it

Another series or another rate gets **its own table**, on the same pattern: a fact table keyed
on its own period, a `collect.DataSource` row, an `ISourceAdapter` for the publisher's payload,
and an `IDatasetWriter` for the table. It does not get added as a value in an existing table —
`CK_Cpi_SeriesCode` and `CK_Sofr_RateType` exist to stop that happening by accident, because a
table that quietly holds two series is one that every query then has to remember to filter.

What a chart may *draw* is a separate, smaller question, answered by `SeriesCatalog` in the
backend: seven entries, one for CPI and six for the measures a SOFR row carries as columns. That
list replaced the `core.Series` table — with the scope fixed, a registry of rows was only a
second place for the answer to live, and a place where a row could be edited into disagreement
with the collector.

The approved Scope of Work itself lives at
[Scope_of_Work_Data_Intelligence_Platform.pdf](../Scope_of_Work_Data_Intelligence_Platform.pdf).
