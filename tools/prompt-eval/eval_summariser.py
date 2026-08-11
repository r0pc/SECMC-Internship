"""Eval for the second model call: turning a result set into prose.

`eval.py` tests only SQL generation. The summariser prompt — its no-markdown rule, its
period-mismatch rule and its coverage rule — was entirely untested, which meant the one prompt
standing between an empty result and a false claim that data is missing could be edited blind.

Assertions here are string checks on prose, which is coarser than checking a statement. That is
accepted deliberately: the failures worth catching are gross ones ("June 2025 falls outside our
coverage" when it does not), and a coarse check catches those.

    python eval_summariser.py                       # local Ollama
    python eval_summariser.py --cloud               # hosted gateway
    python eval_summariser.py --case empty-inside-coverage --show
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
AI = HERE.parents[1] / "backend" / "src" / "DataIntelligence.Infrastructure" / "Ai"


def system_prompt() -> str:
    """The summariser's `const string system`, reassembled from the C# string literals."""
    source = (AI / "ChatCompletionsNlToSqlClient.cs").read_text(encoding="utf-8")
    start = source.index('"You answer questions about')
    end = source.index("var user =", start)

    parts = []
    for line in source[start:end].splitlines():
        stripped = line.strip().lstrip("+").strip()
        if stripped.startswith('"'):
            parts.append(stripped.rstrip(";").strip().strip('"').replace('\\"', '"'))
    return "".join(parts)


VIEW = __import__("re").compile(r"analytics\.\w+", __import__("re").IGNORECASE)


def user_message(case: dict, views_only: bool = False) -> str:
    """
    What SummariseResultsAsync composes — or the candidate that names the views instead of echoing
    the whole statement.

    The statement is the largest volatile part of this prompt, and unlike the schema prompt none of
    it caches. Its stated jobs are resolving a follow-up's referent and comparing the period asked
    for against the period queried, and the parameters carry both. What only the statement carries
    is the filters, which is how an empty result gets explained by a WHERE clause rather than by
    coverage — so this is worth measuring rather than assuming.
    """
    if views_only:
        views = ", ".join(dict.fromkeys(VIEW.findall(case["sql"]))) or "(none)"
        second = f"Read from: {views}"
    else:
        second = f"SQL used: {case['sql']}"

    return (f"Question: {case['question']}\n\n"
            f"{second}\n\n"
            f"Parameters bound to it (JSON): {json.dumps(case['parameters'])}\n\n"
            f"{case.get('coverage', '')}\n"
            f"Results: {json.dumps(case['results'])}")


def ask(system: str, user: str, args) -> str:
    if args.cloud:
        base = os.environ["ASSISTANT_BASE_URL"].rstrip("/")
        body = {"model": args.model, "temperature": 0,
                "messages": [{"role": "system", "content": system},
                             {"role": "user", "content": user}]}
        if args.reasoning:
            body["reasoning_effort"] = args.reasoning
        request = urllib.request.Request(
            base + "/chat/completions", data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json",
                     "Authorization": f"Bearer {os.environ['ASSISTANT_API_KEY']}"})
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.loads(response.read())["choices"][0]["message"]["content"]

    body = {"model": args.model,
            "messages": [{"role": "system", "content": system},
                         {"role": "user", "content": user}],
            "stream": False, "think": False,
            "options": {"num_ctx": 16384, "temperature": 0}}
    request = urllib.request.Request(
        f"{args.host.rstrip('/')}/api/chat", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=900) as response:
        return json.loads(response.read())["message"]["content"]


def check(case: dict, global_rules: dict, answer: str) -> list[str]:
    problems: list[str] = []
    expect = case.get("expect", {})
    lowered = answer.lower()

    for fragment in global_rules.get("absent", []) + expect.get("absent", []):
        if fragment.lower() in lowered:
            problems.append(f"must not contain {fragment!r}")

    for fragment in expect.get("contains", []):
        if fragment.lower() not in lowered:
            problems.append(f"expected to contain {fragment!r}")

    any_of = expect.get("containsAnyOf", [])
    if any_of and not any(f.lower() in lowered for f in any_of):
        problems.append(f"expected at least one of {any_of}")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", default="qwen3.5:4b")
    parser.add_argument("--host", default="http://localhost:11434")
    parser.add_argument("--cloud", action="store_true")
    parser.add_argument("--reasoning", default="none", help="cloud reasoning_effort; '' to omit")
    parser.add_argument("--case")
    parser.add_argument("--show", action="store_true", help="print each answer in full")
    parser.add_argument("--views-only", action="store_true",
                        help="candidate: name the views instead of echoing the statement")
    args = parser.parse_args()

    spec = json.loads((HERE / "summariser_cases.json").read_text(encoding="utf-8"))
    cases = [c for c in spec["cases"] if not args.case or c["id"] == args.case]
    if not cases:
        print(f"no case with id {args.case!r}", file=sys.stderr)
        return 2

    system = system_prompt()
    print(f"{len(cases)} case(s) against {'cloud ' if args.cloud else 'local '}{args.model}")
    print(f"summariser system prompt: {len(system)} chars\n", flush=True)

    passed, failed = 0, []
    for case in cases:
        try:
            answer = ask(system, user_message(case, args.views_only), args)
        except Exception as error:                                   # noqa: BLE001
            failed.append((case, [f"call failed: {type(error).__name__}: {error}"], ""))
            print(f"  ERROR {case['id']}", flush=True)
            continue

        problems = check(case, spec.get("global", {}), answer)
        if problems:
            failed.append((case, problems, answer))
            print(f"  FAIL  {case['id']}", flush=True)
        else:
            passed += 1
            print(f"  pass  {case['id']}", flush=True)

        if args.show:
            print(f"        {answer.strip()[:400]}\n", flush=True)

    print(f"\n{passed}/{len(cases)} passed")
    for case, problems, answer in failed:
        print(f"\n--- {case['id']}")
        print(f"    guards: {case['guards']}")
        for problem in problems:
            print(f"    - {problem}")
        if answer:
            print(f"    answer: {answer.strip()[:300]}")

    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
