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

1. `python eval.py > before.txt` — baseline. Fix or delete anything already failing; a suite that is
   red before you start cannot tell you anything about your change.
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

`deepseek-v4-flash`, 16/16, with `reasoning_effort: none` as production sends:

```
16 passed / 0 failed / 0 errored
cost: 52,088 prompt tokens, 51,200 of them cached + 1,579 completion
```

Two things that baseline settles. **Caching works** — 98% of the prompt served from cache, which is
what the prompt ordering was for. And **reasoning is waste**: the same 16 cases with reasoning left
on cost 8,305 completion tokens instead of 1,579, for exactly the same 16 passes.

There is a second suite, `eval_summariser.py`, for the answer-writing call — 6/6 on the same model.
That one covers the rules that stand between an empty result and a false claim that data is missing.

## Cases

`cases.json`. Each carries a `guards` line naming the rule it protects — a case nobody can trace
back to a rule is a case nobody can act on, so keep that filled in. `global` holds the assertions
that apply to every case returning SQL: no `LIMIT`, no leading `WITH`, no fenced output, and no
object outside the allow-list.

Assertions check properties rather than exact SQL, because many statements are correct: which views
were read, which fragments appear (`LAG`, `TOP`, `AVG`), which values got bound, and whether a
refusal was the right answer.
