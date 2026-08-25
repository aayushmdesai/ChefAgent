#!/usr/bin/env python3
"""
Analyse a Level 1 benchmark raw file.

    python3 bench/analyze.py bench/results/raw-<runId>.jsonl

Reads the JSONL directly, so analysis never depends on the run process
surviving to print its own summary. Works on partial files.

Pricing as posted 2026-08-24. Kept here, not in the harness, so historical
raw files can be re-costed when rates change.
"""
import json, sys, statistics as st
from collections import defaultdict

RATE_IN  = 0.13 / 1_000_000   # $/token
RATE_OUT = 0.40 / 1_000_000

# Generation-rate guards. Without these the arithmetic explodes: responses that
# arrive in a single SSE frame make (Total - Ttft) approach zero and produce
# absurd values (214,843 tok/s observed). Any rate metric needs a floor on both
# numerator and denominator.
MIN_TOKENS = 20
MIN_STREAM_MS = 200


def pct(xs, p):
    if not xs:
        return 0.0
    xs = sorted(xs)
    i = max(0, min(len(xs) - 1, int(round(p / 100 * len(xs))) - 1))
    return xs[i]


def load(path):
    with open(path) as f:
        return [json.loads(l) for l in f if l.strip()]


def gen_rate(r):
    """tok/s during the streaming phase only, excluding time-to-first-token."""
    if r["Error"] or r["TtftMs"] < 0:
        return None
    stream_ms = r["TotalMs"] - r["TtftMs"]
    if r["CompletionTokens"] < MIN_TOKENS or stream_ms < MIN_STREAM_MS:
        return None
    return r["CompletionTokens"] / (stream_ms / 1000)


def block(rows, label, keyfn):
    groups = defaultdict(list)
    for r in rows:
        groups[keyfn(r)].append(r)

    print(f"\n── {label} ──")
    print(f"{'key':<22}{'n':>5}{'ttft_p50':>10}{'ttft_p95':>10}"
          f"{'tot_p50':>10}{'tot_p95':>10}{'gen_p50':>9}{'cache%':>8}"
          f"{'parse%':>8}{'err':>5}{'429':>5}")

    for k in sorted(groups, key=lambda x: (isinstance(x, str), x)):
        g = groups[k]
        ok = [r for r in g if not r["Error"]]
        ttft = [r["TtftMs"] for r in ok if r["TtftMs"] >= 0]
        tot = [r["TotalMs"] for r in ok]
        gen = [x for x in (gen_rate(r) for r in ok) if x is not None]

        p_sum = sum(r["PromptTokens"] for r in ok)
        c_sum = sum(r["CachedTokens"] for r in ok)
        cache = 100 * c_sum / p_sum if p_sum else 0
        parse = 100 * sum(1 for r in g if r["ParseOk"]) / len(g)

        print(f"{str(k):<22}{len(g):>5}{pct(ttft,50):>10.0f}{pct(ttft,95):>10.0f}"
              f"{pct(tot,50):>10.0f}{pct(tot,95):>10.0f}{pct(gen,50):>9.1f}"
              f"{cache:>8.1f}{parse:>8.1f}"
              f"{sum(1 for r in g if r['Error']):>5}"
              f"{sum(r['RateLimitHits'] for r in g):>5}")


def main(path):
    rows = load(path)
    if not rows:
        print("no records")
        return

    rounds = sorted({r.get("Round", 1) for r in rows})
    print(f"{len(rows)} records, rounds {min(rounds)}–{max(rounds)}")

    block(rows, "By concurrency (pooled across rounds)", lambda r: f"c={r['Concurrency']}")
    block(rows, "By call site (pooled)", lambda r: r["Path"])

    # Per-round x concurrency: the check that matters. If a config's numbers
    # swing wildly between rounds, session drift is still dominating and the
    # pooled figures above are not yet trustworthy.
    print("\n── Stability check: ttft_p50 per round ──")
    cs = sorted({r["Concurrency"] for r in rows})
    print(f"{'round':<8}" + "".join(f"{'c='+str(c):>12}" for c in cs))
    for rd in rounds:
        cells = []
        for c in cs:
            g = [r["TtftMs"] for r in rows
                 if r.get("Round", 1) == rd and r["Concurrency"] == c
                 and not r["Error"] and r["TtftMs"] >= 0]
            cells.append(f"{pct(g,50):>12.0f}" if g else f"{'—':>12}")
        print(f"{rd:<8}" + "".join(cells))

    for c in cs:
        vals = []
        for rd in rounds:
            g = [r["TtftMs"] for r in rows
                 if r.get("Round", 1) == rd and r["Concurrency"] == c
                 and not r["Error"] and r["TtftMs"] >= 0]
            if g:
                vals.append(pct(g, 50))
        if len(vals) > 1:
            spread = max(vals) / min(vals) if min(vals) > 0 else float("inf")
            print(f"  c={c}: round-to-round p50 spread {spread:.1f}x "
                  f"({min(vals):.0f}–{max(vals):.0f} ms)")

    # Failures, with the response body so they can actually be diagnosed.
    bad = [r for r in rows if not r["ParseOk"]]
    if bad:
        print(f"\n── Parse/error failures: {len(bad)} ──")
        for r in bad[:15]:
            why = r["Error"] or "parse failed"
            print(f"  r{r.get('Round','?')} c={r['Concurrency']:<3} {r['QueryId']:<14} {why[:70]}")
            if not r["Error"] and r.get("ContentSample"):
                print(f"      got: {r['ContentSample'][:120]!r}")
    else:
        print("\nNo parse failures, no errors.")

    fences = sum(1 for r in rows if r["NeededFenceStrip"])
    jsons = sum(1 for r in rows if r["ExpectFormat"] == "json")
    print(f"\nMarkdown fences: {fences}/{jsons} JSON responses")

    tin = sum(r["PromptTokens"] for r in rows)
    tout = sum(r["CompletionTokens"] for r in rows)
    tcac = sum(r["CachedTokens"] for r in rows)
    cost = tin * RATE_IN + tout * RATE_OUT
    print(f"Tokens: {tin:,} in ({tcac:,} cached), {tout:,} out")
    print(f"Cost:   ${cost:.4f} at $0.13/1M in, $0.40/1M out "
          f"(cached tokens assumed billed at full rate — unverified)")
    print(f"Per 1k requests: ${cost / len(rows) * 1000:.2f}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bench/results/latest.jsonl")