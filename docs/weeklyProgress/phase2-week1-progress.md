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

### Definition of Done

- [x] `IAgent.cs` compiles clean, zero warnings/errors
- [x] No agents implement it yet
- [x] No existing code touched

### Files Changed

```
src/shared/IAgent.cs   # New: IAgent, AgentContext, AgentResult, AgentCapabilities
```

---

## Day 2 — Retrofit existing agents onto `IAgent`

*(pending)*

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