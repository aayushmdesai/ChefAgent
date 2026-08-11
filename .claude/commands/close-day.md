---
description: Close out a day's ChefAgent work — test, independently verify claims, document, commit.
---

Run this sequence in order; don't skip a step because it seems redundant with something done earlier in the session:

1. **Test.** Run `dotnet test src/ChefAgent.sln --no-build --configuration Release` (build first if needed). If anything fails, stop here — don't draft a progress doc against a red build.

2. **Draft.** Write today's progress doc entry following [[write-progress-doc]] — `Goal Alignment` / `What Was Built` / `Decisions` / `Tests` / `Definition of Done` / `Tech debt` / `Carried into Day N+1`. Follow that skill's core rule while drafting: verify each claim as you write it, not after.

3. **Independently verify.** Hand the draft and the day's diff (`git diff --stat`, `git diff`) to the `done-verifier` agent. It re-checks every "Built"/"Fixed" claim and every Definition-of-Done checkbox in a fresh context window, against the actual filesystem and test output — not against the draft's own assertions. This is the chain documented in root `CLAUDE.md`'s "Workflow chain" section, and it exists specifically because this project has repeatedly shipped docs claiming something was built that a plain `find`/`grep` shows wasn't (see `phase-2`'s own progress docs for four documented instances, and the Phase 1 docs audit for more).

4. **Fix or footnote.** For anything the `done-verifier` flags, either fix the doc to match reality or fix the code to match the claim — don't just soften the language and move on without deciding which.

5. **Commit.** Stage the day's changes plus the finalized doc. Match this repo's day-scoped commit convention (`phase-2`'s commits are cleanly per-day, e.g. `"Phase 2 Day 3: agent registry with capability-based lookup"` — `main`'s are looser but the same spirit applies: one commit message that names what the day actually shipped, not a generic "updates"). Confirm with the user before pushing.
