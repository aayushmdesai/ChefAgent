---
description: Orient at the start of a ChefAgent work session — branch, last commit, and open items.
---

Report the current state before doing anything else:

1. Run `git branch --show-current` and `git log -1 --format="%h %ad %s" --date=short`.
2. Classify the branch:
   - `main` → this is the frozen Phase 1 baseline. State that explicitly: e2e eval 56/60, real RAGAS scoring, work paused as of the commit reported above. Don't propose new feature work here unprompted.
   - `phase-2` → this is the active branch. Read the most recent `docs/weeklyProgress/phase2-week*-progress.md` file's "Carried into Day N+1" / "Remaining" section and report the open items from there — don't just say "work is in progress," name what's actually next.
   - anything else → say so plainly and ask what branch is the actual target before assuming either baseline applies.
3. If on `phase-2`, confirm the `Pipelines:Enabled` value currently in `src/api/appsettings.json` and state it — this materially changes what code path a `/chat` request takes.
4. Check `git status --porcelain` and surface anything uncommitted before treating the working tree as clean.
5. Give a one-paragraph summary: branch, last commit, what's open, whether the working tree is clean. Then stop and wait for direction — this command orients, it doesn't decide what to work on next.
