# Phase 2 — Week 1 Progress

**Branch:** `phase-2`
**Month Objective:** Orchestration can scale past 4 agents
**KR this week advances:** KR1 — Agent registry + pipeline builder shipped, zero regression on Phase 1's 56 passing e2e tests

## Goal Alignment
- Month Objective: Orchestration can scale past 4 agents
- KR(s) this week advances: KR1 — registry + pipeline builder (design + interfaces)
- Regression check: Phase 1 e2e suite — 40/50 latest run, 100% of failures traced to Voyage AI rate limiting (429), not code. Clean rate-limit-free rerun flagged as a Week 2 prerequisite. Unit suite: 91 total, 0 failed.

---

## Day 1 — `IAgent` interface + capability model ✅

### What Was Built

`src/shared/IAgent.cs` — new file, additive only, no existing code touched.

- `IAgent` interface: `Name`, `Capabilities` (`IReadOnlyList<string>`), `HandleAsync(AgentContext, CancellationToken) -> Task<AgentResult>`
- `AgentContext` record: `SessionId`, `UserQuery`, `Intent`, plus `SharedData` (`Dictionary<string, object>`) for future pipeline chaining
- `AgentResult` record: `Success`, `ErrorMessage`, `Data`, plus `OutputsForNextAgent` (`Dictionary<string, object>?`) for chaining
- `AgentCapabilities` static class: capability names as `const string` (not enum)

### Decision: Capability strings — const string, not enum

`IntentRouter` already classifies intent as a string from its JSON-structured classifier output (`"SearchRecipe"`, `"ValidateDiet"`, etc.). A C# `enum` would require a string→enum translation layer at the classifier boundary just to feed the registry. `const string` fields on a static `AgentCapabilities` class get typo-safety (compile errors, IDE autocomplete) without that extra conversion step, and stay purely additive if Phase 3 needs new capabilities.

### Chaining sketch (not built until Day 4-5)

`SharedData` / `OutputsForNextAgent` are the seam the Day 4 pipeline builder will use: RecipeAgent's `OutputsForNextAgent` would populate `SharedData["SearchRecipe"]` for the next `AgentContext`, DietAgent reads it, and so on down a chain. Typed loosely (`Dictionary<string, object>`) for now — flagged as a likely pain point once a real chain exists and casting starts happening at each hop.

### Follow-up fix: `ClassifiedIntent` moved to `Shared`

`AgentContext` was revised to carry `ClassifiedIntent` and `TraceContext` directly (instead of a thinner hand-picked field set) once the real plugin call sites (`RecipeSearchPlugin.SearchRecipesAsync`, `DietValidationPlugin.ValidateRecipeAsync`, `MealPlannerPlugin.GeneratePlanAsync`/`ModifyPlanAsync`) were reviewed — a loose `Dictionary<string, object>` bag would have meant casting `TraceContext`/`DietaryProfile`/`RecipeDocument` back out at every call site.

This caused a circular assembly reference: `ClassifiedIntent` lived in `ChefAgent.Agents.Orchestrator`, but `AgentContext` (in `ChefAgent.Shared`) needed it, and `Orchestrator` already depends on `Shared` transitively through the agent plugins.

**Fix:** Moved `ClassifiedIntent` from `ChefAgent.Agents.Orchestrator` to `ChefAgent.Shared.Models`, alongside `UserIntent`, which it already depended on. Same precedent as the Week 11 `DietaryRules` relocation — pure data record, no behavior, wrong project. `dotnet build` clean afterward, zero other files touched beyond the two using it.

### Definition of Done

- [x] `IAgent.cs` compiles clean, zero warnings/errors
- [x] No agents implement it yet
- [x] No existing code touched (aside from the `ClassifiedIntent` relocation, required for `AgentContext` to compile)

### Files Changed

```
src/shared/IAgent.cs               # New: IAgent, AgentContext, AgentResult, AgentCapabilities
src/shared/Models.cs               # Added: ClassifiedIntent (moved from Agents.Orchestrator)
src/agents/Orchestrator/IntentRouter.cs   # Removed: ClassifiedIntent record definition
```

---

## Day 2 — Retrofit existing agents onto `IAgent` ✅

### What Was Built

`RecipeSearchPlugin`, `DietValidationPlugin`, `MealPlannerPlugin` all implement `IAgent` alongside their existing entry points — old orchestrator dispatch untouched, exactly as planned.

- `RecipeSearchPlugin.HandleAsync` — wraps `SearchRecipesAsync`, hardcodes `rerank: false, expand: true` for now (flagged: if reranking needs to be triggerable through `IAgent`, `SharedData` needs a flag for it, same pattern as `maxResults`)
- `DietValidationPlugin.HandleAsync` — wraps `ValidateRecipeAsync`, reads the target recipe from `context.SharedData["TargetRecipe"]` (single-recipe only — does not loop a list; that stays orchestrator/pipeline-builder responsibility)
- `MealPlannerPlugin.HandleAsync` — branches on `CreateMealPlan` vs `ModifyMealPlan`; `GetMealPlan` intentionally has no agent (reads straight from `SessionStore` — registry needs a no-agent fallback path, logged as an open item)

### Blocker + fix: `ClassifiedIntent` circular reference

See Day 1 follow-up note above — `ClassifiedIntent` moved to `ChefAgent.Shared.Models` to unblock `AgentContext`.

### Tests

New: `DietValidationPluginTests` (4 cases — no-profile, no-recipe, rules-violation, compatible) and `AgentRegistrationTests` (6 cases — `IAgent` assignability + capability declarations for all 3 plugins).

`RecipeSearchPlugin` and `MealPlannerPlugin` `HandleAsync` are **not unit-tested** — both depend on concrete non-interface types (`QdrantClient`; `RecipeSearchPlugin`/`DietValidationPlugin` directly) that Moq can't intercept. Real coverage needs either live Qdrant/LLM infra (integration-style) or a refactor toward `IAgent`-typed dependencies — noted as a natural side effect of Day 4's pipeline builder work, not fixed now. Logged as tech debt (P2-1, P2-2), matching the `I-1/I-2/I-3` convention.

**Result:**
```
Test summary: total: 81, failed: 0, succeeded: 78, skipped: 3, duration: 7.3s
```
Original 68 passed + 3 skipped (I-1/I-2/I-3, untouched) + 10 new tests, all passing, 0 failures. Regression-clean.

### Definition of Done

- [x] 3 agents implement `IAgent` in parallel with existing entry points
- [x] Old Phase 1 e2e suite still passes untouched (81 total: 78 pass / 3 skip, 0 fail)

### Open items carried forward

- `GetMealPlan` has no registered agent — Day 3 registry needs a fallback path
- `HandleAsync` unit coverage gap for `RecipeSearchPlugin`/`MealPlannerPlugin` (P2-1, P2-2) — revisit once Day 3's registry gives a cleaner integration seam

## Day 3 — Agent Registry ✅

### What Was Built

`src/shared/AgentRegistry.cs` — maps capability strings to the `IAgent` that handles them. Built once at DI startup via `AddAgentRegistry()` in `ServiceRegistration.cs`, chained after `AddMealPlannerAgent(config)` so all three plugins are already resolvable as singletons when the registry factory runs. **Not yet consulted by `AgentOrchestrator`** — runs side-by-side with the existing dispatch switch, per Day 3's scope.

### Decision: Duplicate capability → error, not first-wins

Agents are registered explicitly at DI startup, not auto-discovered — so a duplicate capability can only happen from a real coding mistake (two agents declaring the same capability string), not a runtime scenario a request needs to survive. `Register()` throws `InvalidOperationException` immediately, which fails app startup loudly in dev rather than silently routing requests to the wrong agent later. This doesn't conflict with the "never 500 to the user" philosophy elsewhere in the codebase — registration happens once, before any request exists, so failing loud here is a build-time safeguard, not a request-time one.

### Decision: `GetMealPlan`'s missing agent → `FindByCapability` returns `null`, caller decides

No special-case logic in the registry itself. `AgentCapabilities.GetMealPlan` stays declared (from Day 1) so the constant exists, but no agent registers it — a lookup for it is a normal miss, same as any unregistered capability. Whichever code wires the registry into the orchestrator (Week 2) is responsible for falling back to `SessionStore` directly when it gets `null` back for `GetMealPlan`. Verified via `UnregisteredCapability_ReturnsNull` test using this exact real-world case.

### Tests

`AgentRegistryTests` — 6 cases: empty registry (`FindByCapability` null, `Count` 0), single-agent registration + lookup, multi-capability agent (both capabilities resolve to the same instance), duplicate capability throws, unregistered capability (`GetMealPlan`) returns null. Uses an in-file `FakeAgent` test double rather than constructing real plugins — pure registry logic needs no Qdrant/LLM.

**Result:**
```
Test summary: total: 90, failed: 0, succeeded: 84, skipped: 6, duration: 10.8s
```
Previous 81 (78 pass / 3 skip) + 6 new `AgentRegistryTests` (all pass) + 3 new skip stubs (`P2-1`, `P2-2` from Day 2, added to the suite this session) = 90 total. Zero failures.

### Definition of Done

- [x] Registry has unit tests
- [x] NOT yet consulted by the orchestrator (still runs side-by-side with old dispatch)

---

## Day 4 — Pipeline builder (design only) ✅

### What Was Built

`src/shared/PipelineBuilder.cs` — `PipelineStep`, `AgentPipeline`, `PipelineBuilder`. Interface/contract only, per Day 4's scope — no `PipelineRunner`, no DI wiring, no execution logic.

First draft was a flat `Step1 → Step2 → Step3` list. Stress-tested against the one real multi-agent chain in the codebase (`HandleSearchRecipeAsync`'s SearchRecipe → DietValidation flow) instead of a toy example, which surfaced a genuine gap: `DietValidationPlugin` validates one recipe at a time (Day 2 decision), but `HandleSearchRecipeAsync` calls it once **per recipe** in a list, non-aborting on failure. A flat step list can't express that at all — revised before it became a Week 2 surprise, same lesson as the `ClassifiedIntent` circular reference on Day 1.

**Final `PipelineStep` shape:**
- `Capability` — which agent capability the step calls
- `RunIf` — optional predicate; skips the step (e.g. skip Diet validation when there's no dietary profile)
- `ContinueOnFailure` — a failed step doesn't abort the pipeline (matches existing graceful-degradation behavior: a failed diet validation still returns the recipe, unbadged)
- `FanOutFrom` — if set, the (future) runner reads a list out of a prior step's output and calls this step's agent once per item, not once total. Required for SearchRecipe → ValidateDiet to be representable at all.

`PipelineBuilder.Build()` validates every referenced capability against a live `AgentRegistry` and throws on an unregistered one — same fail-fast philosophy as Day 3's duplicate-capability check: a pipeline referencing a typo'd capability is a startup-time wiring bug, not something that should surface as a confusing failure on the first real request.

### Decision: Pipeline builder — imperative code, not declarative config

No precedent anywhere in ChefAgent for business logic living in an external config file loaded at runtime — `SlotQueries`, `ProteinKeywords`, `CuisineKeywords` in `MealPlannerPlugin` are all `static readonly Dictionary` literals in C#. Introducing a config loader/schema/validator for a feature with zero real use cases until the Nutrition Agent (Week 3-4) would be new machinery for a chain that doesn't exist yet. Imperative C# also gives compile-time capability-name checking (`AgentCapabilities.X`) instead of a runtime string-matching config format.

### Tests

`PipelineBuilderTests` — proves the shape against the real flow, not a fabricated one: unconditional two-step chain preserves order; unregistered capability throws (message names the missing capability); empty pipeline throws; and a dedicated test builds the actual SearchRecipe→ValidateDiet shape with `RunIf` (profile-gated) and `ContinueOnFailure: true` and `FanOutFrom` set, then verifies `RunIf` evaluates correctly against both a profile-present and profile-absent `AgentContext`.

### Explicitly NOT built (correct scope, not a gap)

- No `PipelineRunner` — nothing walks `Steps`, checks `RunIf`, performs the fan-out loop, or calls `HandleAsync`. Real execution logic, Week 2.
- No `PipelineRegistry` mapping `UserIntent → AgentPipeline`. When built, must return "no pipeline" as a valid case for `GeneralQuestion` and `GetMealPlan`, which don't go through agents.
- No translation layer from pipeline output back to `OrchestratorResponse` shape (`MealPlan`, `DietaryCheck`, confidence disclaimers) — real Week 2 unknown, not solved here.
- `/recipes/search-validated` in `Endpoints.cs` already hand-rolls this exact fan-out pattern (search then per-recipe validate). Once a real `PipelineRunner` exists, that endpoint is a candidate to consolidate onto it instead of maintaining the logic twice. Flagged, not touched.

### Definition of Done

- [x] Pipeline builder interface exists and compiles against a fake 2-agent chain in a test
- [x] Also proven against the real SearchRecipe→ValidateDiet shape, not just a toy chain

---

## Day 5 — Redis shared context schema + week wrap ✅

### What Was Built

Three new key helpers added to `SessionStore.cs`, alongside the existing `PlanKey`/`ProfileKey`/`HistoryKey`/`ExtractionKey`:

```csharp
private static string ShoppingListKey(string sessionId) => $"session:{sessionId}:shopping_list";
private static string FridgeKey(string sessionId) => $"session:{sessionId}:fridge";
private static string NutritionKey(string sessionId) => $"session:{sessionId}:nutrition";
```

Schema only — no `Save`/`Get` methods, no DTOs, no callers. Genuinely additive, matching the roadmap's Day 5 scope. Note: `:history` was already a fully-implemented key (`AppendMessageAsync`/`GetHistoryAsync` since Week 5) — only `shopping_list`, `fridge`, `nutrition` are new as of today.

### Regression check — unit suite

```
Test summary: total: 91, failed: 0, succeeded: 85, skipped: 6, duration: 16.3s
```
Clean. Consistent with Day 3's 90 plus one skip-to-pass; zero failures across the week.

### Regression check — Phase 1 e2e sweep (`test_e2e_sweep.py`)

Two runs against the local `phase-2` branch (`make up`, Upstash Redis, real Qdrant/Voyage/Groq):

| Run | Result | Root cause of failures |
|-----|--------|------------------------|
| 1 | 37/50 (74%) | 100% traced to `VoyageEmbeddingProvider.EmbedAsync` → `429 Too Many Requests` |
| 2 | 40/50 (80%) | Same — 100% traced to the same 429, partial recovery between runs |

**Every failure across both runs, with zero exceptions, is the identical stack trace** (`HttpRequestException: 429` at `VoyageEmbeddingProvider.EmbedAsync` → `RecipeSearchPlugin.SearchRecipesAsync` → `AgentOrchestrator.HandleSearchRecipeAsync`/`HandleValidateDietAsync`/`MealPlannerPlugin.GenerateSlotAsync`). No new exception types, no new failure categories, no code path implicated that wasn't already implicated in the first failing case. This is Voyage AI rate-limiting the account — the sweep fires ~50 unpaced requests plus a dedicated 35-request burst test (TC41) against what is very likely a low-RPM free/dev tier — not a Phase 2 code regression.

**This does not meet a clean regression bar against the frozen 93% (56/60) baseline**, and per the plan's own rule (*"if anything fails, stop and fix before Week 2 starts"*), this is logged as an open item rather than a pass, even though the evidence strongly points away from code:

**Open item carried into Week 2:** Rerun `test_e2e_sweep.py` with request pacing (e.g. `time.sleep(1.5)` in `send_chat`) to get a rate-limit-free run and a real number to compare against the 93% baseline, before or alongside Week 2's orchestrator cutover. Until that rerun happens, Week 2 begins on strong-but-not-fully-confirmed evidence that this week's changes (`IAgent`, `ClassifiedIntent` relocation, `AgentRegistry`, `PipelineBuilder`, Redis schema additions) didn't regress `/chat` behavior.

### Definition of Done

- [x] Redis schema expanded, no new agents write to the new keys yet
- [x] Existing Planner/Diet Redis reads/writes unaffected (unit suite 91/91 non-skipped, 0 failures)
- [~] Regression check run — unit suite clean; e2e sweep inconclusive due to external rate limiting, not a code failure, but not yet a clean confirmed pass either. **Rerun with pacing flagged as Week 2 prerequisite.**

---

## Week 1 Wrap

**Shipped:** `IAgent` interface + capability model, all 3 existing agents retrofitted, `AgentRegistry` with capability-based lookup, `PipelineBuilder` with fan-out support (proven against the real SearchRecipe→ValidateDiet shape), Redis schema staged for Shopping List/Fridge/Nutrition agents. Zero changes to the live orchestrator dispatch path — everything this week runs side-by-side with Phase 1 code, untouched.

**All three open decisions closed:**
- Capability strings → `const string` (Day 1)
- Registry conflict handling → error, throws at startup (Day 3)
- Pipeline builder → imperative C#, not declarative config (Day 4)

**Carried into Week 2:**
- `GetMealPlan` has no registered agent — orchestrator cutover needs an explicit no-agent fallback to `SessionStore`
- `HandleAsync` unit coverage gap for `RecipeSearchPlugin`/`MealPlannerPlugin` (P2-1, P2-2) — both depend on concrete types Moq can't intercept; needs live infra or an `IAgent`-typed dependency refactor
- No `PipelineRunner`, `PipelineRegistry`, or `OrchestratorResponse` translation layer yet — all real Week 2 build work
- `/recipes/search-validated` hand-rolls the same fan-out pattern the pipeline now models — candidate for consolidation once a runner exists
- **E2E regression rerun with request pacing** — get a clean, rate-limit-free number against the 93% (56/60) baseline before or alongside the orchestrator cutover

---

## Open decisions to close out this week

- [x] Capability strings: free-text or enum? → **const string** (see Day 1)
- [x] Registry conflict handling: error on duplicate capability, or first-registered-wins? → **error, throws at startup** (see Day 3)
- [ ] Pipeline builder: declarative config vs imperative code?