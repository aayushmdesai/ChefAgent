---
description: Run ChefAgent's e2e regression sweep before/after a change, with the preconditions that make the result trustworthy.
---

Run [[run-regression-sweep]] end to end:

1. Confirm both preconditions first — vector store populated at the expected count (`make check-vectors`) and one live search actually returns recipes. Do not proceed to sweeping if either fails; report the infra problem instead of a misleading pass/fail number. This exact ambiguity (empty/suspended vector store looking identical to a real regression) has already cost a full day on this project once.
2. Run the paced sweep against the current (before) state.
3. If comparing a change: apply it (or flip the relevant flag, e.g. `Pipelines__Enabled`), restart the same build, run the sweep again.
4. Diff against `test_e2e_sweep.py`'s own recorded baseline (44/50), not the unrelated 56/60 frozen production number — different harness, different denominator, not comparable.
5. Cross-check any failures against `docs/tech-debt.md`'s currently-open `I-4`/`I-5`-pattern items before reporting a new regression — distinguish "this is new" from "this was already broken."
6. Report: pass rate before/after, which specific cases changed status (if any), and whether any change is a new failure, a newly-fixed pre-existing one, or noise consistent with the known-flaky cases (e.g. the rate-limit burst case).
