# ChefAgent — Tech Debt Backlog

**Last updated:** Phase 2 Week 2 Day 6 (2026-08-16) — added the Phase 2 pipeline-architecture items (`P2-1`…`P2-10`), `T-11`, `T-12`, `Inf-8`, `Inf-9`, which prior progress docs referenced as "added" but were never actually written here; also corrected the summary table, which had not counted the Week 2 Day 1 additions.
**Format:** Each item has a source (where it was discovered), severity, and target milestone.

Items are ordered within each section by priority (High → Low).

✅ = resolved | 🔄 = partially resolved | ⏳ = deferred

---

## Intent Classification

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| I-1 | "change Friday to X" not classified as ModifyMealPlan — rules only know "swap" | Week 8 Day 2, TC23 | Low | ✅ Week 16 Day 3 |
| I-2 | "plan breakfast lunch and dinner" not classified as CreateMealPlan — rules expect "plan my dinners" | Week 8 Day 2, TC24 | Low | ✅ Week 16 Day 3 |
| I-3 | "make me a new plan" not classified as CreateMealPlan | Week 8 Day 2, TC26 | Low | ✅ Week 16 Day 3 |
| I-4 | "recipes with garlic and tomatoes" → ValidateDiet false positive — ingredient names trigger dietary rules | Week 8 Day 2, TC03 | Low | 🔄 Reopened Phase 2 Week 2 Day 1 — reproduced in test_e2e_sweep.py rerun. **Day 6 A/B:** TC03 fails on *both* flag states (Phase 1 and pipeline) → pre-existing, not a cutover effect. Durable misroute set is 3 (TC03/05/09); TC47 ("jalapeño & crème fraîche") is nondeterministic and passed both runs, so it is not a reliable fourth. Still open. |
| I-5 | "pasta without dairy" classified as ValidateDiet, not SearchRecipe — "without X" is ambiguous | Week 8 Day 2, TC05 | Low | ⏳ Accepted — ambiguous intent, deferred |
| I-6 | "hello" / greetings classified as SearchRecipe — no greeting/small-talk signals in IntentRouter | Week 8 Day 1 TC35, Day 2 TC50 | Low | ⏳ Deferred — Month 5 |
| I-7 | "whats on monday?" → GeneralQuestion, not GetMealPlan — day-specific plan queries not handled | Week 8 Day 2, TC25 | Low | ✅ Week 16 Day 3 — "what's on {day}" signals added |
| I-8 | Question-form ValidateDiet not recognized — "is X vegan?", "can Z eat X?", "is X safe for Y?" all classified as SearchRecipe | Week 11 E2E eval, cases 25/28/29/30/55/56 | Medium | ✅ Week 16 Day 3 — DietQuestionRegex + CanEatRegex |
| I-9 | "plan dinners for the week, I'm dairy-free" → SearchRecipe — CreateMealPlan rules don't handle dietary constraint appended to plan phrase | Week 11 E2E eval, case 31 | Medium | ✅ Week 16 Day 3 — "plan dinners for the week" added |
| I-10 | "remind me what I'm having Thursday" → SearchRecipe — contraction normalization expands i'm→i am before signal matching | Week 16 Day 3, e2e-042 | Low | ✅ Week 16 Day 3 — normalized form added to GetMealPlanSignals |

---

## Search & Retrieval

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| S-1 | Semantic negation: "dairy-free" matches literal text, not semantic category — milk/cheese still appear in results | Week 2 known limitation | Medium | ✅ Month 3 — FreePattern expands X-free to full ingredient set via DietaryRules |
| S-2 | LLM reranker (`RecipeReranker.cs`) built but opt-in (`rerank: false`) — too slow on CPU for interactive use | Week 2 | Medium | ⏳ Deferred — hardware constraint |
| S-3 | Query expansion (`QueryPreprocessor.cs`) built but opt-in (`expand: false`) — same hardware constraint | Week 2 | Low | ⏳ Deferred — hardware constraint |
| S-4 | No embedding cache — repeated queries re-embed on every request | Week 8 Day 3 | Medium | ✅ Month 3 — in-memory ConcurrentDictionary cache in RecipeSearchPlugin |
| S-5 | Cold start penalty — first embedding call after Ollama startup takes 15-20s | Week 8 Day 3 | Medium | ✅ Resolved via cloud migration — Voyage API ~200ms cold start |
| S-6 | negation/x_free RAGAS regression vs Nomic baseline — voyage-4-lite encodes exclusion queries differently | Week 16 Day 2 | Low | ⏳ Embedding model limitation — post-retrieval filtering correct but candidates weaker. Fix: better model or reranker. Deferred. |
| S-7 | Paleo queries return 0 recipes — DietAgent correctly flags all results, but corpus has no paleo-tagged recipes | Week 16 Day 4, e2e-016 | Low | ⏳ Corpus gap — deferred |

---

## Embedding Provider

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| E-1 | Nomic Atlas API free tier (10M tokens) exhausted — production down | Week 15/16 | High | ✅ Week 16 Day 1 — migrated to Voyage AI (voyage-4-lite, 200M free tokens) |
| E-2 | Meal plan generation fires 21 embedding calls (7 days × 3 slots) → saturates Voyage 3 RPM free tier | Week 16 Day 4 | Medium | ⏳ Accepted — retry logic in place. Fix: paid Voyage tier or throttling (7+ min plan time). Deferred. |
| E-3 | In-memory embedding cache resets on Railway redeploy — no cross-deploy or cross-instance persistence | Week 16 Day 4 | Low | ⏳ Redis-backed cache would help repeat user searches but not meal plan generation. Deferred. |

---

## Memory & Session

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| M-1 | Reference resolution falls through silently — "the first one" with no history searches literally | Week 8 Day 1 TC21 | Low | ⏳ Deferred |
| M-2 | Profile entity extraction fires on every first message of a new session (~12s) — result not cached | Week 8 Day 3 | High | ✅ Month 3 — extraction result cached in Redis per session |
| M-3 | Reference resolution uses LLM for ordinal references — rules can handle 90% of cases | Week 8 Day 3 | Low | ⏳ Deferred |
| M-4 | Redis connection timeout still 8-15s under failure — no Redis circuit breaker | Week 8 Day 1 | Medium | ⏳ Deferred |
| M-5 | Redis connection string (`rediss://user:pass@host:port` URI format) silently mis-parsed by `ConfigurationOptions.Parse` — StackExchange.Redis has no native URI scheme support. Endpoint was garbled (doubled port in logs), meaning Redis had likely never connected successfully in local dev with this format. | Phase 2 Week 2 Day 1 | High | ✅ Fixed — manual URI parsing added in `AddRedis()` |
| M-6 | `SessionStore`'s 8 Redis operations caught `Exception` with no logging — every failure (including M-5) was invisible; `RecordFailure()` fired with no diagnostic trail | Phase 2 Week 2 Day 1 | Medium | ✅ Fixed — `ILogger<SessionStore>` added, all catch blocks now log `ex` with session ID |

---

## Planning

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| P-1 | Plan generation is sequential — 7 embedding calls run one after another | Week 8 Day 3 | Medium | ⏳ Deferred — parallelizing would worsen Voyage 429 cascade |
| P-2 | Multi-slot planning (21 calls) not fully tested at scale | Week 8 Day 2 | Low | ⏳ Deferred |
| P-3 | No variety enforcement across plan days — same recipe could appear multiple times | Week 4 design note | Low | ✅ Month 3 — avoidProteinRepeat + avoidCuisineRepeat constraints added |

---

## Dietary Validation

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| D-1 | Kosher validation incomplete — meat/dairy separation not handled | DietaryRules.cs FIXME | Low | ⏳ Deferred — needs LLM fallback |
| D-2 | Diet validation asks for clarification when query is vague | Week 8 Day 2, TC11 | Low | ⏳ Deferred |

---

## Guardrails

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| G-1 | No global rate limiter tested in e2e | Week 8 Day 2 | Low | ⏳ Deferred |
| G-2 | OutputGuard confidence signaling appends note to message — awkward UX | Week 7 | Low | ⏳ Deferred |
| G-3 | Rate limit test (e2e-053) uses wrong trigger — repeated text in single message doesn't trigger per-session counter | Week 11 E2E eval, case 53 | Low | ⏳ Deferred — needs setup_messages with 4+ identical requests |

---

## Infrastructure & Performance

| #     | Issue | Source | Priority | Status |
| ----- | ----- | ------ | -------- | ------ |
| Inf-1 | Microsoft.SemanticKernel 1.30.0 → 1.77.0 available — major API changes risk | Week 8 Day 4 | Low | ⏳ Deferred |
| Inf-2 | GeneralQuestion LLM inference: p50 6.97s, p95 21.64s on CPU | Week 8 Day 3 | Medium | ✅ Resolved — Groq cloud (Llama 3.3 70B) at ~650ms |
| Inf-3 | No distributed tracing / observability | Month 3 roadmap | High | ✅ Month 3 — Langfuse Cloud, 14 span types, fire-and-forget Channel flush |
| Inf-4 | Docker compose only — no cloud deploy | Month 3 roadmap | High | ✅ Month 3 — Railway (API) + Vercel (frontend) |
| Inf-5 | RAGAS evaluation pipeline not yet integrated | Month 3 roadmap | High | ✅ Month 3 — custom sequential Ollama-based scorer, compare_experiments.py |
| Inf-6 | Langfuse observability not yet integrated | Month 3 roadmap | High | ✅ Month 3 |
| Inf-7 | `CircuitBreaker` keyed `"ollama"` in DI actually wraps Groq/`ILlmProvider` calls — misleading name left over from provider swap | Phase 2 Week 2 Day 1 | Low | ⏳ Deferred — rename to `"llm"` when convenient, touches `ServiceRegistration.cs` + 4 call sites |
| Inf-8 | Qdrant Cloud free tier reclaims idle clusters and drops their collections — the 52,155-recipe corpus was lost mid-verification on Day 5 with no snapshot. Graceful degradation makes this look like an ordinary "couldn't search" hiccup (check the exception for `RpcException Unavailable`/`NotFound`, not the user-facing message) | Phase 2 Week 2 Day 5 | High | ⏳ Accepted risk — no backup exists today. Fix: paid tier, or an automated snapshot + a `load_qdrant.py` reload that validates dimensionality (currently loads the wrong-dim file silently, failing only at query time) |
| Inf-9 | `ServiceRegistration.AddChefAgentServices` calls `AddAgentRegistry()` (lines 50, 52) and `AddApiServices()` (lines 51, 54) twice each — duplicate singleton registrations; DI resolves the last so it's harmless today, but it's confusing and one call each is dead | Phase 2 Week 2 Day 6 | Low | ⏳ Deferred — delete the duplicate `AddAgentRegistry()`/`AddApiServices()` lines, keeping `AddPipelineRegistry()` after a single `AddAgentRegistry()` |

---

## Testing Gaps

| #   | Issue | Source | Priority | Status |
| --- | ----- | ------ | -------- | ------ |
| T-1 | No unit tests for IntentRouter (rules path) | Week 8 Day 5 plan | High | ✅ Month 3 |
| T-2 | No unit tests for DietaryRules engine | Week 8 Day 5 plan | High | ✅ Month 3 |
| T-3 | No unit tests for InputGuard.Validate | Week 8 Day 5 plan | High | ✅ Month 3 |
| T-4 | No unit tests for CircuitBreaker state transitions | Week 8 Day 5 plan | High | ✅ Month 3 |
| T-5 | Profiling script reuses same session+query — repeat detector skews /chat measurements | Week 8 Day 3 | Low | ⏳ Deferred |
| T-6 | No concurrent request testing — all tests are single-threaded | Week 8 Day 1 | Low | ⏳ Deferred |
| T-7 | No partial failure testing (Ollama hanging vs down) | Week 8 Day 1 | Low | ⏳ Deferred |
| T-8 | E2E eval case 53 (rate limit) uses wrong trigger pattern | Week 11 E2E eval | Low | ⏳ Deferred |
| T-9 | Implicit dietary LLM extraction is non-deterministic — e2e-046 flips between intents | Week 11 E2E eval, case 46 | Low | ✅ Week 16 Day 3 — removed broad "can i eat" signal, CanEatRegex more precise |
| T-10 | `test_e2e_sweep.py` reused fixed session IDs (`e2e-search`, `e2e-plan`, etc.) across every run — once Redis persistence actually worked (see M-5), stale profile/plan data from prior runs leaked into fresh runs, producing false failures | Phase 2 Week 2 Day 1 | Medium | ✅ Fixed — `RUN_ID` (uuid) appended to every session ID via `scoped()` helper |
| T-11 | `IntentRouterTests.MakeRouter()` `Mock<SessionStore>` constructor arity broke when Day 1 added `ILogger<SessionStore>` to `SessionStore` — 19 tests failed identically until the helper was updated | Phase 2 Week 2 Day 3 | Low | ✅ Fixed — logger mock passed as 3rd arg. (Day 6 grew the same constructor again with `AgentRegistry`; helper updated in step — see P2-9 lesson.) |
| T-12 | `PipelineRunner` span nesting has no unit coverage — `Tracing` is a concrete non-virtual class Moq can't intercept, and with `Enabled=false` every span call is a no-op, so a test can't distinguish "runner replaced `TraceCtx`" from "tracer off" | Phase 2 Week 2 Day 4 | Low | 🔄 Manual verification done Day 6 (Langfuse public API: `pipeline.SearchRecipe → recipe_agent.search → embed.provider` nests correctly, not flat at root). Unit coverage still absent — accepted; would need a fake `Tracing` or a live tracer against a stub handler. |

---

## Dataset

| #    | Issue | Source | Priority | Status |
| ---- | ----- | ------ | -------- | ------ |
| DS-1 | `corbt/all-recipes` may need replacement with RecipeNLG for richer metadata | Week 1 decision | Low | ⏳ Deferred |
| DS-2 | Only 10K recipes loaded — full dataset would improve retrieval quality | Week 1 | Low | ✅ Week 15/16 — expanded to 52,155 recipes (Western + Indian) |

---

## Pipeline Architecture (Phase 2)

Capability-based agent registry + declarative pipelines replacing hardcoded per-intent dispatch. These were referenced as "added" across Phase 2 progress docs but never actually recorded here until Week 2 Day 6.

| #    | Issue | Source | Priority | Status |
| ---- | ----- | ------ | -------- | ------ |
| P2-1 | `RecipeSearchPlugin.HandleAsync` is not unit-tested — depends on concrete `QdrantClient`/plugin types Moq can't intercept; a skip-stub holds the slot in the suite | Phase 2 Week 1 Day 2 | Low | ⏳ Deferred — needs live infra (integration-style) or an `IAgent`-typed dependency refactor |
| P2-2 | `MealPlannerPlugin.HandleAsync` is not unit-tested — same concrete-type limitation | Phase 2 Week 1 Day 2 | Low | ⏳ Deferred — same fix as P2-1 |
| P2-3 | Fan-out copies `ClassifiedIntent` shallowly (`with { }`) — isolates the mutable string setters (the real risk) but nested `DietaryProfile` references stay shared across branches. `init`-only, so safe today; would bite the first fan-out step that mutates profile | Phase 2 Week 2 Day 3 | Low | ⏳ Deferred |
| P2-4 | `PipelineRunner` stores `List<(object Item, AgentResult Result)>` under an `object`-typed `SharedData` key — the translation layer must cast it back out. The loose-typing pain point Week 1 predicted once a real chain existed | Phase 2 Week 2 Day 3 | Low | ⏳ Deferred |
| P2-5 | `"TargetRecipe"` fan-out key is a bare string literal in **5** files (`PipelineDefinitions`, `DietValidationPlugin`, + 3 test files) — should be one shared const before more fan-out keys (Nutrition, Shopping List) are added | Phase 2 Week 2 Day 3 | Low | ⏳ Deferred — spread from 3→5 files by Day 6 |
| P2-6 | Runner-owned span lifecycle is transitional — agents should own their spans once `ValidateRecipeAsync` opens `diet_agent.validate` and `MealPlannerPlugin.Generate/ModifyPlanAsync` accept a `TraceContext`. Cost: runner can pass only a fan-out index, not `recipeTitle` | Phase 2 Week 2 Day 4 | Low | ⏳ Deferred — nesting verified correct (T-12), so no urgency; removal condition stated |
| P2-7 | `maxResults` is per-intent config passed through the untyped `SharedData` dictionary (`ValidateDiet` seeds `=1`; `RecipeSearchPlugin` defaults `5`) — typed config would be cleaner | Phase 2 Week 2 Day 4 | Low | ⏳ Deferred |
| P2-8 | `MealPlannerPlugin.HandleAsync` (L151–190) doesn't call `SavePlanAsync` (the orchestrator saves afterward; `SavePlanAsync` lives only in `ModifyPlanAsync`, L322), and its generic `catch (Exception)` collapses `ModifyPlanAsync`'s distinct `InvalidOperationException`/`ArgumentException` into one message — losing two user-facing messages. **Blocks routing `CreateMealPlan`/`ModifyMealPlan` through pipelines** | Phase 2 Week 2 Day 4 | Medium | ⏳ Open — gates planner-intent cutover; verified against code Day 6 |
| P2-9 | `HasActionableProfile` logic is duplicated — `PipelineDefinitions` (takes `AgentContext`) and `AgentOrchestrator` (takes `ClassifiedIntent`): same predicate, two shapes. The Day 4 extraction was meant to prevent exactly this third copy | Phase 2 Week 2 Day 5 | Low | ⏳ Deferred |
| P2-10 | `Pipelines:Enabled` flag + the Phase 1 `switch` are two live dispatch paths (drift risk) — delete the switch once planner intents cut over (P2-8). Day 6 A/B cleared the regression half of the trigger; flag stays `false` in committed config until then | Phase 2 Week 2 Day 5 | Medium | ⏳ Deferred — removal gated on P2-8 |

---

## Summary

| Category | Total | ✅ Resolved | ⏳ Deferred / open |
|---|---|---|---|
| Intent Classification | 10 | 7 | 3 |
| Search & Retrieval | 7 | 3 | 4 |
| Embedding Provider | 3 | 1 | 2 |
| Memory & Session | 6 | 3 | 3 |
| Planning | 3 | 1 | 2 |
| Dietary Validation | 2 | 0 | 2 |
| Guardrails | 3 | 0 | 3 |
| Infrastructure | 9 | 5 | 4 |
| Testing Gaps | 12 | 7 | 5 |
| Dataset | 2 | 1 | 1 |
| Pipeline Architecture (Phase 2) | 10 | 0 | 10 |
| **Total** | **67** | **28** | **39** |

**28 items resolved across Months 1-4 and Phase 2 Week 2.** All High priority items from Month 3 are complete; the one new High-priority item is Inf-8 (no Qdrant corpus backup).

The 10 Phase 2 items are all Low/Medium and mostly intended debt from the transitional cutover — two carry explicit removal triggers (P2-6 agent-owned tracing, P2-10 flag removal), and P2-8 is the one that actively gates the planner-intent cutover. `🔄 T-12` counts as not-resolved here (manual verification done, unit coverage still absent).

_Note: the previous summary (49 total / 23 resolved) never counted the Week 2 Day 1 additions (M-5, M-6, Inf-7, T-10) or corrected the Search & Retrieval count (S-5 was resolved but tallied as deferred). The counts above are recomputed directly from the section tables._