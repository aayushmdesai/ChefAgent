# ChefAgent — Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [v0.5.0] — Week 8 (Integration Testing + Hardening)

### Added

- Failure mode matrix: 36 automated test cases across 7 service outage combinations (Qdrant, Ollama, Redis, and combinations)
- 50-query end-to-end scenario sweep across all intents and edge cases
- Performance profiling: p50/p95 latency for 18 operations, bottleneck documentation
- Unit test suite: 68 tests (InputGuard, DietaryRules, IntentRouter, CircuitBreaker)
- CI pipeline: unit tests + health check stage (Docker Compose + `/health` verification)
- Build badge on README
- `docs/tech-debt.md`: consolidated 38-item backlog from all 8 weeks
- `docker-compose.ci.yml`: lightweight CI compose (Redis + Qdrant, no Ollama)

### Fixed

- Redis connection timeout reduced from ~30s to ~15s (4 ops × 2s each) using `ConfigurationOptions` with explicit `AsyncTimeout`
- Stale `placeholder` and `stub` comments removed from `AgentOrchestrator.cs` and `SessionStore.cs`
- Nullability warning in `RecipeReranker.cs` (`Task<string>` → `Task<string?>`)

### Changed

- All infrastructure constants (Redis timeouts, Ollama timeout, CircuitBreaker threshold/cooldown, RateLimiter limits) moved to `appsettings.json`
- `Qdrant.Client` updated from 1.12.0 to 1.18.1
- CI actions updated to run on Node.js 24

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
- Stateless degradation: all Redis operations wrapped in try/catch, system continues without memory
- 15-case stateful flow test matrix

### Fixed

- `GetMealPlan` was misrouting to SearchRecipe — added intent signal + handler
- Contraction forms ("whats", "show me my") failing GetMealPlan classification

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
- Plan generation and persistence split — generation failure ≠ persistence failure

---

## [v0.1.0] — Weeks 1–4 (Recipe + Diet Agents, Orchestrator, UI)

### Added

- RAG pipeline: 10K recipes embedded with `nomic-embed-text` (768-dim), loaded into Qdrant
- `RecipeSearchPlugin`: vector search, payload filtering (ingredient count, step count), negation handling
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

[v0.5.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.5.0
[v0.4.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.4.0
[v0.3.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.3.0
[v0.2.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.2.0
[v0.1.0]: https://github.com/aayushmdesai/ChefAgent/releases/tag/v0.1.0
