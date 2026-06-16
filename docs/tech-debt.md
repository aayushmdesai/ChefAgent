# ChefAgent — Tech Debt Backlog

**Last updated:** Week 16 (Month 4 complete)
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
| I-4 | "recipes with garlic and tomatoes" → ValidateDiet false positive — ingredient names trigger dietary rules | Week 8 Day 2, TC03 | Low | ✅ Already passing Week 16 audit |
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

---

## Dataset

| #    | Issue | Source | Priority | Status |
| ---- | ----- | ------ | -------- | ------ |
| DS-1 | `corbt/all-recipes` may need replacement with RecipeNLG for richer metadata | Week 1 decision | Low | ⏳ Deferred |
| DS-2 | Only 10K recipes loaded — full dataset would improve retrieval quality | Week 1 | Low | ✅ Week 15/16 — expanded to 52,155 recipes (Western + Indian) |

---

## Summary

| Category | Total | ✅ Resolved | ⏳ Deferred |
|---|---|---|---|
| Intent Classification | 10 | 7 | 3 |
| Search & Retrieval | 7 | 2 | 5 |
| Embedding Provider | 3 | 1 | 2 |
| Memory & Session | 4 | 1 | 3 |
| Planning | 3 | 1 | 2 |
| Dietary Validation | 2 | 0 | 2 |
| Guardrails | 3 | 0 | 3 |
| Infrastructure | 6 | 5 | 1 |
| Testing Gaps | 9 | 5 | 4 |
| Dataset | 2 | 1 | 1 |
| **Total** | **49** | **23** | **26** |

**23 items resolved across Months 1-4.** All High priority items from Month 3 are complete.
Remaining 26 items are Low/Medium priority — real improvements but not blocking Month 5 portfolio goals.