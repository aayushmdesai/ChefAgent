---
name: add-pipeline-definition
description: Add a new agent pipeline (a chained sequence of agent calls) to ChefAgent's Phase 2 pipeline architecture. Use when asked to "add a pipeline for [intent]", "chain agent X into Y", or "make [intent] call multiple agents".
---

# Add a pipeline definition

**Phase 2 only.** Confirm `phase-2` is actually checked out before following this — `PipelineBuilder`/`PipelineDefinitions`/`PipelineRegistry` don't exist on `main`:

```bash
test -f src/shared/PipelineBuilder.cs || echo "Not on phase-2 — this skill doesn't apply here"
```

## Steps

1. Open `src/shared/PipelineDefinitions.cs`. Add a private static method following the existing shape:
   ```csharp
   private static AgentPipeline MyIntent(AgentRegistry registry) =>
       PipelineBuilder
           .For(UserIntent.MyIntent, registry)
           .Then(AgentCapabilities.SomeCapability, spanName: "component.action")
           .Build();
   ```
2. For a chained (fan-out) step — one agent's output feeding N calls to the next agent — use `ThenForEach`, not `Then`:
   ```csharp
   .ThenForEach(
       AgentCapabilities.NextCapability,
       fanOutFrom: AgentCapabilities.PreviousCapability,   // reads a list out of SharedData at this key
       fanOutItemKey: SomeSharedConstKey,                   // REQUIRED — the key the next agent's HandleAsync reads its single item from
       runIf: SomePredicate,                                 // optional — step skipped if this returns false
       continueOnFailure: true,                              // optional — one failed item doesn't abort the rest
       spanName: "component.action"
   )
   ```
   `fanOutItemKey` is a required parameter, not optional-with-a-default — a fan-out step with no item key is always a wiring bug (the target agent's `HandleAsync` would find nothing in `SharedData` and silently do nothing useful), and `Build()` throws if `FanOutFrom` is set without it.
3. If the target agent needs a shared constant for its `SharedData` key (like `PipelineDefinitions.TargetRecipeKey` for diet validation), define it once in `PipelineDefinitions` and reference it from both the pipeline definition and the agent's `HandleAsync` — don't let the same string literal drift across multiple files (this is exactly what tech-debt `P2-5` flags as already having happened once).
4. Register the new pipeline from `BuildAll()` — even a single-step pipeline with no chaining is registered by convention here, so the orchestrator has exactly one code path (look up, run, or fall back) rather than branching between pipeline-backed and directly-dispatched intents.
5. If this pipeline should actually be used (not just registered), check `AgentOrchestrator`'s `TryRunPipelineAsync`/`DispatchAsync` — a registered pipeline doesn't by itself change dispatch behavior. Cutover for a given intent is separate work, gated behind `Pipelines:Enabled` (`appsettings.json`, defaults `false`).

## Verification

- Add a `PipelineBuilderTests`-style unit test proving the shape (step order, fan-out key correctness) **before** touching the orchestrator — Phase 2's own history includes a fan-out wiring bug (`ThenForEach` silently not setting the item key) that sat undetected for a week specifically because the test that would have caught it didn't exist. Don't repeat that.
- Startup log: `Registered pipeline for '{Intent}' with N step(s)`, and the pipeline count in `[Startup] N pipeline(s) registered: {Intents}` increments.
- If cutover is enabled for this intent, verify live with `Pipelines__Enabled=true` and confirm the response matches the pre-pipeline (direct-dispatch) response for the same query — behavior parity is the bar for a cutover, not improvement.
