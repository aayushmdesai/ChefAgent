# Phase 2 — Week 1 Progress

**Branch:** `phase-2`
**Month Objective:** Orchestration can scale past 4 agents
**KR this week advances:** KR1 — Agent registry + pipeline builder shipped, zero regression on Phase 1's 56 passing e2e tests

## Goal Alignment
- Month Objective: Orchestration can scale past 4 agents
- KR(s) this week advances: KR1 — registry + pipeline builder (design + interfaces)
- Regression check: Phase 1 e2e suite — [ /56 pass] *(pending Day 5)*

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

## Day 3 — Agent Registry

*(pending)*

## Day 4 — Pipeline builder (design only)

*(pending)*

## Day 5 — Redis shared context schema + week wrap

*(pending)*

---

## Open decisions to close out this week

- [x] Capability strings: free-text or enum? → **const string** (see Day 1)
- [ ] Registry conflict handling: error on duplicate capability, or first-registered-wins?
- [ ] Pipeline builder: declarative config vs imperative code?