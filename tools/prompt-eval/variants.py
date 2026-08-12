"""Generate the trimmed prompt files an ablation probe feeds to eval.py.

Why generated rather than hand-written
--------------------------------------
A probe is "the shipping prompt minus exactly one block". Fifteen hand-copied files cannot be
trusted to be that: the moment a rule is edited in SchemaContextProvider.cs, every copy is testing
a prompt nobody ships, and nothing says so. These are cut from the C# source on each run, so a
probe is always the current prompt minus one named thing.

Blocks are addressed by a distinctive prefix of their first line, because that is what a person
reading the prompt would use to point at one, and it survives the prose being reworded.

    python variants.py --list                 # the blocks, their sizes, and what guards them
    python variants.py --write P2 P7          # write variants/P2.txt, variants/P7.txt
    python variants.py --write all
"""
from __future__ import annotations

import argparse
import re
import sys
import textwrap
from pathlib import Path

import eval as e

HERE = Path(__file__).resolve().parent
OUT = HERE / "variants"

sys.stdout.reconfigure(encoding="utf-8")


def blocks(text: str) -> list[str]:
    """The prompt split on blank lines, which is how it is written and how it reads."""
    return re.split(r"\n\s*\n", text)


def drop(text: str, *starts: str) -> str:
    """`text` without the blocks whose first line begins with any of `starts`."""
    kept, dropped = [], []
    for block in blocks(text):
        first = block.strip().splitlines()[0] if block.strip() else ""
        if any(first.startswith(s) for s in starts):
            dropped.append(first[:50])
            continue
        kept.append(block)

    missing = [s for s in starts
               if not any(b.strip().startswith(s) for b in blocks(text) if b.strip())]
    if missing:
        # Loud, because the failure is silent otherwise: a probe whose block was never found tests
        # the unmodified prompt and reports "no change", which reads as "this block is free to cut".
        raise SystemExit(f"no block starts with {missing!r} — the prompt has been reworded")

    return "\n\n".join(kept)


def shorten_explanations(text: str) -> str:
    """Cut the worked examples' `explanation` values to a clause, keeping the field present."""
    def replace(match: re.Match) -> str:
        return '"explanation": "Reads the figures the question asked for.",'

    return re.sub(r'"explanation": ".*?",', replace, text)


# The refusal taxonomy with its examples reduced to the distinction each arm actually turns on.
# Not dropped: without a taxonomy at all, four cases fail for a reason nobody needs measuring.
# The question is whether the elaboration is doing work the one-liners do not.
COMPRESSED_TAXONOMY = '''When "sql" is null, set "refusal" to one of:
  "not_a_data_question" — a greeting, thanks, or anything not asking about data.
  "about_cpi", "about_sofr" — asks what CPI or SOFR *is*, not what it *was*.
  "about_platform" — what this assistant or platform is, holds, or can answer.
  "unanswerable"   — a genuine data question these views cannot answer.
Leave "refusal" null whenever you return SQL. The "about_" values are classifications, not
requests to write the answer — the reply is fixed text on the other side, so put no definition
in "explanation" and state no figure.'''


def compress_taxonomy(text: str) -> str:
    return "\n\n".join(COMPRESSED_TAXONOMY if b.strip().startswith('When "sql" is null') else b
                       for b in blocks(text))


def rewrap(text: str) -> str:
    """
    The same words, the same order, different line breaks. A placebo.

    The three control runs re-sent a byte-identical prompt and moved one or two statements, which
    measures only how deterministic the model is. It does not measure what a *changed* prompt does,
    and several cases here have more than one right answer - "the overnight borrowing rate right
    now" is vw_Sofr or vw_LatestIndicator, and nothing prefers one. If any perturbation lets the
    model re-pick among those, then "this cut changed five statements" is not evidence about the
    cut, and every probe below needs reading against this number rather than against zero.
    """
    out = []
    for block in blocks(text):
        if block.lstrip().startswith(("{", '"', "-", " ")) or "{" in block:
            out.append(block)          # examples and bullet lists: leave the shape alone
            continue
        out.append(textwrap.fill(" ".join(block.split()), width=88))
    return "\n\n".join(out)


def drop_duplicated_notes(text: str) -> str:
    """
    The view notes whose content the term-mapping block already states.

    Kept deliberately: vw_Sofr's, because "in billions of dollars, not dollars" is a unit that
    appears nowhere else, and losing it changes a figure a user reads rather than a view choice.
    """
    out = []
    for line in text.splitlines():
        note = line.strip()
        if note.startswith("-- Daily collector health per source"):
            continue
        if note.startswith("-- The single latest CPI row"):
            continue
        line = line.replace(
            " (YearOverYearPct is the headline inflation rate)", "")
        out.append(line)
    return "\n".join(out)


# Each probe is (surface, description, transform). `surface` is the eval.py flag it feeds.
PROBES = {
    "P0": ("semantics", "PLACEBO: same words, rewrapped. The floor any probe must beat.",
           rewrap),
    "P2": ("semantics", "worked examples 3 and 4 (cross-dataset, LAG)",
           lambda s: drop(s, '"What is the relation between CPI and SOFR',
                          '"Between which months is the rate of change')),
    "P3": ("semantics", "worked examples 1 and 2 (explicit month, 3-month window)",
           lambda s: drop(s, '"What was CPI in June 2025?"',
                          '"What was the year over year inflation rate')),
    "P4": ("semantics", "the four examples' explanation strings, cut to a clause",
           shorten_explanations),
    "P5": ("semantics", "two-datasets prose, keeping worked example 3",
           lambda s: drop(s, "Two datasets at once")),
    "P6": ("semantics", "series-changed prose, keeping worked example 4",
           lambda s: drop(s, "How a series CHANGED")),
    "P7": ("semantics", "the answerable-by-definition block",
           lambda s: drop(s, "A question about prices, inflation, rates")),
    "P8": ("semantics", "the T-SQL dialect block",
           lambda s: drop(s, "The dialect is Microsoft SQL Server")),
    "P9": ("semantics", "the dates block",
           lambda s: drop(s, "Dates. Prefer ReferenceDate")),
    "P10": ("semantics", "the column-values block",
            lambda s: drop(s, "Column values")),
    "P11": ("semantics", "the term-mapping block",
            lambda s: drop(s, "What the words in a question map to")),
    "P1": ("semantics", "the follow-ups block (the conditional-block candidate)",
           lambda s: drop(s, "Follow-ups. Earlier turns")),
    "P16": ("semantics", "the Rules list",
            lambda s: drop(s, "Rules:")),
    "P13": ("shape", "the refusal taxonomy, compressed to the distinction each arm turns on",
            compress_taxonomy),
    "P14": ("shape", "the parameterisation paragraph",
            lambda s: drop(s, "Every literal the question supplies")),
    "P12": ("views", "the view notes the term-mapping block already states",
            drop_duplicated_notes),
}

# The shipping text of each surface, so a probe is always "what ships, minus one thing".
SOURCE = {"semantics": e.semantics, "shape": e.shape, "views": e.views}


def build(name: str) -> tuple[str, str]:
    """The probe's (surface, trimmed text)."""
    surface, _, transform = PROBES[name]
    return surface, transform(SOURCE[surface]())


# Composed variants: every cut that cost nothing on its own, applied together.
#
# Individually clean does not compose. Two of these pairs are complementary by construction - P2
# drops the worked examples for cross-dataset and LAG while P5/P6 drop the prose that says the same
# thing, and P11 drops the term mapping while P12 drops the view notes that restate it. Each half
# passed alone precisely because the other half was still there. So V1 and V2 keep one side of each
# pair, and V3 deliberately takes both, because "where does it break" is the question that was
# asked and an ablation cannot answer it.
VARIANTS = {
    "V1": ("conservative", ["P4", "P7", "P12", "P14"]),
    "V2": ("moderate", ["P4", "P7", "P12", "P14", "P1", "P2", "P16"]),
    "V3": ("aggressive", ["P4", "P7", "P12", "P14", "P1", "P2", "P16",
                          "P5", "P6", "P8", "P9", "P11"]),

    # V2 lost largest-change to a CTE, and the Rules list is the only place that forbids one -
    # worked example 4 is what carried the shape when P16 was cut alone. These two split that pair
    # to find which half has to stay, and V1b isolates the follow-ups block, the single largest
    # block that cost nothing on its own.
    "V1b": ("V1 + follow-ups block", ["P4", "P7", "P12", "P14", "P1"]),
    "V2a": ("V2 keeping the Rules list", ["P4", "P7", "P12", "P14", "P1", "P2"]),
    "V2b": ("V2 keeping worked examples 3+4", ["P4", "P7", "P12", "P14", "P1", "P16"]),

    # Pushing on from V2b, which held. P5/P6 drop the prose whose worked examples V2b keeps, so
    # unlike in V3 each rule still has one statement of it left. P8/P9 are the dialect and dates
    # blocks, both clean alone. P11 is left out: V2b already cuts the view notes, and the term
    # mapping is the only other place that says what "inflation" or "borrowed" maps to.
    "V2c": ("V2b + dialect + dates", ["P4", "P7", "P12", "P14", "P1", "P16", "P8", "P9"]),
    "V2d": ("V2b + the two prose blocks", ["P4", "P7", "P12", "P14", "P1", "P16", "P5", "P6"]),
    "V2e": ("V2b + all four", ["P4", "P7", "P12", "P14", "P1", "P16", "P5", "P6", "P8", "P9"]),

    # Everything that survived, plus the term mapping - the other half of the pair V2e already
    # broke by cutting the view notes. This is the one expected to fail, and it is run for that
    # reason: a frontier nobody pushed past is a guess about where the frontier is.
    "V2f": ("V2e + the term mapping", ["P4", "P7", "P12", "P14", "P1", "P16",
                                       "P5", "P6", "P8", "P9", "P11"]),
}


def build_variant(name: str) -> dict[str, str]:
    """The variant's {surface: trimmed text}, applying each probe's transform in turn."""
    _, probes = VARIANTS[name]
    out = {surface: source() for surface, source in SOURCE.items()}

    for probe in probes:
        surface, _, transform = PROBES[probe]
        out[surface] = transform(out[surface])

    # Only the surfaces a variant actually changed, so eval.py is passed the shipping version of
    # the rest rather than a copy of it that could drift.
    return {s: t for s, t in out.items() if t != SOURCE[s]()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--write", nargs="*", metavar="PROBE")
    args = parser.parse_args()

    if args.list or not args.write:
        for surface, source in SOURCE.items():
            print(f"shipping {surface}: {len(source()):,} chars")
        print()
        for name in sorted(PROBES, key=lambda n: int(n[1:])):
            surface, description, _ = PROBES[name]
            _, trimmed = build(name)
            saved = len(SOURCE[surface]()) - len(trimmed)
            print(f"  {name:4} --{surface:10} -{saved:5,} chars  ~{saved / 3.6:4.0f} tok   {description}")
        return 0

    OUT.mkdir(exist_ok=True)
    names = list(PROBES) if args.write == ["all"] else args.write

    for name in names:
        if name in VARIANTS:
            description, probes = VARIANTS[name]
            saved = 0
            flags = []
            for surface, text in build_variant(name).items():
                path = OUT / f"{name}-{surface}.txt"
                path.write_text(text, encoding="utf-8")
                saved += len(SOURCE[surface]()) - len(text)
                flags.append(f"--{surface} variants/{path.name}")
            print(f"{name} ({description}, {'+'.join(probes)}): {saved:,} chars, ~{saved / 3.6:.0f} tok")
            print(f"  {' '.join(flags)}")
            continue

        if name not in PROBES:
            raise SystemExit(f"unknown probe {name!r}; try --list")
        surface, text = build(name)
        path = OUT / f"{name}.txt"
        path.write_text(text, encoding="utf-8")
        saved = len(SOURCE[surface]()) - len(text)
        print(f"{path.relative_to(HERE)}  --{surface} {path.name}  ({saved:,} chars removed)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
