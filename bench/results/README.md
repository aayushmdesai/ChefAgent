# Benchmark runs

Every run is kept, including the ones that failed and the ones whose results
turned out to be unusable. Deleting runs that didn't fit is what discredits
benchmarks. Where a run is not safe to draw conclusions from, this file says so
and says why.

Findings referenced below (F-nn, B-nn) are in [`../FINDINGS.md`](../FINDINGS.md).

---

## Runs

| Run ID | Config | Requests | Status | Use |
|---|---|---|---|---|
| `20260824-213426` | c=8, `-fast` model | 46 | **Aborted** | Evidence for F-06 only |
| `20260824-213739` | c=32/8/1 sequential | 46 | **Partial** | Client-config A/B vs the next run |
| `20260824-214822` | c=32/8/1 sequential | 46 | **Partial** | Client-config A/B; F-10, F-11 |
| `20260824-215404` | c=32/8/1 sequential | 138 | **Superseded** | Latency unusable; parse rate valid |
| `20260825-155808` | 5 interleaved rounds | 690 | **Primary** | All published results |
| `idle-probes.txt` | 54 idle probes | 54 | **Valid** | F-19, F-22 |

---

### `20260824-213426` — aborted, wrong model ID

Assumed `meta-llama/Llama-3.3-70B-Instruct-fast` existed from Nebius's naming
convention without checking `/v1/models` first. All 46 requests returned 404:

```
{"detail":"The model `meta-llama/Llama-3.3-70B-Instruct-fast` does not exist."}
```

Kept as the evidence for **F-06** — no `-fast` variant was reachable on this
account, and model objects expose no flavor field to discover that from.

*Lesson: check the catalog, don't infer it from a naming convention.*

---

### `20260824-213739` and `20260824-214822` — client-configuration A/B

Both incomplete: only the c=32 config finished before the process was stopped.

The pair exists because c=32 showed time-to-first-token values clustering within
1–3 ms of each other, which independent requests do not do. The hypothesis was
that .NET's default connection limit was serialising requests and the
measurement was of the client, not the endpoint.

- `213739` — default `HttpClient`
- `214822` — `SocketsHttpHandler` with `MaxConnectionsPerServer = 64`

The clustering **persisted and widened** with more connections available, which
argues against the client being the bottleneck. Recorded as **F-10** and
**F-11**, both still HYPOTHESIS: a single run during a window of unknown health
cannot establish structure, and the later variance findings (F-09, F-18, F-22)
mean this needs repetition before it can be claimed.

*Do not cite either run alone. They are only meaningful as a pair.*

---

### `20260824-215404` — superseded, latency unusable

First complete sweep: three configs, 46 queries each, run sequentially.

**Why the latency numbers are not usable.** Configs ran one after another across
23 minutes, during a session in which the endpoint's own latency was drifting
badly. c=32 ran first, c=1 ran last, and c=1 came out *slower* than c=32 — which
is not a real result, just the drift landing on whichever config ran last. This
is the run that motivated the interleaved design.

**What is still valid from it:** the JSON parse rate. Correctness is not
time-sensitive, so 84/84 parsed stands (**F-08**, later superseded at n=690 by
F-17). The generation-rate figures also stand in principle, being within-request
measurements, but are superseded by the larger sample in F-16.

Also the source of the tok/s arithmetic bug: the first analysis pass reported
214,843 tok/s because responses arriving in a single SSE frame drive the
denominator toward zero. `analyze.py` now floors both token count and streaming
duration.

---

### `20260825-155808` — primary run

**All published results come from this run.**

- 5 rounds × 3 concurrency levels (1, 8, 32) × 46 queries = **690 requests**
- Levels shuffled within each round; pooled by config across rounds
- Health probe logged at each round boundary (`probes-20260825-155808.jsonl`)
- Seed 20260825, executed order recorded in the manifest
- 2026-08-25 15:58–17:02 UTC
- **690/690 succeeded. 690/690 parsed. Zero errors, zero 429s.**
- 136,815 input tokens (62,576 cached), 34,151 output. $0.0314.

Supports **F-15** (concurrency comparison not possible — within-config noise
24.7× vs between-config signal under 2×), **F-16** (generation 39–43 tok/s),
**F-17** (parse rate 100% at n=690), **F-20** (cache rate differs by config),
**F-21** (cost).

**What this run does not support:** any ranking of concurrency levels. See F-15.

---

### `idle-probes.txt` — variance baseline

54 sequential single requests at ~60 s intervals over 64 minutes, identical
10-token `"Say OK"` prompt, **no benchmark running**. Plain text, one line per
probe: UTC timestamp and elapsed seconds.

Range 0.57 s – 99.77 s (175×). p50 5.71 s, p90 28.91 s, p95 33.61 s.
Distribution is bimodal: 24.1% under 1 s (tightly clustered, 0.57–0.67 s),
9.3% over 30 s.

Rejects **F-19** — the variance is present at idle, so it is not caused by the
benchmark's own load. Supports **F-22**, including the observation that 9.3%
would cross ChefAgent's configured 30 s client timeout.

---

## Reproducing

```bash
export NEBIUS_API_KEY=...
dotnet run --project bench -- bench/queries.json bench/results/ 5
python3 bench/analyze.py bench/results/raw-<runId>.jsonl
```

`analyze.py` reads the raw JSONL directly, so analysis never depends on the run
process surviving to print its own summary, and works on partial files.

## File formats

- `raw-<runId>.jsonl` — one record per request: timings, token counts, cache
  hits, parse result, first 400 chars of the response, status, error
- `probes-<runId>.jsonl` — one record per health probe
- `manifest-<runId>.json` — seed, model, levels, and the exact executed order
- `idle-probes.txt` — plain text, timestamp and elapsed seconds per line