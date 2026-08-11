---
name: run-regression-sweep
description: Run ChefAgent's e2e pass/fail regression sweep to confirm a change didn't break existing behavior. Use when asked "did this change break anything", "regression check", or before/after a risky refactor.
---

# Run a regression sweep

Different from [[run-eval-pipeline]] — that measures retrieval/answer *quality* (RAGAS scores). This measures pass/fail *regression* against `scripts/eval/test_e2e_sweep.py`, and is modeled directly on how this repo's own `phase-2` branch validated its pipeline-dispatch cutover.

## Preconditions — both non-negotiable, skipping either produces misleading results

1. **Confirm the vector store is actually populated**: `make check-vectors`, expect `points` matching the expected corpus size (10,000 for the original dataset, 52,155 after the Week 15/16 Indian-recipe-dataset expansion — check which one is actually loaded, don't assume). A suspended/emptied Qdrant Cloud cluster and a genuine pipeline regression are **indistinguishable at the response level** — both look like clean failures with friendly error messages, not crashes. This exact ambiguity cost a full day on `phase-2` (Qdrant Cloud free-tier reclaimed the cluster mid-verification).
2. **Confirm one live search actually returns recipes** before sweeping either state — same reasoning.

## Procedure

1. Run the paced sweep against the *before* state: `python3 scripts/eval/test_e2e_sweep.py` (confirm current pacing/flags in the script — a `pace`/rate-limit-aware mode may or may not be the default depending on when this skill is being read; check the script rather than assuming. As of this writing on `phase-2`/`phase-2-agentic`, `send_chat(..., pace: bool = True)` paces by default and every session ID is scoped to a per-run `RUN_ID` — both confirmed in the current script, not assumed).
2. Make the change / flip the flag under test (e.g. `Pipelines__Enabled=true`).
3. Restart the same build, run the sweep again against the *after* state.
4. **Diff against this script's own recorded results file (`eval/datasets/e2e_sweep_results.md`), not any other harness's number — and check that file's own date/count before trusting a number cited here.** As of the last `phase-2` update to that file (2026-07-08), it records **47/50** — already an improvement over the older Week 8/16 baseline of 44/50, from the Redis-connection-string fix (see `docs/tech-debt.md` `M-5`), not from anything pipeline-related. If you're testing the pipeline cutover specifically, 47/50 is the correct pre-change baseline to diff against, not 44/50 — the Redis fix landed first and is orthogonal to `Pipelines:Enabled`. Either way, the unrelated 56/60 frozen production number (`eval/datasets/e2e_results.json`, Week 19, different case count, different denominator, run against production with pre-warm+retry) is never the right comparison — that mistake has already happened once in this project's history.
5. **Expect the same pre-existing failures on both runs.** Check `docs/tech-debt.md` for the current list of known-failing cases before running (`I-4` as of this writing, reopened Phase 2 Week 2 Day 1 — re-verify it's still open rather than trusting this skill's date-stamped claim), so a pre-existing failure isn't mistaken for a new regression. A *difference between the two runs* is the real signal — a case that fails only after the change, or a previously-failing case that now passes.

## Verification

The diff itself, run against a confirmed-populated corpus with confirmed-working search. If results seem worse than expected, re-check the two preconditions above before concluding the change caused a regression — rule out "the corpus/service is broken" before blaming the code.
