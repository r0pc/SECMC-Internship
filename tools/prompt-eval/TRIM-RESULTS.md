# How far the prompt can be trimmed

An experiment, on a branch, to find where cutting the assistant's prompt starts costing answers.
Not a proposal to ship — the branch tip is the deliverable and the merge decision is open.

Everything below is measured. Nothing is estimated.

## The short version

On `deepseek-v4-flash`, the shipped cloud model, **1,712 of ~3,300 prompt tokens can be removed
with no measurable change in quality** — 27/28 cases before and after, on a suite twice the size
of the one that existed when this started. That is 46% of the prompt.

Four blocks are load-bearing and cannot go. The other eleven are not doing work this suite can see.

**But read *What this does and does not buy* before acting on that number.** The removed tokens
were nearly all being served from the provider's prefix cache at roughly a tenth of the input rate,
so the bill moves far less than the headline.

## The method, and the one thing that made it interpretable

Fifteen probes, each the shipping prompt minus exactly one named block, generated from the C#
sources rather than hand-copied so a probe cannot silently drift from what ships
(`variants.py`). Each probe ran the full 28 cases, not just the cases its block guards — the reason
to run an ablation is collateral damage, and a targeted subset is the one thing that cannot find it.

Then the control that changed the reading of everything else.

Three runs of a **byte-identical** prompt moved 1–2 statements each. That measures only how
deterministic the model is. It does not measure what a *changed* prompt does — and several cases
here have more than one right answer, so any perturbation lets the model re-pick freely.

So: **P0, a placebo.** The prompt's line breaks rewrapped. Same words, same order, no meaning
change whatever.

| run | statements changed of 28 | pass count |
| --- | --- | --- |
| identical prompt × 3 | 1, 2, 1 | 27, 27, 27 |
| **placebo (rewrapped) × 3** | **5, 5, 5** — the same five every time | 27, 27, 27 |

A meaning-preserving edit moves five statements. Reading the probe diffs as evidence would have
condemned nine cuts that cost nothing. **Statement churn below ~5 cases is not signal. The pass
count is** — it did not move once across six null runs.

`diff.py` is still what says *what* broke once a case does break. It is just not what says whether
one did.

## What each block is worth

Fifteen probes, full suite, cloud model. Baseline 27/28.

| | block | tok | pass | verdict |
| --- | --- | ---: | --- | --- |
| P3 | worked examples 1 + 2 | 209 | 26 | **load-bearing** — breaks `m13-not-month` |
| P10 | column-values block | 136 | 26 | **load-bearing** — breaks `m13-not-month` |
| P13 | refusal taxonomy elaboration | 67 | 26 | **load-bearing** — breaks `m13-not-month` |
| P2 | worked examples 3 + 4 | 501 | 27 | **load-bearing in composition only** (see V2a) |
| P1 | follow-ups block | 307 | 27 | free |
| P16 | Rules list | 180 | 27 | free alone, needs P2 kept |
| P11 | term mapping | 215 | 27 | free |
| P9 | dates block | 171 | 27 | free |
| P6 | series-changed prose | 173 | 27 | free (worked example 4 carries it) |
| P14 | parameterisation paragraph | 139 | 27 | free |
| P7 | answerable-by-definition | 136 | 27 | free |
| P5 | two-datasets prose | 132 | 27 | free (worked example 3 carries it) |
| P8 | T-SQL dialect block | 114 | 27 | free |
| P4 | examples' `explanation` strings | 92 | 27 | free |
| P12 | duplicated view notes | 52 | 27 | free |

`m13-not-month` is the sentinel that three separate cuts trip. It is stable across four identical
runs and twelve other perturbations, so this is signal, not a bistable case. P10's mechanism is
plain — that block is where `'M13' = the annual average` is written down. **P13's is not**:
compressing the *refusal taxonomy* has no route to an annual-CPI query, and it is recorded as an
unexplained effect rather than dressed up in a story.

## Individually free does not compose

This is where the interactions showed up, and exactly where they were predicted to.

| variant | cut | pass | what broke |
| --- | ---: | --- | --- |
| V1 conservative | 420 | 27/28 | — |
| V1b | 727 | 27/28 | — |
| V2b | 906 | 27/28 | — |
| V2c | 1,193 | 27/28 | — |
| V2d | 1,213 | 27/28 | — |
| V2e | 1,498 | 27/28 | — |
| **V2f** | **1,712** | **27/28** | — ← frontier |
| V2a | 1,161 | 26/28 | `largest-change` |
| V2 moderate | 1,341 | 25/28 | `largest-change` (CTE), `peak` (inline date literal) |
| V3 aggressive | 2,146 | 25/28 | `cross-dataset` lost `AVG`, `largest-change` lost `LAG` |

- **V2 emitted a CTE.** The Rules list is the only place that forbids `WITH`, and worked example 4
  is the only place that demonstrates the derived-table form instead. Each covers for the other;
  cutting both leaves nothing saying it. The validator rejects CTEs before they run, so this is a
  user-facing failure, not a stylistic one.
- **V3 lost `AVG` and `LAG`** because it cut both the prose *and* the worked example for
  cross-dataset joins and for LAG-over-aggregate. Two complementary pairs, both flagged before the
  run, both confirmed.
- **V2a settles which half of the pair matters.** Keeping the Rules list and dropping the examples
  still fails; keeping the examples and dropping the Rules list passes. **The worked examples are
  the load-bearing half.** That is the single most useful finding here for anyone editing this
  prompt later.

Note V3 cuts 434 more tokens than V2f and scores *worse*. The curve is not monotonic, which is why
the frontier had to be searched rather than assumed.

## The summariser prompt

Trimmed separately, 10 cases. This suite is noisy — the **baseline itself** scored 9/10 on one of
three runs, and `empty-inside-coverage` is the flaky one.

| prompt | chars | three runs |
| --- | ---: | --- |
| shipping | 1,797 | 10, 10, 9 |
| S1 −65 tok | 1,563 | 9, 10, 10 |
| S3 −158 tok | 1,228 | 10, 10, 10 |

S3 removes 32% of it: the sentences describing the columnar result shape (the JSON shows it), the
sentence saying a long result carries a note (the note says so itself), and the period-mismatch and
empty-result rules stated once instead of three times.

**Weaker evidence than the SQL suite** — ten coarse prose assertions with a known-flaky case. Free
on what is measured, and what is measured is thinner.

## What this does and does not buy

Three currencies, and the headline number only moves one of them.

**1. The displayed token count — moves by the full cut.** A question goes from ~3,270 to ~1,760
prompt tokens. This is the number in the UI.

**2. The bill — moves by roughly a tenth of that.** 95%+ of the prompt is served from DeepSeek's
prefix cache at a fraction of the input rate. Cutting 1,500 cached tokens saves ~150
token-equivalents per question against a total of ~500. Real, and an order of magnitude smaller
than the headline suggests.

**3. Local latency and context — the largest real effect.** On the CPU-served local model, reading
the prompt is 1,941s of a 2,641s run: **73% of the wall clock, at 14 tokens/sec.** Prefill is what
a shorter prompt actually buys. See the local results below for what it measured.

And one cost: shipping any prompt change invalidates the cache once. One request at full price.

## Findings that have nothing to do with trimming

Three things the expanded suite found in the *shipping* prompt, all of which stand whatever happens
to this branch.

- **`follow-up-unresolvable` fails on the cloud model.** Asked *"and what about the year before
  that"* with no conversation history at all, it invents a referent and queries 2024 rather than
  refusing — directly against the prompt's own *"Do not guess a period."* It fails at baseline and
  in every one of the ~25 runs here. This is the one real defect the whole exercise surfaced.
- **`peak` fails on the shipped local model.** `qwen3.5:4b` writes the right query without
  `TOP (1)`, returning every month rather than the highest.
- **The local model is much weaker than its cloud counterpart**: 21/28 against 27/28 at baseline,
  failing `volume-wording`, `peak`, `half-open-range`, `semiannual-code`, `revision-history`,
  `rolling-week` and `year-to-date`.

Also, a harness bug worth recording: `shape()` anchored on a line of content while `_raw_string`
drops the line its marker lands on, so **every eval run before this branch silently omitted
"Respond with JSON only, no prose, in this exact shape:"** from the prompt it measured. It passed
regardless because both providers are separately asked for JSON via `response_format`. The
committed cloud dump on `main` was produced against that wrong prompt.

## Reproducing

```
python variants.py --list                       # every block, its size, what it costs
python variants.py --write all                  # regenerate the probes from the C# sources
ASSISTANT_BASE_URL=... ASSISTANT_API_KEY=... ./run_probes.sh
python diff.py produced-baseline-cloud.json produced-V2f-cloud.json
```

Cloud runs are metered and need `--i-know-this-costs`. The whole experiment — ~35 full 28-case
runs — cost about 3 million tokens, the large majority of it cache-served.
