## Goal Alignment
- Month Objective: Orchestration can scale past 4 agents
- KR(s) this week advances: KR1 — registry + pipeline builder shipped, orchestrator cutover complete
- Regression check: `test_e2e_sweep.py` — 47/50 vs. this script's own 44/50 baseline (Week 8/16)

---

## Day 1 — Close the Week 1 regression gap: paced e2e rerun ✅

### What Was Built

- Added `pace: bool = True` param to `send_chat()` — 1.5s delay by default on every call, per Week 1's flagged fix
- Extracted TC41 (35-request rate-limit burst) into its own `test_rate_limit_burst()`, run last and unpaced, so it doesn't consume Voyage's rate-limit budget for the other 49 cases
- Added `RUN_ID` (uuid, 8 hex chars) and a `scoped()` helper — every session ID in the sweep is now unique per run

### What Was Found (not in the original Day 1 scope, but blocking it)

The first paced run surfaced two real, previously-invisible bugs rather than confirming a clean baseline:

1. **Redis connection string never parsed correctly.** `AddRedis()` passed the raw `rediss://user:pass@host:port` URI straight into `ConfigurationOptions.Parse()`, which has no native support for that scheme (confirmed: open StackExchange.Redis feature request, #1590). The endpoint silently mis-parsed — visible in logs as a doubled port (`host:6379:6379`) — meaning Redis had likely never connected successfully in this environment, independent of any Phase 2 change. Every session write (`SavePlanAsync`, `AppendMessageAsync`, etc.) was failing via the circuit breaker's fail-fast path, invisibly.

2. **The failure was invisible because `SessionStore`'s 8 Redis methods caught `Exception` with no logging.** `RecordFailure()` fired correctly but nothing captured *why*. Added `ILogger<SessionStore>` and logged the real exception in every catch block — this is what surfaced the actual root cause (`RedisConnectionException: UnableToConnect`) instead of another guess.

**Fix:** manual URI parsing in `AddRedis()` (`ServiceRegistration.cs`) — extracts host/port/password/SSL from the `rediss://` string directly into `ConfigurationOptions` instead of relying on `.Parse()`. Confirmed via `[Startup] Redis pre-warm ping succeeded` post-fix, and TC19–26 (the full meal-plan generate → read → modify → multi-slot chain) now passes end-to-end for the first time.

A second-order bug followed from the first: once Redis genuinely started persisting, the sweep's fixed, non-unique session IDs (`e2e-search`, `e2e-plan`, etc.) began reading back *real* leftover data from previous runs — surfacing as false failures (TC09 showing dietary-profile-influenced results on a session that never set one). Fixed by scoping every session ID to a per-run `RUN_ID`.

### Regression check — paced e2e sweep

| Run | Result | Notes |
|-----|--------|-------|
| Before Redis fix | 46/49 (TC42 missing) | Zero 429s in scored path; TC24/TC26 timed out — traced to Redis, not Voyage |
| After Redis + session-scoping fix | **47/50** | Zero 500s, zero connection errors, zero unexplained failures |

**This script's own baseline (Week 8/16): 44/50.** 47/50 is an improvement, not a regression — confirms the Week 1 IAgent/AgentRegistry/PipelineBuilder changes did not break `/chat` behavior.

**Note on the 93% (56/60) frozen baseline:** that number comes from a separate, larger harness (`eval/datasets/e2e_results.json`, Week 19, 60 cases, run against production with pre-warm + retry logic) — not this file. `test_e2e_sweep.py` and the frozen production harness are different tests with different denominators; comparing this week's 47/50 against 56/60 would not be apples-to-apples. This script's own 44/50 is the correct comparison point, and Week 2's later regression checks (Day 6) should keep using it consistently.

### Remaining failures (3, all pre-existing/tracked)

| TC | Query | Issue | Tracked as |
|----|-------|-------|-----------|
| TC03 | "recipes with garlic and tomatoes" | → ValidateDiet, expected SearchRecipe | I-4 (reopened — was marked resolved Week 16, reproduced today) |
| TC05 | "pasta without dairy" | → ValidateDiet, expected SearchRecipe | I-5 (already tracked, deferred) |
| TC09 | "vegetarian stir fry without nuts" | → ValidateDiet, expected SearchRecipe | I-5 pattern |
| TC47 | "jalapeño & crème fraîche" | → ValidateDiet, expected SearchRecipe | New instance of I-4/I-5 pattern — same rule gap, unicode/special-char query |

TC41 (burst): 5/35 got 429, exactly matching Week 8's original documented behavior — confirms the app's rate limiter itself was never affected by any of the above; the earlier 0/35 result (before the Redis fix) was a side effect of broken Redis calls consuming time inside the request path, not a limiter regression.

### Definition of Done

- [x] One full paced e2e run, zero 429s in the failure trace (scored path)
- [x] Pass rate recorded and compared to the correct baseline (44/50 → 47/50)
- [x] TC41 confirmed isolated and behaving per original documented baseline
- [x] Two infra bugs found via this process fixed, not just papered over (Redis parsing, error logging)

### Tech debt updated

- I-4 reopened (see above)
- M-5, M-6 added and marked resolved
- Inf-7 added (misleading `"ollama"`-keyed CircuitBreaker name), deferred
- T-10 added and marked resolved (session ID scoping)

---

## Day 2 — Pipeline builder verification + fan-out wiring fix ✅

### What Was Found First

Week 1's Day 4 entry describes `PipelineBuilderTests` in detail — four cases including a dedicated fan-out shape test. Checking the actual file tree: **`PipelineBuilderTests.cs` did not exist.** One of the four described tests had been written into `AgentRegistryTests.cs` (`RealWorldShape_SearchThenFanOutDietValidation`); the other three were never written at all. Day 4's DoD was ticked against tests that mostly didn't exist.

That gap had a direct consequence. `ThenForEach` accepted `fanOutFrom` but never set `FanOutItemKey` on the step it constructed — the property existed on `PipelineStep` and nothing populated it. Since `DietValidationPlugin.HandleAsync` reads its target from `SharedData["TargetRecipe"]`, a fan-out step built through the public API had no key to write items under, meaning **the one real multi-agent chain in the codebase could not have worked**. The missing test is exactly why this sat unnoticed for a week.

### What Was Built

- `ThenForEach` — added `string fanOutItemKey` as a required positional parameter and set it on the step. Required, not optional-with-default: a fan-out step whose item key is unset is always a wiring bug, and the target agent would silently find nothing rather than fail. Same fail-fast reasoning as Day 3's duplicate-capability throw.
- `Build()` — added a guard for `FanOutFrom` set without `FanOutItemKey`, ordered before the registry lookup so a step with both problems reports the wiring bug rather than a misleading "unregistered capability". Note: this guard is **unreachable through the public builder API** (`ThenForEach` now always sets both), and only fires if a `PipelineStep` is constructed directly, which its public `init` setters allow. Kept as a safeguard; not unit-testable without `InternalsVisibleTo`.
- `PipelineBuilderTests.cs` — created, 6 tests: empty pipeline throws, unregistered capability throws and names the capability, step order preserved, `Then` defaults (no fan-out, abort on failure), the real `SearchRecipe → ValidateDiet` shape with `"TargetRecipe"` as the item key, and `RunIf` evaluated against both profile-present and profile-absent contexts.
- Moved the fan-out shape test out of `AgentRegistryTests` — it tests the builder, not the registry.

### Blocker cleared: `ClassifiedIntent` / `TraceContext` were never actually blocking

Both were carried as "undefined, blocking the pipeline test files." Both are fully defined. `ClassifiedIntent` has three required members and defaults for everything else; `TraceContext` is a plain record with no Langfuse dependency and ships a `None` no-op static. Test construction needs no fakes, no mocks, no live infra:

```csharp
var ctx = new AgentContext
{
    Classified = new ClassifiedIntent
    {
        Intent = UserIntent.SearchRecipe,
        SearchQuery = "pasta",
        ClassifiedBy = "rules",
    },
    TraceCtx = TraceContext.None,
};
```

The blocker was assumed, never checked.

### Collateral: `IntentRouterTests` broken by Day 1's own fix

`dotnet test` came back 96 total / 19 failed — every failure identical, all from `IntentRouterTests.MakeRouter()`:

Can not instantiate proxy of class: ChefAgent.Shared.SessionStore.
Could not find a constructor that would match given arguments:
IConnectionMultiplexerProxy, CircuitBreaker

Day 1 added `ILogger<SessionStore>` to `SessionStore`'s constructor as part of the silent-catch-block fix. The test file's `Mock<SessionStore>(...)` was never updated. Fixed by passing the logger mock as the third argument.

**Worth naming:** Day 1's headline achievement was making eight silent Redis failures visible — and that same change introduced a break that stayed invisible for a day, because Day 1's DoD checked four boxes and none of them was the unit suite. Adding "unit suite green" to every day's DoD, not just the days that touch `src/`, would have caught it immediately.

### Result

Test summary: total: 96, failed: 0, succeeded: 90, skipped: 6, duration: 10.2s
### Definition of Done

- [x] `ThenForEach` sets `FanOutItemKey`; fan-out chain is structurally capable of working
- [x] `PipelineBuilderTests.cs` exists, 6 tests, proven against the real chain shape
- [x] Full unit suite green

---

## Day 3 — `PipelineRunner`, `PipelineRegistry`, DI wiring ✅

### What Was Found First

`find . -name "PipelineRunner*.cs"` returned nothing. The runner had been carried as "implementation written" with specific implementation details attached (`List<(object Item, AgentResult Result)>` fan-out results, per-item `ContinueOnFailure`, `SharedData` mutated in place with per-item copies). **It did not exist.** Fourth item this phase documented as done that wasn't.

Net effect was favorable: the runner got written against a builder contract proven by six passing tests, rather than against an assumed one.

### Decisions closed before writing

| Question | Decision | Why |
|---|---|---|
| Return type | `PipelineResult` (per-step outcomes + final `SharedData`), not `AgentResult` | The translation layer needs to know which recipes validated, which failed, and whether diet ran at all — a single `AgentResult` can't carry that |
| `RunIf` on a fan-out step — once or per item? | Once, against the parent context | The real gate is `MergedProfile is not null`, which doesn't vary per recipe. Per-item would build N contexts to discard them |
| Fan-out source key missing / not enumerable | Step fails; `ContinueOnFailure` decides whether the pipeline dies | A prior step not producing what a later step expects is a wiring bug, not a data condition |
| Fan-out source empty list | Zero invocations, step succeeds | Legitimate data condition — a search returning nothing isn't an error |
| Agent throws | Caught, converted to failed `AgentResult` | Matches existing graceful degradation — a thrown diet validation shouldn't 500 a recipe search |

### The fan-out isolation problem

`ClassifiedIntent` carries four **mutable** setters (`SessionId`, `TargetDay`, `TargetSlot`, `ModifyConstraint`). Giving each fan-out branch its own `SharedData` copy is not sufficient: if all N branches hold a reference to the same `ClassifiedIntent`, any agent writing one of those fields corrupts every sibling. No agent does this today, so it isn't biting — it would bite the first time a fan-out step touched those fields, and would present as data bleeding between recipes.

Runner copies `Classified` per item alongside `SharedData`. **Honest limitation:** `context.Classified with { }` is a shallow copy — enough to isolate the mutable string setters, which is the actual risk. Nested `DietaryProfile` references stay shared. Those are `init`-only so a well-behaved agent can't mutate them, but nothing enforces it. Logged as P2-3.

### `PipelineRegistry` — keyed on `UserIntent`, not `string`

`AgentPipeline.Intent` and `PipelineBuilder.For` changed from `string` to `UserIntent`.

`UserIntent` is a closed set — every intent reaching the orchestrator is one of seven enum members, and the compiler knows all of them. A string-keyed dictionary admits infinitely many keys of which seven are meaningful, so the type was lying about the domain. The enum member names and capability strings *happen* to match today, and nothing enforced that; a Phase 2 intent added without a matching capability name would have broken silently. Lookup site (`ClassifiedIntent.Intent`) is already `UserIntent`, so the conversion disappears entirely.

This closes the intent side only. `AgentCapabilities` stays `const string`, so the capability layer keeps its drift risk — guarded at runtime by `Build()`.

Registry returns `null` from `FindByIntent` for `GetMealPlan`, `GeneralQuestion`, and `Unknown`, exactly as `AgentRegistry.FindByCapability` does for `GetMealPlan`. Caller decides the fallback. Duplicate registration throws, matching `AgentRegistry`.

### `PipelineDefinitions` — definitions live together, not in DI

Four pipelines in one file, following the codebase precedent (`SlotQueries`, `ProteinKeywords`, `CuisineKeywords` are static tables in `MealPlannerPlugin`, not scattered through startup).

**Decision: single-step pipelines are registered.** `ValidateDiet`, `CreateMealPlan`, and `ModifyMealPlan` are one agent call with no chaining, so a "pipeline" adds a layer without adding behavior. Registered anyway so the cutover has exactly one code path — look up, run or fall back — rather than branching between pipeline-backed and directly-dispatched intents. Costs nothing at runtime.

### DI wiring + startup fail-fast

`AddPipelineRegistry()` chained after `AddAgentRegistry()`, since `Build()` validates every referenced capability against a populated registry.

These are lazy singleton factories, so nothing constructs until something resolves `PipelineRegistry` — and until the cutover, nothing does. A clean build would have proven the code compiles, not that the pipelines actually build. Added a forced resolution in `Program.cs` alongside the Redis pre-warm, deliberately **not** wrapped in try/catch: a bad pipeline definition is a wiring bug that should stop the app, consistent with the throw-on-duplicate and throw-on-unregistered decisions.

Startup output, first run:

[AgentRegistry] Registered 'SearchRecipe' -> RecipeAgent
[AgentRegistry] Registered 'SearchByIngredients' -> RecipeAgent
[AgentRegistry] Registered 'ValidateDiet' -> DietAgent
[AgentRegistry] Registered 'CreateMealPlan' -> PlannerAgent
[AgentRegistry] Registered 'ModifyMealPlan' -> PlannerAgent
Registered pipeline for 'SearchRecipe' with 2 step(s)
Registered pipeline for 'ValidateDiet' with 1 step(s)
Registered pipeline for 'CreateMealPlan' with 1 step(s)
Registered pipeline for 'ModifyMealPlan' with 1 step(s)
[Startup] 4 pipeline(s) registered: SearchRecipe, ValidateDiet, CreateMealPlan, ModifyMealPlan

Capability validation ran for real against the live registry.

### Tests

`PipelineRunnerTests.cs` — 13 tests, no infra: sequential order, `OutputsForNextAgent` visible to later steps, `RunIf` skip without invoking the agent, abort vs. continue on failure, agent-throws-becomes-failed-result, and seven fan-out cases (once per item, item written to `FanOutItemKey`, isolated `SharedData`, isolated `ClassifiedIntent`, one-item-fails with and without `ContinueOnFailure`, missing source key, empty list).

One test needed correction: asserting branch isolation via `Distinct()` on `ClassifiedIntent` fails, because records compare by value and three value-identical copies collapse to one. Replaced with a behavioral assertion — a branch writes `TargetSlot`, and the test verifies each branch reads back its own value rather than the last writer's. Tests the consequence rather than the mechanism, so it survives refactors of *how* isolation is achieved.

Test summary: total: 110, failed: 0, succeeded: 104, skipped: 6, duration: 11.0s
### Definition of Done

- [x] `PipelineRunner` executes steps, fan-out, `RunIf`, and both failure modes
- [x] `PipelineRegistry` keyed on `UserIntent`, returns null for non-agent intents
- [x] All four pipelines build and validate against the live registry at startup
- [x] Full unit suite green (110 total, 104 passed, 6 skipped)
- [x] Orchestrator dispatch path still untouched — registry/runner run side-by-side with Phase 1 code

### Tech debt

- **P2-3 added** — fan-out `ClassifiedIntent` copy is shallow; nested `DietaryProfile` references shared across branches
- **P2-4 added** — `shared[step.Capability]` stores `List<(object, AgentResult)>` in an `object`-typed dictionary; translation layer must cast back out. This is the loose-typing pain point Week 1 Day 1 predicted would surface once a real chain existed
- **P2-5 added** — `"TargetRecipe"` now exists as a literal in three places (`PipelineDefinitions`, `DietValidationPlugin`, `PipelineRunnerTests`). Should be a single shared const before Nutrition and Shopping List add more fan-out keys
- **T-11 added and marked resolved** — `IntentRouterTests` `SessionStore` constructor arity

### Carried into Day 4

- `OrchestratorResponse` translation layer — `PipelineResult` → `List<ValidatedRecipe>` / `DietaryCheck` / `MealPlan` / `ResponseConfidence`. Must reproduce the existing confidence derivation, not invent a new one
- Orchestrator cutover — gated on the above
- IntentRouter intent discovery from the registry (roadmap Week 1–2 scope, not yet started)
- `/recipes/search-validated` consolidation onto the runner

---

## Day 4 — Tracing preservation + `OrchestratorResponse` translation layer ✅

### Three blockers found before any code

Reading `AgentOrchestrator` against `PipelineResult` surfaced three gaps, two of which would have shipped silently at cutover.

**1. The `RunIf` gate didn't match the orchestrator's actual gate.**

`PipelineDefinitions` had:
```csharp
runIf: ctx => ctx.Classified.MergedProfile is not null
```

`HandleSearchRecipeAsync` has:
```csharp
classified.MergedProfile is null
|| (classified.MergedProfile.Allergies.Count == 0
    && classified.MergedProfile.Restrictions.Count == 0)
```

`LoadAndMergeProfileAsync` returns a non-null profile whenever *either* the stored or request side exists, so an empty-but-present profile is reachable. Today it skips validation; the pipeline would have run it — five pointless diet calls, `Confidence` dropping High → Medium, and a different message template. Fixed, and extracted to `PipelineDefinitions.HasActionableProfile` since the same predicate was already written longhand twice in `AgentOrchestrator`.

**2. Tracing would have been silently gutted.**

`PipelineRunner` created no spans at all. Checking what the plugins own internally rather than assuming:

| Plugin | Owns internally | Depends on orchestrator wrapper |
|---|---|---|
| `RecipeSearchPlugin` | `embed.cache_hit`, `embed.provider`, reranker spans | `recipe_agent.search` |
| `DietValidationPlugin` | `diet.llm_validation` only | `diet_agent.validate` — **the entire rules-only path** |
| `MealPlannerPlugin` | nothing; `GeneratePlanAsync`/`ModifyPlanAsync` don't accept a `TraceContext` | all 14 internal calls already untraced |

The `DietValidationPlugin` row is the serious one: by the project's own "rules first, LLM fallback" design, the rules path is the dominant path, and it is visible in Langfuse *solely* because the orchestrator wraps each call. Cutting over as-is would have made the majority of diet validations disappear from tracing entirely.

**3. `ValidateDiet`'s pipeline shape was wrong.**

`HandleValidateDietAsync` is not a bare validation call — it searches first (`maxResults: 1`), takes the top result, then validates it. The registered pipeline was a single `ValidateDiet` step, so `DietValidationPlugin.HandleAsync` would have found no `TargetRecipe` in `SharedData` and **failed every ValidateDiet request**. Rewritten as search → fan-out validate, reusing the item-key wiring rather than duplicating it.

Fifth instance this phase where reading the actual code contradicted what was assumed about it.

### Decision: runner-owned span lifecycle, deliberately transitional

Three options were weighed:

| Option | Trade-off |
|---|---|
| Runner opens spans, agents supply payload via a new `AgentResult.TraceOutput` field | Fastest, but puts an observability concern in a business-logic contract, `object`-typed, with no consumer checking it — reproducing the exact shape of debt already logged as P2-4 |
| Agent-owned spans now | Correct end state, but means adding span lifecycle to `ValidateRecipeAsync` and threading `TraceContext` through two `MealPlannerPlugin` signatures — Phase 1 surface area, changed during a cutover, so a Day 6 regression failure would have two candidate causes |
| Runner-owned lifecycle, no payload | Chosen. Nothing to un-build later, no new fields, plugin internals untouched |

The deciding argument against the `TraceOutput` bridge: "temporary" fields tend to outlive the condition that justified them. Accepting a few days of thinner traces on a non-production branch is the cheaper trade.

**Built:**
- `PipelineStep.SpanName` (optional, defaults to `pipeline.{Capability}`), plumbed through `Then` and `ThenForEach`. Optional rather than required — unlike `FanOutItemKey`, a missing span name degrades cosmetically instead of breaking the chain. Only mandate what breaks correctness.
- `PipelineDefinitions` supplies the Phase 1 names (`recipe_agent.search`, `diet_agent.validate`) so traces stay comparable across the cutover and existing Langfuse views keep working. Planner pipelines take the default — there was no Phase 1 name to preserve there, so `pipeline.CreateMealPlan` is new information rather than a rename.
- `PipelineRunner` takes `Tracing`, opens a `pipeline.{Intent}` boundary span, one span per step, one per fan-out item, and replaces `TraceCtx` on every context handed to an agent so plugin-internal spans nest correctly instead of flattening to the trace root.

**Known limitation:** the runner can count fan-out items but can't name them — Phase 1 passed `recipeTitle`, the runner can only pass an index. That is the concrete cost of runner-owned tracing and exactly what agent-owned spans will fix.

### Tracing is not unit-testable at this layer

A test asserting that steps receive a span context rather than the root failed:

Assert.NotEqual() Failure: Values are equal
Expected: Not TraceContext { TraceId = , SpanId = , IsNone = True }
Actual: TraceContext { TraceId = , SpanId = , IsNone = True }

With `Enabled = false`, `Tracing.StartSpan` short-circuits and returns `TraceContext.None`, so the assertion cannot distinguish "runner forgot to replace `TraceCtx`" from "tracer is off." Covering it properly needs either a fake `Tracing` (concrete class, no interface, non-virtual — Moq can't intercept) or a live tracer against a stub HTTP handler. Both are more machinery than the guarantee is worth right now.

Test deleted rather than kept passing for the wrong reason. **Span nesting is covered by manual verification in a real Langfuse trace after cutover, not by unit tests** — recorded here so it isn't mistaken for coverage that exists.

### Translation layer

`MapSearchRecipeResult` and `MapValidateDietResult` added to `AgentOrchestrator` — they need the private `BuildSearchMessage` / `BuildMetadata` / `ErrorResponse` helpers, so they live there rather than in `Shared`.

Behavior parity is the bar, not improvement. Every mapping reproduces current output exactly:

| Orchestrator concept | Derived from |
|---|---|
| Recipe-search failure | `AbortedAt == SearchRecipe` → existing `ErrorResponse` |
| Zero recipes | search succeeded, list empty → distinct "couldn't find any" message, `High` |
| Diet skipped | `StepResult.Skipped` → unbadged recipes, `High` |
| `ValidatedRecipe` | each fan-out tuple → `Recipe = (RecipeDocument)Item`, `Dietary = Result.Data as DietaryValidation` |
| `dietaryUnavailable` | any fan-out item failed → recipe returned unbadged, `Low` |
| `compatibleCount` | count where `Dietary?.IsCompatible == true` |
| Sort order | compatible first, applied in the mapper — the runner does not sort |

Two fidelity details worth recording:

- `dietaryUnavailable` maps to a *thrown* validation, not an LLM failure. `ValidateRecipeAsync` already catches its own LLM errors and returns compatible-with-warning, so `HandleAsync` only fails on genuine exceptions — same semantics as today's per-recipe catch.
- `BuildSearchMessage` receives the pre-validation `recipes` list, so `count` stays the search count even if fan-out returned fewer. Matches current behavior.
- `MapValidateDietResult`'s "no recipes found" branch sets no explicit `Confidence`, relying on the `High` default. That quirk is copied verbatim from today's code rather than fixed, so Day 6 stays comparable.

`Message`, `Metadata`, `AppendConfidenceDisclaimer`, history append, profile merge, and reference resolution all stay in `RouteAsync`, outside the pipeline. Message construction is presentation, not agent work.

### Also found, not fixed

- `RecipeSearchPlugin.HandleAsync` reads `maxResults` from `SharedData` with a default of 5. `ValidateDiet` needs 1, so the orchestrator must seed it — per-intent config living in an untyped dictionary.
- `MealPlannerPlugin.HandleAsync` doesn't call `SavePlanAsync`; the orchestrator does it afterward.
- `MealPlannerPlugin.HandleAsync`'s `ModifyMealPlan` path collapses `InvalidOperationException` and `ArgumentException` into one generic failure, losing two distinct user-facing messages.

### Result

Test summary: total: 114, failed: 0, succeeded: 108, skipped: 6

Both mappers compile; nothing calls them yet. Orchestrator dispatch still untouched.

### Definition of Done

- [x] `RunIf` gate matches orchestrator semantics, extracted to one shared predicate
- [x] Phase 1 span names preserved through `PipelineStep.SpanName`
- [x] `PipelineRunner` opens boundary, step, and per-item spans; agents receive span contexts
- [x] `ValidateDiet` pipeline reshaped to search → validate
- [x] Both mappers written against real handler behavior, not assumed behavior
- [x] Full unit suite green

### Tech debt

- **P2-6 added** — runner-owned spans are transitional. Agents should own their own span lifecycle once `ValidateRecipeAsync` opens a `diet_agent.validate` span and `MealPlannerPlugin.GeneratePlanAsync`/`ModifyPlanAsync` accept a `TraceContext`. Removal condition stated so this doesn't become permanent by default.
- **P2-7 added** — `maxResults` as per-intent config in untyped `SharedData`
- **P2-8 added** — `MealPlannerPlugin.HandleAsync` loses `SavePlanAsync` and the two distinct modify-failure messages; blocks planner-intent cutover
- **T-12 added** — runner span nesting has no unit coverage; manual Langfuse verification required post-cutover

### Carried into Day 5

- Cutover: `SearchRecipe` and `ValidateDiet` only. Planner intents stay on direct dispatch until P2-8 is resolved — a registered pipeline doesn't oblige the orchestrator to use it.
- Feature flag (`Pipelines:Enabled`, default false) so Day 6 can A/B both paths on one build rather than comparing against a recorded number
- Pre-pipeline guards stay in the orchestrator: `ValidateDiet`'s no-profile early return, `CreateMealPlan`'s session-id check, `ModifyMealPlan`'s target-day check

