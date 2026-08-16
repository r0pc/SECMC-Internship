# Prompt eval

Puts the assistant's real prompt to a real model and checks the SQL that comes back.

## Why

The schema prompt is ~3,300 tokens of views, rules and worked examples, re-sent on every question,
and every line of it was added because a model got something wrong without it. That made trimming
it unfalsifiable — nobody could say which lines still earned their place, so nobody could cut any of
them, and "this rule is load-bearing" stayed an assertion rather than a fact.

This makes it a measurement. Remove a rule, run the eval, and the cases that fail tell you what the
rule was buying. If none fail, on both models, the rule is not paying for itself.

It also catches the failure that prompted it: `qwen3.5:2b` answering *"what is the cpi in june
2025"* with a query for the current month. `explicit-month` is that bug as a case.

## Running

```powershell
python eval.py                          # local Ollama, qwen3.5:4b
python eval.py --model qwen3.5:2b       # a weaker model, to see what the rules are protecting
python eval.py --case explicit-month    # one case, printing the SQL it produced
python eval.py --semantics trimmed.txt  # A/B a cut-down rules block
```

Against a hosted gateway:

```powershell
$env:ASSISTANT_BASE_URL = "https://api.deepseek.com/"
$env:ASSISTANT_API_KEY  = "sk-..."      # this one costs money per run
python eval.py --cloud --model deepseek-v4-flash
```

This is **not** a unit test and is not in the test suite. It needs a model, it takes minutes on a
CPU, and it costs money against a hosted gateway. A weak model failing cases a strong one passes is
information, not a broken suite.

## Trimming a rule

1. `python eval.py > before.txt`, and diff it against the recorded run in `baseline/` for the same
   model. Neither baseline is green, so "did anything fail" is the wrong question — the one that
   matters is whether the *same* cases failed. A new name in that list is your change.
2. Copy the `Semantics` block out of `SchemaContextProvider.cs` into a file and cut one rule.
3. `python eval.py --semantics that-file.txt` and compare.
4. **Re-run against both models before believing it.** A rule a 4B model no longer needs may be the
   only thing keeping a 2B model honest, and the local option is a supported choice.
5. If it holds, make the cut in `SchemaContextProvider.cs` and note the token saving.

`--today` is fixed at a date rather than reading the clock, so a case asserting a resolved window
gives the same answer next month.

## The fixtures, and the one way this can drift

`eval.py` reads the two blocks that actually get edited — the `Semantics` constant and the response
contract — straight out of the C# sources, so those cannot go stale.

The other two are fixtures here:

- `schema-fixture.txt` — the view list `SchemaContextProvider` builds from `INFORMATION_SCHEMA`.
- `temporal-fixture.txt` — today's date and the coverage window, with `{TODAY}` substituted.

**These are copies and can drift.** If a view gains or loses a column, the eval keeps using the old
list and stops testing the prompt that ships. Re-sync `schema-fixture.txt` after any change to the
`analytics` views — the assistant's own prompt is the authority, and one way to see it is to run a
question through the API with `Information` logging on.

## Baseline

Recorded runs live in `baseline/` and are the thing to compare against. Both were taken with
`reasoning_effort: none`, as production sends.

| Suite | Model | Result | Cost |
| --- | --- | --- | --- |
| `baseline/cloud-deepseek.txt` | `deepseek-v4-flash` | **27 passed / 1 failed** of 28 | 91,611 prompt (86,400 cached) + 2,474 completion = 94,085 — 3,271 prompt per case |
| `baseline/qwen4b.txt` | `qwen3.5:4b`, local | **21 passed / 7 failed** of 28 | 94,636 prompt + 2,413 completion = 97,049 — 3,379 prompt per case |
| `baseline/cloud-summariser.txt` | `deepseek-v4-flash` | **10 passed / 0 failed** of 10 | — |

Two things those settle. **Caching works** — 94% of the cloud prompt served from cache, which is
what the prompt ordering was for. And **reasoning is waste**: measured on the 16-case suite this
grew out of, the same cases with reasoning left on cost 8,305 completion tokens against 1,579
with it off, for exactly the same passes.

**Neither baseline is green, and that is the point of checking them in.** A recorded failure is a
known gap you can diff against; a suite nobody has run is not a baseline at all.

- Cloud fails `follow-up-unresolvable` — a referent that is not in the conversation at all should
  be refused rather than guessed at, and the model guesses.
- The local model fails seven, and they cluster: `year-to-date`, `rolling-week` and
  `half-open-range` are all relative-date resolution, `semiannual-code` and `revision-history` are
  schema detail the prompt spells out. That gap between 4B and the hosted model is the trade the
  local option makes, stated in cases rather than in adjectives.

The second suite, `eval_summariser.py` over `summariser_cases.json`, covers the answer-writing
call — the rules that stand between an empty result and a false claim that data is missing.

`diff.py` compares two runs' produced statements: `python diff.py baseline/cloud-deepseek.txt
produced-baseline-cloud.json`.

## Cases

`cases.json` — 28 of them, plus 10 in `summariser_cases.json`. Each carries a `guards` line naming
the rule it protects — a case nobody can trace back to a rule is a case nobody can act on, so keep
that filled in. `global` holds the assertions that apply to every case returning SQL: no `LIMIT`,
no leading `WITH`, no fenced output, and no object outside the allow-list.

Assertions check properties rather than exact SQL, because many statements are correct: which views
were read, which fragments appear (`LAG`, `TOP`, `AVG`), which values got bound, and whether a
refusal was the right answer.
