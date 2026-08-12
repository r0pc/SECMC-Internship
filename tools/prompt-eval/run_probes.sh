#!/usr/bin/env bash
# Run every ablation probe as a full 28-case suite and dump its statements for diffing.
#
# Full suite per probe, not just the cases the cut block guards. A targeted subset answers "did the
# thing I expected to break, break" — which is the question least worth asking, because the reason
# to run an ablation is collateral damage. Cutting the dates block should not change how a wording
# question picks its view; if it does, that is the finding, and a subset would never see it.
#
# Against the cloud gateway on purpose. Local is free but costs ~10 minutes a probe: every probe
# changes the system prompt, which invalidates Ollama's prefix checkpoints, so each one re-reads
# the whole prompt at 14 tokens/sec. A cloud probe is ~40 seconds and, measured against this
# account's rates, well under a cent. Local is where the composed variants get confirmed, since
# that is where a shorter prompt actually buys something.
#
#   ASSISTANT_BASE_URL=... ASSISTANT_API_KEY=... ./run_probes.sh [probe ...]

set -u
cd "$(dirname "$0")"

: "${ASSISTANT_BASE_URL:?set ASSISTANT_BASE_URL}"
: "${ASSISTANT_API_KEY:?set ASSISTANT_API_KEY}"

MODEL="${MODEL:-deepseek-v4-flash}"
PROBES=("$@")
if [ ${#PROBES[@]} -eq 0 ]; then
    PROBES=(P1 P2 P3 P4 P5 P6 P7 P8 P9 P10 P11 P12 P13 P14 P16)
fi

mkdir -p probes
python variants.py --write all > /dev/null

for probe in "${PROBES[@]}"; do
    # Which flag this probe's file feeds is a property of the probe, and variants.py is the one
    # place that knows it. Asking it beats maintaining the same mapping twice.
    surface=$(python -c "import variants; print(variants.PROBES['$probe'][0])")
    printf '%-5s --%-10s ' "$probe" "$surface"

    python eval.py --cloud --model "$MODEL" --i-know-this-costs \
        "--$surface" "variants/$probe.txt" --label "$probe" \
        > "probes/$probe.txt" 2>&1

    # The summary line, so the run is legible while it happens rather than only afterwards.
    grep -oE '^[0-9]+ passed / [0-9]+ failed / [0-9]+ errored' "probes/$probe.txt" \
        || echo "NO RESULT - see probes/$probe.txt"
done

echo
echo "diffs against the baseline (statement changes, not just pass counts):"
for probe in "${PROBES[@]}"; do
    echo "=== $probe"
    python diff.py produced-baseline-cloud.json "produced-$probe.json" | tail -n +1
done
