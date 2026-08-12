"""Compare two statement dumps from `eval.py --label ...`.

Why this exists
---------------
`eval.py` answers "did the same cases pass". That is not the question trimming asks. Sixteen — now
twenty-eight — binary assertions cannot distinguish a statement that is right from one that is
merely right enough to satisfy them, so a cut can hold the pass count steady while quietly turning
a half-open range into a BETWEEN, dropping a NULL guard, or swapping to a view that happens to
carry the same column.

This looks at what the model actually wrote. It is deliberately coarse — it reports *that* a
statement changed and in which of a few load-bearing respects, and leaves reading the SQL to a
person, because deciding whether a different-but-valid statement is worse is a judgement and not an
assertion.

    python diff.py produced-baseline.json produced-p2.json
    python diff.py produced-baseline.json produced-p2.json --show   # print both statements
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

# A Windows console is cp1252, which cannot encode the arrows and dashes this report is written in;
# without this it does not print a mangled character, it raises UnicodeEncodeError and loses the
# whole run. Redirecting to a file picks the same codec, so the reports committed as evidence would
# be the ones that crashed.
sys.stdout.reconfigure(encoding="utf-8")

VIEW = re.compile(r"analytics\.\w+", re.IGNORECASE)

# The fragments whose presence or absence is a rule being obeyed or broken, rather than a stylistic
# choice. Each one is here because a case in cases.json asserts on it or a rule in the prompt
# demands it — so a change in one of these is a change worth a person's attention.
FRAGMENTS = ["LAG", "TOP", "AVG", "JOIN", "BETWEEN", "GROUP BY", "ORDER BY", "DATEFROMPARTS",
             "IS NOT NULL", "ABS", "ROW_NUMBER", "DISTINCT", "WITH", "LIMIT"]

# Matched on word boundaries, not as substrings. `AVG` occurs inside the alias `AvgRatePercent`,
# so a statement that swapped AVG( for SUM( still read as containing AVG and the change went
# unreported — which is precisely the silent regression this file exists to catch.
BOUNDED = {f: re.compile(rf"\b{f.replace(' ', r'\s+')}\b") for f in FRAGMENTS}


def load(path: Path) -> dict:
    if not path.exists():
        candidate = HERE / path.name
        if candidate.exists():
            return json.loads(candidate.read_text(encoding="utf-8"))
    return json.loads(path.read_text(encoding="utf-8"))


def normalise(sql: str | None) -> str:
    """Collapsed whitespace and upper-cased, so indentation is not reported as a change."""
    return " ".join((sql or "").split()).upper()


def describe(before: dict, after: dict) -> list[str]:
    """Every way these two replies differ, as a list of one-line notes. Empty means identical."""
    notes: list[str] = []

    b_sql, a_sql = before.get("sql"), after.get("sql")
    b_refusal, a_refusal = before.get("refusal"), after.get("refusal")

    # The largest possible change, and the one a pass count is most likely to hide when the case
    # under test is not the one that regressed: a question that used to be answered now is not.
    if bool(b_sql) != bool(a_sql):
        went = "SQL → refusal" if b_sql else "refusal → SQL"
        return [f"{went} ({b_refusal!r} → {a_refusal!r})"]

    if not b_sql and not a_sql:
        if b_refusal != a_refusal:
            notes.append(f"refusal {b_refusal!r} → {a_refusal!r}")
        return notes

    if normalise(b_sql) == normalise(a_sql):
        b_params, a_params = before.get("parameters") or {}, after.get("parameters") or {}
        if b_params != a_params:
            notes.append(f"same SQL, parameters {b_params} → {a_params}")
        return notes

    notes.append("statement changed")

    b_views = {v.lower() for v in VIEW.findall(b_sql)}
    a_views = {v.lower() for v in VIEW.findall(a_sql)}
    if b_views != a_views:
        notes.append(f"  views {sorted(b_views)} → {sorted(a_views)}")

    b_upper, a_upper = normalise(b_sql), normalise(a_sql)
    present_before = {f for f, rx in BOUNDED.items() if rx.search(b_upper)}
    present_after = {f for f, rx in BOUNDED.items() if rx.search(a_upper)}

    lost = [f for f in FRAGMENTS if f in present_before - present_after]
    gained = [f for f in FRAGMENTS if f in present_after - present_before]
    if lost:
        notes.append(f"  LOST {', '.join(lost)}")
    if gained:
        notes.append(f"  gained {', '.join(gained)}")

    b_params, a_params = before.get("parameters") or {}, after.get("parameters") or {}
    if b_params != a_params:
        notes.append(f"  parameters {b_params} → {a_params}")

    return notes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--show", action="store_true", help="print both statements where they differ")
    args = parser.parse_args()

    before, after = load(args.before), load(args.after)

    shared = [k for k in before if k in after]
    only_before = [k for k in before if k not in after]
    only_after = [k for k in after if k not in before]

    changed = 0
    for case in shared:
        notes = describe(before[case], after[case])
        if not notes:
            continue
        changed += 1
        print(f"\n{case}")
        for note in notes:
            print(f"  {note}")
        if args.show:
            print(f"    before: {before[case].get('sql')}")
            print(f"    after:  {after[case].get('sql')}")

    print(f"\n{changed} of {len(shared)} shared case(s) changed")

    # Named rather than ignored: comparing a 28-case baseline against a 6-case probe is the normal
    # way to use this, and silently reporting "0 changed" over the six would read as a clean result
    # for the twenty-two that were never run.
    if only_before:
        print(f"not in {args.after.name}: {', '.join(only_before)}")
    if only_after:
        print(f"not in {args.before.name}: {', '.join(only_after)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
