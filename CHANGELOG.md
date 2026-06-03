# ChefAgent — Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [v0.8.0] — Week 11

### Added
- **Embedding cache** (`RecipeSearchPlugin.cs`) — in-memory `ConcurrentDictionary<string, float[]>` keyed on query text. Cache hit skips Ollama entirely. 1813ms → 12ms on repeated queries (99.3% latency reduction). `embed.cache_hit` and `embed.ollama` Langfuse spans make cache effectiveness visible in traces.
- **E2E eval harness** (`eval/harnesses/eval_e2e.py`) — calls `/chat` instead of `/recipes/search`. Handles single-shot and stateful sequence test cases. Run-scoped session ID prefix prevents Redis state collision across runs.
- **E2E golden dataset** (`eval/datasets/e2e_golden_dataset.json`) — 60 test cases across 10 intent categories including stateful sequences (CreateMealPlan → ModifyMealPlan → GetMealPlan).
- **LLM judge** (`eval/harnesses/llm_judge.py`) — scores `/chat` responses on helpfulness (1-5), safety (1-5), coherence (1-5) using Ollama. 56 cases scored, 0 failures.
- **Semantic negation test** (`scripts/eval/test_semantic_negation.py`) — 7 X-free test cases + 1 control validating dairy-free, gluten-free, nut-free filtering.
- **Month 3 eval report** (`eval/datasets/month3-eval-report.md`) — consolidated RAGAS, e2e, and Langfuse results with trend lines across experiments.
- **Eval pipeline README** (`eval/README.md`) — reproducibility doc for running the full eval pipeline from scratch.
- **ADR-011** (`docs/adrs/011-evaluation-strategy.md`) — why three eval layers, judge model limitations, experiment tracking design.

### Changed
- **`DietaryRules.cs` moved to `ChefAgent.Shared`** — was in `ChefAgent.Agents.Diet`. Added `GetCategoryIngredients(string category)` public method. `DietValidationPlugin.cs` namespace updated.
- **`QueryPreprocessor.ParseNegation`** — X-free pattern now expands category name to full ingredient set via `DietaryRules.GetCategoryIngredients`. "dairy-free" now excludes ~35 ingredient terms instead of the literal word "dairy". Zero latency cost — rules lookup, no LLM, no I/O.
- **`eval_e2e.py`** — added `message_full` field to evaluation output for LLM judge consumption.

### Removed
- **`DietaryCategoryMap.cs`** — superseded by `DietaryRules.GetCategoryIngredients`. Single source of truth for dietary ingredient sets now lives in `DietaryRules`.

### Fixed
- "dairy-free pasta" returning recipes with cream, butter, mozzarella — semantic negation expansion fix.
- "gluten-free bread" returning wheat-containing recipes — same fix.

### Evaluation results (Week 11)
- x_free answer_relevancy: 0.213 → 0.325 (+0.112) via semantic negation fix
- faithfulness overall: 0.444 → 0.488 (+0.044)
- E2E pass rate: 47/60 (78%), 49/57 (86%) adjusted
- Intent accuracy: 78% — intent classifier identified as weakest link
- LLM judge weakest: implicit_dietary H:2.33
- LLM judge strongest: general_question, search_with_diet both H:4.0

### Tech debt added
- I-8: ValidateDiet question-form not recognized ("is X vegan?", "can Z eat X?")
- I-9: CreateMealPlan phrasing miss ("plan dinners... I'm dairy-free")
- G-3: Rate limit test requires multiple separate requests, not repeated text
- T-8: E2E case 53 (rate limit) needs actual setup_messages, not repeated text
- T-9: Implicit dietary LLM extraction is non-deterministic across runs

---

## [v0.7.0] — Week 10 (Langfuse Observability)

### Added
- **Self-hosted Langfuse v2** — Docker Compose (postgres + server, ~650MB). Auto-provisioned org/project/API keys via `LANGFUSE_INIT_*` env vars — no manual UI setup.
- **`Tracing.cs`** — fire-and-forget Langfuse client. `Channel<T>` + `IHostedService` background worker. < 1ms overhead on request thread. Never throws to callers.
- **14 span types** across all agents — `chat.request`, `intent.classify`, `intent.llm_extraction`, `recipe_agent.search`, `embed.ollama`, `diet_agent.validate`, `diet.llm_validation`, `reranker.llm`, `planner_agent.generate`, `planner_agent.modify`, `plan.redis_read`, `plan.redis_write`, `circuit_breaker.blocked`, `guardrail.blocked`
- **`MetricsCollector.cs`** — sliding 5-minute window, p50/p95/p99 latency per intent. `/admin/metrics` endpoint.
- **Correlation IDs** — propagated via `BeginScope` across all agent log lines. Every log line for a request shares the same short ID.
- **`TraceContext`** — lightweight record (traceId, spanId, correlationId) passed as parameter. No ambient state, no static globals.
- **Week 10 observability test suite** (`scripts/eval/week10_observability_test.py`) — 13 scenarios covering every trace path.
- **ADR-010** (`docs/adrs/010-observability-architecture.md`)

### Performance (Week 10 test run, 13 scenarios)
- p50: 101ms | p95: 14,326ms (LLM extraction path) | p99: 14,326ms
- Guardrail blocked: 4ms | GetMealPlan: 15ms | ModifyMealPlan: 115ms
- Tracing overhead: < 1ms confirmed

---

## [v0.6.0] — Week 9 (Evaluation Pipeline + Spell-Check)

### Added
- **RAGAS-style eval pipeline** — `retrieve.py` (local) + `score_simple.py` (Colab GPU) + `compare_experiments.py`
- **100-query golden dataset** (`eval/datasets/golden_dataset.json`) — 12 categories, labeled ground truth
- **Experiment tracking** — timestamped JSON files in `eval/experiments/`, diff tool with ↑/↓/→ arrows
- **SymSpell spell correction** (`QueryPreprocessor.cs`) — frequency-weighted, replaces Hunspell. Handles long-tail misspellings automatically.
- **Food domain dictionary** (`food_corrections.json`) — high-confidence culinary corrections (chiken → chicken, soop → soup). Runs before SymSpell, takes priority.
- **Entity extraction cache** (`SessionStore.cs`) — Redis key `session:{id}:extraction`. First session with implicit constraint: 90s → 13ms on cache hit.
- **Redis circuit breaker** — keyed singleton (`redis`, 30s cooldown), independent from Ollama breaker. Redis failure fast-fails in < 1ms instead of 15s timeout.
- **ADR-009** (`docs/adrs/009-evaluation-pipeline.md`)

### Evaluation results (Week 9)
- Baseline context_relevance: 0.470 → post spell-check: 0.524 (+0.054)
- misspelling category: 0.442 → 0.617 (+0.175) — targeted improvement
- x_free category: 0.438 → 0.588 (+0.150) — unexpected bonus

---

## [v0.5.0] — Week 8 (Integration Testing + Hardening)

### Added
- Failure mode matrix: 36 automated test cases across 7 service outage combinations
- 50-query end-to-end scenario sweep across all intents and edge cases
- Performance profiling: p50/p95 latency for 18 operations
- Unit test suite: 68 tests (InputGuard, DietaryRules, IntentRouter, CircuitBreaker)
- CI pipeline: unit tests + health check stage (Docker Compose + `/health` verification)
- Build badge on README
- `docs/tech-debt.md`: consolidated 38-item backlog from all 8 weeks
- `docker-compose.ci.yml`: lightweight CI compose (Redis + Qdrant, no Ollama)

### Fixed
- Redis connection timeout reduced from ~30s to ~15s using `ConfigurationOptions` with explicit `AsyncTimeout`
- Stale `placeholder` and `stub` comments removed from `AgentOrchestrator.cs` and `SessionStore.cs`
- Nullability warning in `RecipeReranker.cs` (`Task<string>` → `Task<string?>`)

### Changed
- All infrastructure constants moved to `appsettings.json`
- `Qdrant.Client` updated from 1.12.0 to 1.18.1

---

## [v0.4.0] — Week 7 (Guardrails + Audit Logging)

### Added
- `InputGuard`: 5-layer input validation (length, injection detection, oversized, repeat, rate limit)
- `OutputGuard`: response shape validation + confidence signaling (High / Medium / Low)
- `CircuitBreaker`: three-state machine (Closed → Open → HalfOpen) for Ollama fault tolerance
- `RateLimiter`: per-session sliding window (30 req/min default)
- `GuardrailAuditLog`: 9 event types, `/admin/guardrails` endpoint
- Two-signal injection detection (override keyword + system-level action required)
- Confidence level attached to every `/chat` response
- 15-case InputGuard test matrix + 18-case guardrails integration test matrix

### Changed
- All agent calls gated through circuit breaker — Ollama failures fail fast (0.03s) instead of timing out (100s+)

---

## [v0.3.0] — Week 6 (Conversation History + Profile Persistence)

### Added
- Conversation history: sliding window (20 entries) in Redis per session
- Profile persistence: dietary profile stored in Redis, loaded on every request
- `GetMealPlan` intent + `GET /profile/{sessionId}` + `POST /profile/{sessionId}`
- LLM entity extraction: implicit dietary constraints ("I can't have dairy") → profile update
- Contraction normalization: `"what's"` → `"what is"` before rule matching
- Reference resolution: "the first one" → indexed into last search results
- Stateless degradation: all Redis operations wrapped in try/catch
- 15-case stateful flow test matrix

### Fixed
- `GetMealPlan` was misrouting to SearchRecipe — added intent signal + handler
- Contraction forms failing GetMealPlan classification

---

## [v0.2.0] — Week 5 (Planner Agent + Session Memory)

### Added
- `PlannerAgent`: 7-day meal plan generation via 7× sequential Recipe Agent calls
- `ModifyMealPlan` intent: swap individual plan slots by day
- Redis session memory: plan storage with 7-day TTL
- `MealPlan` model with day-indexed slots
- React UI: dietary profile sidebar, meal plan display, swap controls

### Changed
- `OrchestratorResponse` extended with `mealPlan` field

---

## [v0.1.0] — Weeks 1–4 (Recipe + Diet Agents, Orchestrator, UI)

### Added
- RAG pipeline: 10K recipes embedded with `nomic-embed-text` (768-dim), loaded into Qdrant
- `RecipeSearchPlugin`: vector search, payload filtering, negation handling
- `DietaryRules`: 420+ phrase-level rules across 12 dietary categories (94% coverage)
- `DietAgent`: rules engine → LLM fallback → substitution suggestions
- `IntentRouter`: rules-based classification (< 1ms, zero LLM calls)
- `AgentOrchestrator`: routes SearchRecipe, ValidateDiet, GeneralQuestion
- `POST /recipes/search`, `POST /recipes/search-validated`, `POST /chat`, `GET /health`
- React + Tailwind UI with per-recipe dietary validation badges
- LLM re-ranker and query expansion (built, opt-in, hardware-limited)
- 24-query retrieval test suite, 20-case diet test matrix, 20-case orchestrator matrix
- ADRs 001–005

---

[v0.8.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.8.0
[v0.7.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.7.0
[v0.6.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.6.0
[v0.5.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.5.0
[v0.4.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.4.0
[v0.3.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.3.0
[v0.2.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.2.0
[v0.1.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.1.0