---
name: add-new-agent
description: Add a new agent to the ChefAgent backend and wire it into dispatch. Use when asked to "add a new agent", "wire up a [X] agent", or "create an agent for [capability]".
---

# Add a new agent

This repo has two different agent-wiring patterns depending on branch. Check which one applies before doing anything else:

```bash
git branch --show-current
test -f src/shared/IAgent.cs && echo "IAgent pattern (phase-2)" || echo "Phase 1 direct-dispatch pattern (main)"
```

## Phase 1 pattern (`main` — no `IAgent.cs`)

1. New `.csproj` project under `src/agents/<AgentName>/`, referencing `ChefAgent.Shared` (and `Microsoft.SemanticKernel` if it needs `ILlmProvider`). Add it to `src/ChefAgent.sln`.
2. Register it in `src/api/ServiceRegistration.cs`: a new private `AddXAgent(this IServiceCollection services, IConfiguration config)` extension method, following the shape of `AddRecipeAgent`/`AddDietAgent`/`AddMealPlannerAgent` — singleton, factory lambda resolving dependencies via `sp.GetRequiredService<T>()`. Add one call to it from `AddChefAgentServices`. See [[dotnet-di-pattern]].
3. Add a `UserIntent` enum member if this agent handles a genuinely new intent (`src/shared/Models.cs`) — see [[add-new-intent]] if so, it's a bigger change than just this agent.
4. Add a case to `AgentOrchestrator`'s dispatch switch (`RouteAsync`/`DispatchAsync`) routing the relevant intent(s) to the new agent, wrapped in try/catch per [[resilience-pattern]] — don't copy the two endpoints (`/recipes/search`, `/recipes/search-validated`) that currently skip this.

## Phase 2 pattern (`phase-2` branch — `IAgent.cs` exists)

1. Implement `IAgent` on the class: `Name` (string), `Capabilities` (`IReadOnlyList<string>`), `HandleAsync(AgentContext context, CancellationToken ct)`. This can live alongside an existing Phase-1-style entry point on the same class — that's the established pattern (`RecipeSearchPlugin`, `DietValidationPlugin`, `MealPlannerPlugin` all do this; old dispatch untouched).
2. Add capability name(s) as `const string` fields on `AgentCapabilities` (`src/shared/IAgent.cs`) — not an enum, deliberately, to avoid a translation layer at the `IntentRouter` JSON-classifier boundary.
3. Register the agent instance with `AgentRegistry` in `ServiceRegistration.cs`'s `AddAgentRegistry()` — registration throws on a duplicate capability, by design; that's a startup-time wiring bug, not something to route around.
4. If this agent should be reachable from `/chat`, reference its capability from a `PipelineDefinitions` entry — see [[add-pipeline-definition]].
5. `HandleAsync` on agents that depend on concrete external clients (`QdrantClient`) or concrete sibling plugins rather than interfaces won't be unit-testable with Moq — this is a known pattern, already hit twice (see the `[Fact(Skip = "P2-1: ...")]`/`[Fact(Skip = "P2-2: ...")]` placeholders in `RecipeSearchPluginTests.cs`). Those `P2-` labels are cited from `phase2-week1-progress.md` — verified they are **not** actually in `docs/tech-debt.md` (that file has no `P2-`-prefixed entries at all, despite the progress docs repeatedly saying items were "added" there). Either accept an integration-style test or note the same limitation with a skip-marked placeholder like the existing ones.

## Verification (both patterns)

- `dotnet build src/ChefAgent.sln --no-restore` clean.
- Phase 2: the startup log shows `[AgentRegistry] Registered '{Capability}' -> {AgentName}` for every capability this agent declares, and (if pipeline-wired) `Registered pipeline for '{Intent}' with N step(s)`.
- One live `/chat` request that should route to the new agent actually does — check the response content, not just a 200 status.
