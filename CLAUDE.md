# ChefAgent

A recipe-recommendation / diet-validation / meal-planning chat assistant: C#/.NET backend (Semantic Kernel-based multi-agent orchestration over a Qdrant vector store), a React chat frontend, and a Python RAGAS-based eval pipeline. Built solo over ~19 weeks. **`main` (Phase 1) is frozen; `phase-2` is where active development actually is** — see "Current phase" below before assuming either applies. Run `/start-session` to orient.

## Map

| Area | Path | Details |
|---|---|---|
| .NET backend | `src/` | [src/CLAUDE.md](src/CLAUDE.md) |
| Agent layer | `src/agents/` | [src/agents/CLAUDE.md](src/agents/CLAUDE.md) |
| React frontend | `src/frontend/` | [src/frontend/CLAUDE.md](src/frontend/CLAUDE.md) |
| Eval pipeline | `eval/` | [eval/CLAUDE.md](eval/CLAUDE.md) |
| Data pipeline & manual scripts | `scripts/` | [scripts/CLAUDE.md](scripts/CLAUDE.md) |
| Docs corpus | `docs/` | [docs/CLAUDE.md](docs/CLAUDE.md) — **read this before citing any doc** |

## The one rule that matters most here

**Docs in this repo have a confirmed history of being wrong or stale — verify before citing.** README.md links to 9 of 13 wrong ADR filenames and claims "80+ tests" when the real count is ~68; `docs/tech-debt.md` is dated Week 16 but describes at least one item as still-open when it was fixed in Week 18; CHANGELOG.md stops at Week 12 despite seven more weeks of shipped work. Full details in [[docs-are-unverified]] and [docs/CLAUDE.md](docs/CLAUDE.md). Before repeating any doc's claim about what exists or what's fixed, check the actual code.

## Current phase

**Check `git branch -a` before trusting this section — it describes `main` only.** As of this writing `main`'s code is frozen at commit `368f80f` ("real RAGAS eval, e2e 56/60, retrieve.py → /chat", 2026-06-23) — confirmed independently via the matching `eval/experiments/2026-06-23_final_portfolio.json` artifact and `retrieve.py`/`score_ragas.py` content, not just by trusting the Week 19 doc's own "frozen" claim. One further commit (`a053d2c`, 2026-07-03) only edits `docs/weeklyProgress/week19-progress.md` — no code. On `main`: e2e eval 56/60 (93%), real RAGAS scoring (Claude judge + Voyage embeddings) replacing an older custom scorer, with a handful of items explicitly deferred not fixed (a negation/embedding-model regression, a meal-plan rate-limit cascade, two known-failing e2e cases, occasional 60s+ meal-plan timeouts, a cosmetic reporting bug in the e2e harness summary line).

**`main` is Phase 1, frozen. `phase-2` is the actual in-progress work — check it, don't default to `main`'s "paused" framing.** This working tree has `main`'s code checked out (that's what everything below describes), but `phase-2` is where development is happening: 9 commits, most recent dated today, diverged from `main` by ~3,000 lines. Read it with `git show phase-2:<path>` / `git diff main phase-2` rather than checking it out into this tree, unless you're actually switching to work on it.

**What Phase 2 is building** (per `docs/weeklyProgress/phase2-week1-progress.md` and `phase2-week2-progress.md` on that branch, cross-checked against the actual code there — see below): a capability-based agent-registry + pipeline architecture, replacing `AgentOrchestrator`'s hardcoded per-intent dispatch. New: `IAgent` interface (`Name`, `Capabilities`, `HandleAsync(AgentContext, ct)`) that `RecipeSearchPlugin`/`DietValidationPlugin`/`MealPlannerPlugin` now also implement (Phase 1 entry points untouched, both exist in parallel); `AgentRegistry` (capability string → `IAgent`, throws on duplicate registration); `PipelineBuilder`/`PipelineDefinitions`/`PipelineRegistry` (declarative per-`UserIntent` step chains, e.g. `SearchRecipe` = search-then-fan-out-validate); `PipelineRunner` (executes a pipeline, owns span lifecycle transitionally). Cutover is feature-flagged: `Pipelines:Enabled` in `appsettings.json`, **default `false`** — confirmed in the actual file. Only `SearchRecipe` and `ValidateDiet` route through pipelines when enabled; `CreateMealPlan`/`ModifyMealPlan` are explicitly blocked (labeled `P2-8` in `phase2-week2-progress.md` — **not** actually in `docs/tech-debt.md`, see [src/agents/CLAUDE.md](src/agents/CLAUDE.md) for that gap: `MealPlannerPlugin.HandleAsync` doesn't call `SavePlanAsync` and collapses two distinct error messages into one).

**This paragraph is a summary, not the current task list — run `/start-session` for that.** As of the last commit, the doc's own "Remaining" section lists 4 open Day-5 items (not just the 2 mentioned above — also `[Dispatch]` log lines and confirming `CreateMealPlan` falls through cleanly), a full Day 6 regression comparison not yet started, and Day 7 wrap-up not done. Don't treat this paragraph as exhaustive; it summarizes the architecture, not the punch list.

**Two infra footguns worth knowing before touching Qdrant on this branch, found during Phase 2's own Day 5 verification and not otherwise written down anywhere:**
- **No backup exists for the production vector store.** Qdrant Cloud's free tier reclaimed the cluster mid-verification and took the entire 52,155-recipe collection with it, no snapshot. If `/recipes/search` or `/chat` starts returning "couldn't search right now" with no other symptoms, check for a `RpcException Unavailable`/`NotFound: Collection 'recipes' doesn't exist` in the actual exception, not just the friendly user-facing message — the graceful degradation that makes the app not crash also makes this failure mode look identical to an ordinary hiccup.
- **Reloading vectors from the wrong local file fails silently.** `data/embeddings/recipe_vectors.jsonl` (the gitignored file [scripts/CLAUDE.md](scripts/CLAUDE.md)/`CODESPACES.md` describe uploading manually) has existed in at least two incompatible forms — a 10,000-doc/768-dim pre-Voyage file and the current 52,155-doc/1024-dim one. Loading the wrong one **reports success** (`load_qdrant.py` doesn't validate dimensionality) and only fails later, at query time, with a `Vector dimension error`. After any reload, run a live search and check the result count/relevance, not just the load script's exit code. Related: `load_qdrant.py` defaults to REST (port 6333, `prefer_grpc=False`) while the .NET client uses gRPC (6334) — passing the wrong port to the script fails with a non-JSON parse error, not a helpful message.

**Phase 2's docs are unusually rigorous** — worth more trust than Phase 1's, but still verify. They repeatedly self-report the exact failure mode this whole memory layer warns about: multiple days record finding that something previously marked "done" (a described test file, `PipelineRunner` itself) didn't actually exist on disk, caught only by running `find`. That said, verifying independently still turned up two things the docs don't mention:

1. **A real doc-vs-code gap.** Day 3 states the forced pipeline-registry resolution added to `Program.cs` is "deliberately not wrapped in try/catch: a bad pipeline definition is a wiring bug that should stop the app." The actual `Program.cs` on `phase-2` has that resolution *inside* the same `try` block as the Redis pre-warm ping, sharing its `catch` — which only logs `"[Startup] Redis pre-warm ping failed — continuing"` and does not stop the app. A broken pipeline definition would currently be swallowed and misreported as a Redis failure, the opposite of the stated intent.
2. **A dead field.** `AgentResult.TraceOutput` (`object?`) is declared in `IAgent.cs` with a doc-comment describing its purpose, but nothing in the codebase reads or writes it — the Day 4 doc explicitly describes deciding *against* building this bridge ("no new fields"), so this looks like a leftover from the original Day 1 design that the later decision never removed.

Don't assume "paused" describes the whole project, and don't assume Phase 2's own docs are fully caught up with Phase 2's own code either — the pattern repeats at every layer.

## Conventions that hold across the whole backend

Confirmed consistent with no exceptions found across every backend file sampled — see the linked rule for detail and rationale:
- DI/service wiring goes through one factory-branch pattern in `src/api/ServiceRegistration.cs` — [[dotnet-di-pattern]]
- Logging is `ILogger<T>` with a bracketed component tag + named placeholders, never string interpolation — [[structured-logging]]
- `I`-prefixed interfaces, `Async`-suffixed methods, one class per file — [[dotnet-naming-conventions]]
- LLM/external calls are meant to go through `CircuitBreaker` + retry-then-fallback, though two endpoints don't yet follow this — [[resilience-pattern]]

Frontend and Python areas are much smaller and don't have their own dedicated convention set beyond what's in their area-specific CLAUDE.md.

## Footguns worth knowing before you start

- A tracked file is duplicated at a doubly-nested path inside `RecipeAgent/` — see [src/agents/CLAUDE.md](src/agents/CLAUDE.md) for the exact paths and which copy is correct.
- `Qdrant.Client` NuGet version differs between the Recipe agent project (1.12.0) and the API project (1.18.1) — see [src/agents/CLAUDE.md](src/agents/CLAUDE.md).
- `.env.example` is missing the two most consequential config switches (`LlmProvider`, `EmbeddingProvider`) and every cloud provider API key that `docker-compose.yml` actually references — see [src/CLAUDE.md](src/CLAUDE.md).
- The frontend has zero test coverage and no test runner configured — see [src/frontend/CLAUDE.md](src/frontend/CLAUDE.md).
- The eval pipeline (`eval/`) has no dependency manifest — see [eval/CLAUDE.md](eval/CLAUDE.md) and [[run-eval-pipeline]].
- Codespaces/devcontainer setup is a *separate* path from local setup, with its own footguns (manual gitignored-file upload) — see [scripts/CLAUDE.md](scripts/CLAUDE.md) and `CODESPACES.md`.

## Skills available for this repo

- [[add-llm-or-embedding-provider]] — wiring a new LLM/embedding backend into the existing config-driven provider pattern
- [[run-eval-pipeline]] — running the current `retrieve.py` → `score_ragas.py` RAGAS chain, including the undocumented dependency list and Python-version pin
- [[add-new-agent]] — branch-aware: Phase 1 direct-dispatch pattern on `main`, `IAgent`/`AgentRegistry` pattern on `phase-2`
- [[add-pipeline-definition]] — `phase-2` only: chaining agents through `PipelineBuilder`/`PipelineDefinitions`
- [[add-new-intent]] — new `UserIntent` classification, including the test-update convention this repo's tech-debt log shows is load-bearing
- [[verify-local-stack]] — bring-up plus the actual health checks, not just `make up`
- [[verify-deploy]] — confirming a push landed on Railway/Vercel (no deploy command exists in-repo)
- [[add-observability-span]] — extending `Tracing.cs`, including the Phase 2 runner-owned-span nuance
- [[run-regression-sweep]] — e2e pass/fail regression check against `test_e2e_sweep.py`'s own baseline, distinct from the RAGAS quality check above
- [[write-progress-doc]] — the weeklyProgress template, with the "verify before writing" rule this project has needed twice over

## Workflow chain — command → agent → skill

`/close-day` is the demonstrated end-to-end chain: it runs the test suite, drafts today's progress doc via [[write-progress-doc]], then hands the draft and diff to the `done-verifier` agent (`.claude/agents/done-verifier.md`) — a fresh context window that independently re-checks every "Built"/"Fixed" claim and Definition-of-Done box against the actual filesystem and test output, rather than trusting the drafting session's own assertions. Only after that comes back clean (or has been reconciled) does `/close-day` commit. This chain exists specifically because this project has repeatedly shipped docs claiming something was built that a plain `find` shows wasn't — on both `main` (audited in this session) and `phase-2` (self-reported in its own docs, at least four times). `/start-session` and `/regression-check` are the other two commands; `rules-reviewer` (`.claude/agents/rules-reviewer.md`) is available separately for checking a diff against `.claude/rules/` specifically, not wired into `/close-day` by default — invoke it directly when a change touches DI wiring, logging, naming, or resilience patterns and you want that checked before considering it done.
