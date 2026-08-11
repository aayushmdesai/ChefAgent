---
name: add-observability-span
description: Add a new Langfuse tracing span around a call in ChefAgent, or trace a request end to end. Use when asked to "add a span for X", "trace this call", or "why isn't X showing up in Langfuse".
---

# Add an observability span

`Tracing.cs` (`src/shared/Observability/`) is, by its own doc-comment, "the ONLY class in ChefAgent that knows Langfuse exists." Never call Langfuse directly from anywhere else, and never add a new tracing abstraction — extend this one.

## Phase 1 pattern (direct agent code, both branches use this outside pipelines)

```csharp
var ctx = _tracing.StartSpan(parentCtx, "component.action");
try
{
    // work
    _tracing.EndSpan(ctx, output: someResult, statusMessage: "ok");
}
catch
{
    _tracing.EndSpan(ctx, statusMessage: "error");
    throw; // or handle, per the surrounding resilience pattern
}
```
A `TraceContext` (plain record, `TraceId`/`SpanId`, no Langfuse dependency) is threaded through method parameters, not injected as a service — this is deliberate: if `Tracing` throws, `TraceContext`-consuming business logic can't be affected, because it's just a value being passed around. `StartSpan`/`EndSpan` never throw and never block the request thread (bounded `Channel` + background worker); check `_tracing.StartSpan(...)` call sites in `AgentOrchestrator.cs`/`IntentRouter.cs`/`DietValidationPlugin.cs` for the exact shape before adding a new one.

## Phase 2 pipeline pattern (`phase-2` only)

Span lifecycle for pipeline-dispatched calls is currently **runner-owned and explicitly transitional** — `PipelineRunner` opens a `pipeline.{Intent}` boundary span, one span per step (name from `PipelineStep.SpanName`, defaulting to `pipeline.{Capability}` if unset — set it explicitly to `recipe_agent.search`/`diet_agent.validate`-style names to keep Phase 1 trace naming comparable across the cutover), and one per fan-out item. **Known limitation**: the runner can count fan-out items but can't name them meaningfully (index only, not e.g. a recipe title) — that's the documented cost of runner-owned spans versus agent-owned ones, not a bug to silently fix. Don't add a payload-carrying field to bridge this (`AgentResult.TraceOutput` exists in the type but is unused everywhere — that path was explicitly considered and rejected).

## Verification

Unit tests cannot verify span nesting — with `Langfuse:Enabled = false` (or in a test double), every span call short-circuits to `TraceContext.None`, so an assertion can't distinguish "the code forgot to pass the context" from "tracing is off." The only real verification is a live trace: send a real request, open the Langfuse UI (self-hosted `:3100` locally, or Langfuse Cloud in prod), and confirm the new span appears nested under its actual parent — not flattened to the trace root, which is exactly the failure mode that happens if a `TraceContext` doesn't get threaded through correctly.
