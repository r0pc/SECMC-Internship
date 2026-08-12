"""Put the assistant's real prompt to a real model and check the SQL that comes back.

Why this exists
---------------
The prompt is ~3,300 tokens of views, rules and worked examples, re-sent on every question, and
every line of it was added because a model got something wrong without it. That made trimming it
unfalsifiable: nobody could say which lines were still earning their place, so nobody could cut any
of them. This turns that into a measurement — remove a rule, run this, and the cases that fail tell
you exactly what the rule was buying.

It is not a unit test. It needs a model, it costs time (and money, against a hosted gateway), and a
2B model will fail cases a 4B model passes. That is information, not a broken test.

Usage
-----
    python eval.py                             # local Ollama, default model
    python eval.py --model qwen3.5:2b          # a different local model
    python eval.py --semantics trimmed.txt     # A/B a cut-down rules block
    python eval.py --shape s.txt --views v.txt # the other two halves of the prompt
    python eval.py --cloud                     # a hosted OpenAI-shaped gateway (see --help)
    python eval.py --case explicit-month       # one case, printing the full SQL
    python eval.py --case a,b,c --label p2     # an ablation probe, dumped under its own name

Trimming workflow
-----------------
1. `python eval.py --label baseline > baseline.txt`. Fix any case that already fails, or delete it —
   a suite that is red before you start cannot tell you anything about your change.
2. Copy the block you want to cut out of the C# source into a file and cut one rule.
3. `python eval.py --semantics that-file.txt --label p7`. Same passes? Then
   `python diff.py produced-baseline.json produced-p7.json` — because a cut that leaves the pass
   count alone can still have made every statement worse, and pass/fail cannot see that.
4. Re-run against BOTH models before believing it. A rule a strong model no longer needs may still
   be the only thing keeping a small one honest.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import textwrap
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
AI = REPO / "backend" / "src" / "DataIntelligence.Infrastructure" / "Ai"
HERE = Path(__file__).resolve().parent

# The `guards` lines and the model's own prose contain em-dashes and arrows, and a Windows console —
# or a file this output is redirected into — is cp1252 by default. That is why the committed
# baseline shows replacement characters where its dashes should be. Reports kept as evidence should
# be readable.
sys.stdout.reconfigure(encoding="utf-8")


# --------------------------------------------------------------- building the prompt

def _raw_string(source: str, start: str, end: str) -> str:
    """The body of a C# raw string literal, dedented as the compiler would dedent it."""
    i = source.index(start)
    body = source[i:source.index(end, i)]
    return textwrap.dedent("\n".join(body.splitlines()[1:]))


def semantics(override: Path | None = None) -> str:
    if override:
        return override.read_text(encoding="utf-8")
    source = (AI / "SchemaContextProvider.cs").read_text(encoding="utf-8")
    return _raw_string(source, 'private const string Semantics = """', '""";')


def shape(override: Path | None = None) -> str:
    if override:
        return override.read_text(encoding="utf-8")
    source = (AI / "ChatCompletionsNlToSqlClient.cs").read_text(encoding="utf-8")
    return _raw_string(source, "Respond with JSON only", '""" + schemaContext')


def views(override: Path | None = None) -> str:
    return (override or HERE / "schema-fixture.txt").read_text(encoding="utf-8")


def system_prompt(today: str, semantics_text: str,
                  shape_text: str | None = None, views_text: str | None = None) -> str:
    """
    The prompt as ChatCompletionsNlToSqlClient assembles it: contract, then schema, date last.

    All three of its parts are overridable, because all three are worth trimming and only one of
    them lives in a file this harness owns. `shape_text` and `views_text` default to the shipping
    versions so a caller that only wants to A/B the rules block passes one argument as before.
    """
    temporal = (HERE / "temporal-fixture.txt").read_text(encoding="utf-8").replace("{TODAY}", today)
    return ((shape_text if shape_text is not None else shape()) + "\n\n"
            + (views_text if views_text is not None else views()) + "\n"
            + semantics_text + temporal)


def replay(history: list[dict]) -> list[dict]:
    """Prior turns as the client replays them: the question, then the JSON it produced."""
    messages = []
    for turn in history:
        messages.append({"role": "user", "content": turn["question"]})
        messages.append({"role": "assistant", "content": json.dumps(
            {"sql": turn["sql"], "parameters": turn["parameters"]})})
    return messages


# ------------------------------------------------------------------------ providers

def ask_ollama(system: str, messages: list[dict], model: str, host: str) -> dict:
    body = {
        "model": model,
        "messages": [{"role": "system", "content": system}] + messages,
        "stream": False, "format": "json", "think": False,
        "options": {"num_ctx": 16384, "temperature": 0},
    }
    request = urllib.request.Request(
        f"{host.rstrip('/')}/api/chat", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=1800) as response:
        parsed = json.loads(response.read())

    # Ollama reports its durations in nanoseconds, split into loading the weights, reading the
    # prompt, and generating. Reading the prompt is the part trimming changes, and on a CPU it is
    # most of the wait — so it is the one payoff of a shorter prompt that is unambiguous and free
    # to measure. Tokens alone cannot show it, since a cached prefix costs tokens and no time.
    return {"content": parsed.get("message", {}).get("content", ""),
            "prompt_tokens": parsed.get("prompt_eval_count"),
            "completion_tokens": parsed.get("eval_count"),
            "prefill_ms": (parsed.get("prompt_eval_duration") or 0) / 1e6,
            "generate_ms": (parsed.get("eval_duration") or 0) / 1e6}


def ask_openai(system: str, messages: list[dict], model: str, base: str, key: str,
               reasoning: str = "none") -> dict:
    """
    One question to an OpenAI-shaped gateway.

    `reasoning` defaults to "none" because production does: deepseek-v4-flash reasons unless told
    not to, and spends most of its completion budget doing it for no change in the statement. A
    harness that left it on would measure a configuration nobody ships.
    """
    body = {
        "model": model,
        "messages": [{"role": "system", "content": system}] + messages,
        "temperature": 0, "response_format": {"type": "json_object"},
    }
    if reasoning:
        body["reasoning_effort"] = reasoning
    request = urllib.request.Request(
        f"{base.rstrip('/')}/chat/completions", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {key}"})
    with urllib.request.urlopen(request, timeout=300) as response:
        parsed = json.loads(response.read())
    usage = parsed.get("usage") or {}
    return {
        "content": parsed["choices"][0]["message"]["content"],
        "prompt_tokens": usage.get("prompt_tokens"),
        "completion_tokens": usage.get("completion_tokens"),
        # Both spellings, matching ChatUsage.CachedPromptTokens on the C# side.
        "cached_tokens": usage.get("prompt_cache_hit_tokens")
                         or (usage.get("prompt_tokens_details") or {}).get("cached_tokens"),
    }


# ------------------------------------------------------------------------ assertions

VIEW = re.compile(r"analytics\.\w+", re.IGNORECASE)


def check(case: dict, global_rules: dict, reply: dict) -> list[str]:
    """Every way this reply fell short, as a list of one-line complaints. Empty means it passed."""
    failures: list[str] = []
    expect = case.get("expect", {})

    try:
        parsed = json.loads(reply["content"])
    except json.JSONDecodeError:
        return [f"reply was not JSON: {reply['content'][:120]!r}"]

    sql = parsed.get("sql")
    refusal = parsed.get("refusal")
    parameters = parsed.get("parameters") or {}

    if "refusal" in expect:
        if sql:
            failures.append(f"expected a refusal ({expect['refusal']}), got SQL")
        elif refusal != expect["refusal"]:
            failures.append(f"expected refusal {expect['refusal']!r}, got {refusal!r}")
        return failures

    if not sql:
        return [f"expected SQL, got refusal {refusal!r}"]

    upper = sql.upper()

    for fragment in global_rules.get("absent", []) + expect.get("absent", []):
        if fragment.upper() in upper:
            failures.append(f"must not contain {fragment!r}")

    for prefix in global_rules.get("notStartsWith", []):
        if upper.lstrip().startswith(prefix.upper()):
            failures.append(f"must not start with {prefix!r}")

    if global_rules.get("onlyViews"):
        allowed = {v.lower() for v in ALLOWED_VIEWS}
        for named in VIEW.findall(sql):
            if named.lower() not in allowed:
                failures.append(f"named an object that is not an allowed view: {named}")

    for fragment in expect.get("contains", []):
        if fragment.upper() not in upper:
            failures.append(f"expected to contain {fragment!r}")

    wanted_views = expect.get("views", [])
    if wanted_views:
        found = {v.lower() for v in VIEW.findall(sql)}
        hits = [v for v in wanted_views if v.lower() in found]
        if expect.get("viewsAnyOf"):
            if not hits:
                failures.append(f"expected one of {wanted_views}, read {sorted(found)}")
        else:
            missing = [v for v in wanted_views if v.lower() not in found]
            if missing:
                failures.append(f"expected to read {missing}, read {sorted(found)}")

    for value in expect.get("parameterValues", []):
        if not any(str(value) in str(bound) for bound in parameters.values()):
            failures.append(f"expected a parameter containing {value!r}, bound {parameters}")

    if expect.get("hasParameters") and not parameters:
        failures.append("expected bound parameters, got none")

    return failures


ALLOWED_VIEWS = [
    "analytics.vw_Cpi", "analytics.vw_CpiAnnual", "analytics.vw_CpiMonthlyChange",
    "analytics.vw_CpiRevision", "analytics.vw_Sofr", "analytics.vw_SofrAnnual",
    "analytics.vw_SofrRevision", "analytics.vw_LatestIndicator", "analytics.vw_CollectionHealth",
]


# ----------------------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--host", default="http://localhost:11434")
    parser.add_argument("--cloud", action="store_true",
                        help="use an OpenAI-shaped gateway; reads ASSISTANT_BASE_URL and "
                             "ASSISTANT_API_KEY from the environment")
    parser.add_argument("--semantics", type=Path,
                        help="file replacing the Semantics block, for A/B-ing a trim")
    parser.add_argument("--shape", type=Path,
                        help="file replacing the JSON output-contract block")
    parser.add_argument("--views", type=Path,
                        help="file replacing the view list and its notes")
    parser.add_argument("--today", default="2026-08-11",
                        help="the date the prompt claims it is, so results stay reproducible")
    parser.add_argument("--case", help="comma-separated case ids; a single id also prints its SQL")
    parser.add_argument("--label", help="names the statement dump produced-<label>.json, so an "
                                        "ablation probe does not overwrite the baseline's")
    parser.add_argument("--reasoning", default="none",
                        help="cloud reasoning_effort; matches production. '' to omit")
    parser.add_argument("--i-know-this-costs", action="store_true",
                        help="required for --cloud; acknowledges the run is metered")
    args = parser.parse_args()

    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))

    # A comma list rather than one id, because an ablation probe wants exactly the cases a block
    # guards plus a few controls — running all 28 for each of fifteen probes is hours of CPU spent
    # re-confirming the cases that cannot be affected.
    wanted = [c.strip() for c in args.case.split(",") if c.strip()] if args.case else None
    cases = [c for c in spec["cases"] if not wanted or c["id"] in wanted]

    if wanted:
        missing = [w for w in wanted if w not in {c["id"] for c in cases}]
        if missing:
            # Named and refused rather than silently skipped: a probe that quietly ran four cases
            # instead of six would report a clean result for a block it never tested.
            print(f"no case with id(s): {', '.join(missing)}", file=sys.stderr)
            return 2

    system = system_prompt(args.today, semantics(args.semantics),
                           shape(args.shape), views(args.views))

    if args.cloud:
        base, key = os.environ.get("ASSISTANT_BASE_URL"), os.environ.get("ASSISTANT_API_KEY")
        if not base or not key:
            print("--cloud needs ASSISTANT_BASE_URL and ASSISTANT_API_KEY", file=sys.stderr)
            return 2

        # A guard rather than a prohibition. Cloud runs are useful for confirming a change against
        # the model that actually serves production, and cheap enough to do — but each one carries
        # the whole schema prompt sixteen times, and it should never happen unnoticed inside a loop
        # while somebody bisects a rule.
        if not args.i_know_this_costs:
            print(f"A cloud run of {len(cases)} cases costs roughly "
                  f"{len(cases) * 3900:,} prompt tokens. Local runs are free and are the right "
                  f"place to iterate.\nRe-run with --i-know-this-costs to proceed.",
                  file=sys.stderr)
            return 2

        target = f"cloud {args.model} at {base}"
        run = lambda msgs: ask_openai(system, msgs, args.model, base, key, args.reasoning)  # noqa: E731
    else:
        target = f"local {args.model}"
        run = lambda msgs: ask_ollama(system, msgs, args.model, args.host)  # noqa: E731

    overrides = [f"{name} from {path}" for name, path in
                 (("semantics", args.semantics), ("shape", args.shape), ("views", args.views))
                 if path]

    print(f"{len(cases)} case(s) against {target}")
    print(f"system prompt: {len(system)} chars"
          + (f"  [{'; '.join(overrides)}]" if overrides else "  [shipping prompt]") + "\n")

    passed, failed, errored = 0, [], []
    consecutive_errors = 0

    # What a run costs, tallied from what the provider itself reported rather than estimated. This
    # matters when the target is a metered gateway: sixteen cases each carrying the whole schema
    # prompt is not a free thing to run in a loop while bisecting a rule.
    prompt_total = completion_total = cached_total = 0
    prefill_ms_total = generate_ms_total = 0.0

    # Every statement the run produced, so two runs can be diffed. Pass/fail alone hides the change
    # that matters most when trimming a rule: SQL that got worse but still satisfies the assertions.
    produced: dict[str, object] = {}

    for case in cases:
        messages = replay(case.get("history", [])) + [{"role": "user", "content": case["question"]}]

        # One retry, because a model server that drops a connection mid-run is a fact about the
        # server and not about the prompt. Left unretried it reports as a regression.
        reply = None
        for attempt in (1, 2):
            try:
                reply = run(messages)
                break
            except Exception as error:                              # noqa: BLE001
                last = f"{type(error).__name__}: {error}"
                if attempt == 2:
                    errored.append((case, last))

        if reply is None:
            consecutive_errors += 1
            print(f"  ERROR {case['id']}", flush=True)

            # Ollama falling over after case 1 once produced fifteen "regressions" in a row. Stop
            # rather than manufacture them: a run that cannot reach the model has no result to give.
            if consecutive_errors >= 2:
                print("\n  Two consecutive transport errors — is the model server up? Aborting.",
                      flush=True)
                break
            continue

        consecutive_errors = 0
        prompt_total += reply.get("prompt_tokens") or 0
        completion_total += reply.get("completion_tokens") or 0
        cached_total += reply.get("cached_tokens") or 0
        prefill_ms_total += reply.get("prefill_ms") or 0.0
        generate_ms_total += reply.get("generate_ms") or 0.0

        try:
            produced[case["id"]] = json.loads(reply["content"])
        except json.JSONDecodeError:
            produced[case["id"]] = {"unparseable": reply["content"]}

        problems = check(case, spec.get("global", {}), reply)
        if problems:
            failed.append((case, problems))
            print(f"  FAIL  {case['id']}", flush=True)
        else:
            passed += 1
            print(f"  pass  {case['id']}", flush=True)

        if wanted and len(wanted) == 1:
            print("\n" + reply["content"])

    print(f"\n{passed} passed / {len(failed)} failed / {len(errored)} errored"
          f"  (of {len(cases)} cases)")

    if errored:
        # Said plainly, because the whole point of separating these is that a run with errors is not
        # a measurement. Comparing it to a baseline compares the prompt against the network.
        print("  *** This run did not complete. It is NOT comparable to a baseline. ***")

    if prompt_total:
        cached_note = f", {cached_total:,} of them cached" if cached_total else ""
        answered = passed + len(failed)
        print(f"cost: {prompt_total:,} prompt tokens{cached_note} + {completion_total:,} completion "
              f"= {prompt_total + completion_total:,} total"
              + (f"  ({prompt_total // answered:,} prompt per case)" if answered else ""))

    if prefill_ms_total:
        # Reported per case as well as in total, because comparing two runs of different sizes on a
        # total is how a probe that skipped half the suite looks like a speed-up.
        answered = passed + len(failed)
        print(f"time: {prefill_ms_total / 1000:.1f}s reading the prompt + "
              f"{generate_ms_total / 1000:.1f}s generating"
              + (f"  ({prefill_ms_total / answered:,.0f} ms prefill per case)" if answered else ""))

    if produced and not (wanted and len(wanted) == 1):
        stem = args.label or f"{'cloud' if args.cloud else 'local'}-{args.model.replace(':', '-')}"
        dump = HERE / f"produced-{stem}.json"
        dump.write_text(json.dumps(produced, indent=2), encoding="utf-8")
        print(f"statements written to {dump.name} — `python diff.py <baseline> {dump.name}`")

    for case, problems in failed:
        print(f"\n--- FAIL {case['id']}: {case['question']!r}")
        print(f"    guards: {case['guards']}")
        for problem in problems:
            print(f"    - {problem}")

    for case, error in errored:
        print(f"\n--- ERROR {case['id']}: {error}")

    return 0 if not failed and not errored else 1


if __name__ == "__main__":
    sys.exit(main())
