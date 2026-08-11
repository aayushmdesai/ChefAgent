---
name: write-progress-doc
description: Write or close out a day's progress doc entry for ChefAgent, following the project's established weeklyProgress template. Use when asked to "write today's progress doc", "close out Day N", or "document what we built today".
---

# Write a progress doc entry

Follow the template every existing entry uses (`docs/weeklyProgress/week*-progress.md` on `main`, `docs/weeklyProgress/phase2-week*-progress.md` on `phase-2`): `Goal Alignment` → `What Was Built` → `Decisions` (table: question / decision / why, where relevant) → `Tests` → `Definition of Done` (checkboxes) → `Tech debt` (new items, using the established ID convention — `I-`/`S-`/`E-`/`M-`/`P-`/`D-`/`G-`/`Inf-`/`T-`/`DS-` on `main`, `P2-` on `phase-2`) → `Carried into Day N+1`.

## The one rule that matters more than the template shape

**For every "Built" or "Fixed" claim, verify the file/class/test exists *before* writing the sentence — not from memory of having done it earlier in the session.** This is not a generic caution — it is this project's single most-repeated, most-damaging failure mode, confirmed on both branches during the August 2026 agentification audit:

- `main`'s docs were found to describe a "80+ tests" suite that's actually ~68 cases, ADR links that point to 9-of-13 wrong filenames, and a tech-debt item marked "deferred" a week after it was actually fixed.
- `phase-2`'s own progress docs — otherwise unusually rigorous — record *at least four separate instances* of describing something as built and checking its Definition-of-Done box, only to discover on a later day (via a plain `find`/`grep`) that the file didn't actually exist: a described `PipelineBuilderTests.cs` that wasn't written, a described `PipelineRunner.cs` that wasn't written, a `ThenForEach` fan-out wiring bug that sat unnoticed for a week because the test that would have caught it was one of the missing ones.

Concretely, before checking a box or writing "Built: X":
- If X is a file/class: `find`/`grep` for it, confirm it exists at the path claimed.
- If X is "tests pass": actually run `dotnet test` (or the relevant suite) in this session, don't cite an earlier run from memory.
- If X is "verified live": the request/response pair should be from this session, not assumed from the implementation looking correct.

If a Definition-of-Done item can't be verified this way right now, leave it unchecked and say why — an honest open checkbox is far cheaper than a false one discovered days later, which is exactly what happened repeatedly on `phase-2`.

## Verification

Every claim in the finished doc traces to a `grep`/`find`/`dotnet test`/live-request result obtained in *this* session while writing it. If invoked via `/close-day`, the `done-verifier` agent (see root `CLAUDE.md`'s workflow-chain section) independently re-checks this before the doc is finalized — don't skip self-verification just because that backstop exists.
