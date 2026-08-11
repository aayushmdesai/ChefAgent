# src/agents/ — the agent layer

**This file describes `phase-2`/`phase-2-agentic`.** Two architectures coexist in this codebase right now: the Phase 1 direct-dispatch pattern (still the live default) and a Phase 2 pipeline layer (built, partially cut over behind a feature flag). Both are real and both matter — don't describe only one.

## The four agents

Four separate `.csproj` projects, each a distinct concern:

- **`RecipeAgent/`** (`ChefAgent.Agents.Recipe`) — `RecipeSearchPlugin.SearchRecipesAsync` embeds the query via `IEmbeddingProvider`, searches Qdrant, optionally expands the query and/or reranks results via `ILlmProvider` (see footguns below). Owns `QueryPreprocessor` and `RecipeReranker`.
- **`DietAgent/`** (`ChefAgent.Agents.Diet`) — `DietValidationPlugin`, a 3-tier validator (rules → ambiguous-tier escalation → LLM fallback) built on `DietaryRules.cs`, which lives in `src/shared/` (moved there in Week 11), namespace `ChefAgent.Agents.Diet` despite the file's location.
- **`PlannerAgent/`** (`ChefAgent.Agents.Planner`) — `MealPlannerPlugin`, sequential calls into `RecipeSearchPlugin` per meal slot, backed by `SessionStore` (Redis, 7-day TTL).
- **`Orchestrator/`** (`ChefAgent.Agents.Orchestrator`) — `IntentRouter` (rules-first classifier) and `AgentOrchestrator` (routes a classified intent, holds conversation history).

`RecipeSearchPlugin`, `DietValidationPlugin`, and `MealPlannerPlugin` **each implement two separate entry points now**: their original Phase 1 methods (`SearchRecipesAsync`, `ValidateRecipeAsync`, `GeneratePlanAsync`/`ModifyPlanAsync` — unchanged, still what Phase 1 dispatch calls) **and** `IAgent.HandleAsync` (new, what the pipeline layer calls). Both exist side by side on the same class; the Phase 1 methods were not touched or wrapped, per the confirmed pattern of every "Day 2" retrofit in `phase2-week1-progress.md`.

## The pipeline layer (`src/shared/`, new)

- **`IAgent.cs`** — the interface (`Name`, `Capabilities: IReadOnlyList<string>`, `HandleAsync(AgentContext, ct) -> Task<AgentResult>`), plus `AgentContext` (`Classified: ClassifiedIntent`, `TraceCtx: TraceContext`, `SharedData: Dictionary<string, object>`), `AgentResult` (`Success`, `ErrorMessage`, `Data`, `OutputsForNextAgent`, `TraceOutput`), and `AgentCapabilities` (capability names as `const string`, deliberately not an enum — avoids a translation layer at `IntentRouter`'s string-based classifier boundary).
- **`AgentRegistry.cs`** — maps capability string → `IAgent`. `Register()` throws on a duplicate capability claim (startup-time wiring bug, not routed around). `FindByCapability` returns `null` for unregistered capabilities (e.g. `GetMealPlan`, which intentionally has no agent — reads `SessionStore` directly).
- **`PipelineBuilder.cs` / `PipelineDefinitions.cs`** — declarative per-`UserIntent` step chains. `PipelineStep` has `Capability`, `RunIf` (skip predicate), `ContinueOnFailure`, `FanOutFrom`/`FanOutItemKey` (chain one agent's output into N calls to the next), `SpanName`. `PipelineDefinitions.BuildAll()` registers all four: `SearchRecipe` (search → fan-out validate, `RunIf: HasActionableProfile`), `ValidateDiet` (search maxResults:1 → fan-out validate over the single result — **not** a bare validation call, mirrors `HandleValidateDietAsync`'s actual behavior), `CreateMealPlan`, `ModifyMealPlan` (both single-step, registered anyway so the orchestrator has one lookup-and-run-or-fallback code path rather than branching per intent).
- **`PipelineRegistry.cs`** — keyed on `UserIntent` (not string — closed enum, compiler-checked). `FindByIntent` returns `null` for `GetMealPlan`/`GeneralQuestion`/`Unknown`.
- **`PipelineRunner.cs`** — executes a pipeline: runs each step, handles fan-out (copies `SharedData` and a shallow copy of `Classified` per branch — isolates the mutable string setters on `ClassifiedIntent`, but **not** nested `DietaryProfile` references, which stay shared across fan-out branches), owns span lifecycle (see Observability below), catches agent exceptions and converts them to a failed `AgentResult` rather than propagating.

## How the pipeline relates to the orchestrator — verified on this branch, current as of the last commit

`AgentOrchestrator.RouteAsync` → `DispatchAsync`:
```csharp
if (_pipelinesEnabled)
{
    var mapped = await TryRunPipelineAsync(classified, orchCtx);
    if (mapped is not null) return mapped;
}
return classified.Intent switch { /* the original Phase 1 switch, unchanged */ };
```
`_pipelinesEnabled` is `config.GetValue<bool>("Pipelines:Enabled", false)`, read once at construction. **Confirmed in `src/api/appsettings.json` right now: `"Pipelines": { "Enabled": false }`.** Phase 1 direct dispatch is the live default. When the flag is `true`, `TryRunPipelineAsync` returns `null` (falling through to Phase 1) for: no registered pipeline, planner intents (no mapper written yet — see blockers), or a pre-pipeline guard firing (`ValidateDiet`'s no-profile early return is duplicated here rather than expressed as a `RunIf`, specifically because the guard must produce its own response *before* any agent runs, which a skippable pipeline step can't do). Only `SearchRecipe` and `ValidateDiet` have a mapper (`MapSearchRecipeResult`/`MapValidateDietResult` in `AgentOrchestrator.cs`) — these translate `PipelineResult` back into the exact same `OrchestratorResponse` shape Phase 1 produces; behavior parity was the explicit bar, not improvement.

## Open blockers before a fuller cutover (verified against code + the doc's own checklist, not just cited)

- **`CreateMealPlan`/`ModifyMealPlan` are pipeline-registered but never dispatched through the pipeline** — `TryRunPipelineAsync`'s `switch` has no case for them, falls to `default: return null`. Blocking reason (tech-debt `P2-8`, see caveat below): `MealPlannerPlugin.HandleAsync` doesn't call `SavePlanAsync` and collapses two distinct Phase 1 error messages into one generic failure.
- **No `[Dispatch]` log lines exist** — confirmed absent via grep on this branch. Without them, there's no way to tell from a response alone whether a request took the pipeline or Phase 1 path (both are designed to produce identical output).
- **`ValidateDiet` through the pipeline has never been exercised live**, per the doc's own checklist — the reshaped search→fan-out-validate chain compiles and unit-tests clean but hasn't been hit with `Pipelines__Enabled=true` end to end.
- **Langfuse span nesting is unverified** — unit tests structurally can't prove this (see Observability below); needs a real trace.
- **`IntentRouter` discovering intents from the registry** (rather than the registry being separately populated) — named on every weekly carry-forward list since Week 1, not started as of this commit.

## Tech debt — a real gap between what the docs claim and what's in `docs/tech-debt.md`

The `phase2-week*-progress.md` docs repeatedly say things like "P2-3 added", "P2-8 added" — **but `docs/tech-debt.md` on this branch contains zero `P2-`-prefixed entries.** Verified by direct grep, not inference. Only 5 items from Phase 2 Week 2 Day 1 actually made it into the file, using the pre-existing ID scheme: `I-4` (reopened), `M-5`, `M-6`, `Inf-7`, `T-10`. If you need the P2-1 through P2-10 list (fan-out isolation, `maxResults` as untyped `SharedData`, `HasActionableProfile` duplicated twice, the feature-flag-removal trigger condition, etc.), it only exists in the progress docs themselves — `docs/tech-debt.md`'s own header still says "Last updated: Week 16," unchanged.

## Orchestration flow (Phase 1 path, still live by default)

`/chat` → `Endpoints.cs` → `AgentOrchestrator` → `IntentRouter.ClassifyAsync` (rules regex first; short `<=8`-word follow-ups reclassified `SearchRecipe`→`GeneralQuestion` if the prior turn was `GeneralQuestion`) → `DispatchAsync` → (pipeline attempt if enabled, else) the original per-intent handler methods.

## Footguns — re-verified fresh on this branch, all still hold

1. **`Qdrant.Client` version mismatch** — `ChefAgent.Agents.Recipe.csproj` still 1.12.0, `ChefAgent.Api.csproj` still 1.18.1. Unchanged from Phase 1.
2. **`expand` default asymmetry** — `SearchRecipesAsync`'s own parameter still defaults `true`; `RecipeSearchRequest` DTO still defaults `Expand = false`. Both Phase 1 orchestrator call sites (`AgentOrchestrator.cs`, lines ~697 and ~851) still call without specifying `rerank`/`expand`, so they inherit the method defaults — `rerank` stays `false` either way.
3. **Reranker still unreachable from `/chat`** — `ChatRequest` still has no `Rerank` field; confirmed both Phase 1 direct-dispatch call sites don't pass it.
4. **Duplicate dictionary file, still present**: `src/agents/RecipeAgent/src/agents/RecipeAgent/Dictionaries/frequency_dictionary_en_80k.txt` alongside the correct `src/agents/RecipeAgent/Dictionaries/frequency_dictionary_en_80k.txt`.
5. **New on this branch — a doc-vs-code gap in `Program.cs`.** The forced `PipelineRegistry` resolution at startup is documented as "deliberately not wrapped in try/catch: a bad pipeline definition is a wiring bug that should stop the app" — but it's actually inside the same `try` block as the Redis pre-warm ping, sharing one `catch` that only logs and continues. A broken pipeline definition would currently be silently swallowed and misreported as a Redis failure. Confirmed by reading `Program.cs` directly, not by trusting the doc.
6. **New on this branch — a dead field.** `AgentResult.TraceOutput` (`object?`) is declared with a doc-comment but confirmed unreferenced anywhere else in the codebase (`grep -rn "TraceOutput"` finds only the declaration) — a leftover from before the Day 4 decision to explicitly not build this bridge.
7. **New on this branch — `AddAgentRegistry()`/`AddApiServices()` are each called twice** in `ServiceRegistration.cs`'s `AddChefAgentServices`. Verified harmless (last-registration-wins DI semantics, idempotent ASP.NET Core APIs) but genuinely dead code — see [src/CLAUDE.md](../CLAUDE.md).

## Observability nuance specific to this layer

Pipeline-dispatched calls get spans from `PipelineRunner`, not from the agent itself — a `pipeline.{Intent}` boundary span, one span per step (name from `PipelineStep.SpanName`, defaulting to `pipeline.{Capability}`; `PipelineDefinitions` sets explicit Phase 1-matching names — `recipe_agent.search`, `diet_agent.validate` — so traces stay comparable across the cutover), one per fan-out item. This is explicitly transitional (tech-debt-tracked as needing agent-owned spans eventually) and has a known limitation: fan-out items get an index, not a meaningful name (Phase 1 passed the recipe title). Unit tests cannot verify span nesting — with tracing disabled every span call short-circuits to `TraceContext.None`, so a test can't distinguish "context wasn't threaded through" from "tracing is off." The only real verification is a live trace in Langfuse. See [[add-observability-span]].

See [[resilience-pattern]] for how agent calls are expected to handle failures, [[dotnet-naming-conventions]] for the project-per-agent structure convention, [[add-new-agent]] for extending either pattern, and [[add-pipeline-definition]] for extending the pipeline layer specifically.
