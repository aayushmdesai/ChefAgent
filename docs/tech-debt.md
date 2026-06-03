# ChefAgent — Tech Debt Backlog

**Last updated:** Week 8, Day 4
**Format:** Each item has a source (where it was discovered), severity, and target milestone.

Items are ordered within each section by priority (High → Low).

---

## Intent Classification

| #   | Issue                                                                                                     | Source                        | Priority | Target  |
| --- | --------------------------------------------------------------------------------------------------------- | ----------------------------- | -------- | ------- |
| I-1 | "change Friday to X" not classified as ModifyMealPlan — rules only know "swap"                            | Week 8 Day 2, TC23            | Low      | Month 3 |
| I-2 | "plan breakfast lunch and dinner" not classified as CreateMealPlan — rules expect "plan my dinners"       | Week 8 Day 2, TC24            | Low      | Month 3 |
| I-3 | "make me a new plan" not classified as CreateMealPlan                                                     | Week 8 Day 2, TC26            | Low      | Month 3 |
| I-4 | "recipes with garlic and tomatoes" → ValidateDiet false positive — ingredient names trigger dietary rules | Week 8 Day 2, TC03            | Low      | Month 3 |
| I-5 | "pasta without dairy" classified as ValidateDiet, not SearchRecipe — "without X" is ambiguous             | Week 8 Day 2, TC05            | Low      | Month 3 |
| I-6 | "hello" / greetings classified as SearchRecipe — no greeting/small-talk signals in IntentRouter           | Week 8 Day 1 TC35, Day 2 TC50 | Low      | Month 3 |
| I-7 | "whats on monday?" → GeneralQuestion, not GetMealPlan — day-specific plan queries not handled             | Week 8 Day 2, TC25            | Low      | Month 3 |
| I-8 | Question-form ValidateDiet not recognized — "is X vegan?", "can Z eat X?", "is X safe for Y?" all classified as SearchRecipe | Week 11 E2E eval, cases 25/28/29/30/55/56 | Medium | Month 4 |
| I-9 | "plan dinners for the week, I'm dairy-free" → SearchRecipe — CreateMealPlan rules don't handle dietary constraint appended to plan phrase | Week 11 E2E eval, case 31 | Medium | Month 4 |

**Fix pattern for I-1 to I-3:** Add phrasing variants to IntentRouter rules. Low effort, additive.
**Fix pattern for I-4 to I-5:** Tighten ValidateDiet signal — require explicit restriction keywords or question form, not just ingredient names.

---

## Search & Retrieval

| #   | Issue                                                                                                             | Source                  | Priority | Target  |
| --- | ----------------------------------------------------------------------------------------------------------------- | ----------------------- | -------- | ------- |
| S-1 | Semantic negation: "dairy-free" matches literal text, not semantic category — milk/cheese still appear in results | Week 2 known limitation | Medium   | Month 3 |
| S-2 | LLM reranker (`RecipeReranker.cs`) built but opt-in (`rerank: false`) — too slow on CPU for interactive use       | Week 2                  | Medium   | Month 3 |
| S-3 | Query expansion (`QueryPreprocessor.cs`) built but opt-in (`expand: false`) — same hardware constraint            | Week 2                  | Low      | Month 3 |
| S-4 | No embedding cache — repeated queries re-embed on every request                                                   | Week 8 Day 3            | Medium   | Month 3 |
| S-5 | Cold start penalty — first embedding call after Ollama startup takes 15-20s (model load). No startup warmup.      | Week 8 Day 3            | Medium   | Month 3 |

**S-1 fix:** LLM-based semantic filtering — check if recipe ingredient _semantically implies_ a category (e.g. "cheddar" → dairy), not just literal string match. Requires LLM call per recipe, gate behind flag.
**S-4 fix:** Redis-backed embedding cache keyed on query hash. TTL 24h. Near-zero latency on cache hit.
**S-5 fix:** Startup warmup call — embed a dummy string at API boot to pre-load Ollama model.

---

## Memory & Session

| #   | Issue                                                                                                                | Source            | Priority | Target  |
| --- | -------------------------------------------------------------------------------------------------------------------- | ----------------- | -------- | ------- |
| M-1 | Reference resolution falls through silently — "the first one" with no history searches literally for "the first one" | Week 8 Day 1 TC21 | Low      | Month 3 |
| M-2 | Profile entity extraction fires on every first message of a new session (~12s) — result not cached                   | Week 8 Day 3      | High     | Month 3 |
| M-3 | Reference resolution uses LLM for ordinal references ("first", "second") — rules can handle 90% of cases             | Week 8 Day 3      | Low      | Month 3 |
| M-4 | Redis connection timeout still 8-15s under failure (4 ops × 2s each) — no Redis circuit breaker                      | Week 8 Day 1      | Medium   | Month 3 |

**M-2 fix (high priority):** Cache entity extraction result in Redis alongside raw profile. Key: `session:{id}:entities`. Only re-extract when profile changes. Drops first-access from ~12s to <0.1s.
**M-3 fix:** Rules-based ordinal resolver — "the first/second/third one" → index into last search results in history. No LLM needed for the common case.
**M-4 fix:** Redis circuit breaker — after first Redis failure, skip ops 2-4. Drops failure latency from ~8s to ~2s.

---

## Planning

| #   | Issue                                                                                     | Source             | Priority | Target  |
| --- | ----------------------------------------------------------------------------------------- | ------------------ | -------- | ------- |
| P-1 | Plan generation is sequential — 7 embedding calls run one after another                   | Week 8 Day 3       | Medium   | Month 3 |
| P-2 | Multi-slot planning (breakfast + lunch + dinner × 7 = 21 calls) not fully tested at scale | Week 8 Day 2       | Low      | Month 3 |
| P-3 | No variety enforcement across plan days — same recipe could appear multiple times         | Week 4 design note | Low      | Month 3 |

**P-1 fix:** `Task.WhenAll` for parallel search — 7 concurrent embedding calls instead of sequential. Expected improvement: 0.85s → ~0.15s on warm Codespaces, larger gains on cold start.

---

## Dietary Validation

| #   | Issue                                                                                                                                            | Source                | Priority | Target  |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------- | -------- | ------- |
| D-1 | Kosher validation incomplete — meat/dairy separation (basar b'chalav), shechita requirement, forbidden species beyond pork/shellfish not handled | DietaryRules.cs FIXME | Low      | Month 3 |
| D-2 | Diet validation asks for clarification when query is vague — no proactive profile usage in clarification response                                | Week 8 Day 2, TC11    | Low      | Month 3 |

**D-1 fix:** LLM fallback for Kosher — complex enough that rules can't cover it, needs semantic reasoning.

---

## Guardrails

| #   | Issue                                                                                             | Source       | Priority | Target  |
| --- | ------------------------------------------------------------------------------------------------- | ------------ | -------- | ------- |
| G-1 | No global rate limiter tested in e2e (only per-session) — TC41 showed 5/35 throttled              | Week 8 Day 2 | Low      | Month 3 |
| G-2 | OutputGuard confidence signaling appends note to message — slightly awkward UX for Low confidence | Week 7       | Low      | Month 3 |
| G-3 | Rate limit test requires multiple separate requests — repeated text in single message doesn't trigger per-session counter | Week 11 E2E eval, case 53 | Low | Month 4 |

---

## Infrastructure & Performance

| #     | Issue                                                                                               | Source          | Priority | Target  |
| ----- | --------------------------------------------------------------------------------------------------- | --------------- | -------- | ------- |
| Inf-1 | Microsoft.SemanticKernel 1.30.0 → 1.77.0 available — deferred, major API changes risk               | Week 8 Day 4    | Low      | Month 4 |
| Inf-2 | GeneralQuestion LLM inference: p50 6.97s, p95 21.64s on Codespaces CPU — GPU or faster model needed | Week 8 Day 3    | Medium   | Month 3 |
| Inf-3 | No distributed tracing / observability — latency breakdown per-operation not visible in production  | Month 3 roadmap | High     | Month 3 |
| Inf-4 | Docker compose only — no cloud deploy, no Kubernetes manifests                                      | Month 3 roadmap | High     | Month 3 |
| Inf-5 | RAGAS evaluation pipeline not yet integrated                                                        | Month 3 roadmap | High     | Month 3 |
| Inf-6 | Langfuse observability not yet integrated                                                           | Month 3 roadmap | High     | Month 3 |

---

## Testing Gaps

| #   | Issue                                                                                 | Source               | Priority | Target       |
| --- | ------------------------------------------------------------------------------------- | -------------------- | -------- | ------------ |
| T-1 | No unit tests for IntentRouter (rules path)                                           | Week 8 Day 5 plan    | High     | Week 8 Day 5 |
| T-2 | No unit tests for DietaryRules engine                                                 | Week 8 Day 5 plan    | High     | Week 8 Day 5 |
| T-3 | No unit tests for InputGuard.Validate                                                 | Week 8 Day 5 plan    | High     | Week 8 Day 5 |
| T-4 | No unit tests for CircuitBreaker state transitions                                    | Week 8 Day 5 plan    | High     | Week 8 Day 5 |
| T-5 | Profiling script reuses same session+query — repeat detector skews /chat measurements | Week 8 Day 3 finding | Low      | Month 3      |
| T-6 | No concurrent request testing — all tests are single-threaded                         | Week 8 Day 1 note    | Low      | Month 3      |
| T-7 | No partial failure testing (Ollama hanging vs down)                                   | Week 8 Day 1 note    | Low      | Month 3      |
| T-8 | E2E eval case 53 (rate limit) uses wrong trigger pattern — needs setup_messages with 4+ identical requests, not repeated text in one message | Week 11 E2E eval | Low | Month 4 |
| T-9 | Implicit dietary LLM extraction is non-deterministic — "I can't have gluten, what pasta can I eat?" flips between SearchRecipe and ValidateDiet across runs | Week 11 E2E eval, case 46 | Low | Month 4 |

**T-1 to T-4:** These are Day 5 items — pure logic tests, no Docker needed, fast to write.

---

## Dataset

| #    | Issue                                                                                           | Source          | Priority | Target  |
| ---- | ----------------------------------------------------------------------------------------------- | --------------- | -------- | ------- |
| DS-1 | `corbt/all-recipes` dataset may need to be replaced with RecipeNLG for richer metadata          | Week 1 decision | Low      | Month 3 |
| DS-2 | Only 10K recipes loaded — full dataset is larger, more coverage would improve retrieval quality | Week 1          | Low      | Month 3 |

---

## Summary

| Category              | Items  | High  | Medium | Low    |
| --------------------- | ------ | ----- | ------ | ------ |
| Intent Classification | 7      | 0     | 0      | 7      |
| Search & Retrieval    | 5      | 0     | 3      | 2      |
| Memory & Session      | 4      | 1     | 1      | 2      |
| Planning              | 3      | 0     | 1      | 2      |
| Dietary Validation    | 2      | 0     | 0      | 2      |
| Guardrails            | 2      | 0     | 0      | 2      |
| Infrastructure        | 6      | 4     | 1      | 1      |
| Testing Gaps          | 7      | 4     | 0      | 3      |
| Dataset               | 2      | 0     | 0      | 2      |
| **Total**             | **38** | **9** | **6**  | **23** |

**9 High priority items:** 4 unit tests (Day 5 this week) + M-2 entity extraction cache + Inf-3 observability + Inf-4 cloud deploy + Inf-5 RAGAS + Inf-6 Langfuse (all Month 3).

The 23 Low priority items are real improvements but won't block Month 3 goals. Revisit in Month 4.
