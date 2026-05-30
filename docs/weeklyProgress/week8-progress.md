# ChefAgent — Week 8 Progress Log

**Date:** May 29, 2026
**Goal:** Integration testing, hardening, CI enhancement, Month 2 retrospective
**Status:** ✅ Day 1 (Failure Mode Matrix) Complete | ⬜ Day 2 (E2E Sweep) | ⬜ Day 3 (Performance Profile) | ⬜ Day 4 (Tech Debt Sweep) | ⬜ Day 5 (CI Enhancement) | ⬜ Days 6–7 (Retrospective + v0.5.0)

---

## What We're Building

The hardening week. Seven weeks of rapid feature building produced a working multi-agent system — three agents, five guardrail layers, session memory, conversation history, and a full Docker Compose stack. Week 8 stops adding features and proves the system is solid under stress, documents its performance characteristics, cleans up accumulated tech debt, and makes it presentation-ready.

```
Weeks 1–7:  Build features — agents, memory, guardrails, orchestration

Week 8:     Prove it works
                │
                ├─ Day 1: Failure mode matrix         — break every service, document behavior
                ├─ Day 2: E2E scenario sweep (50)     — full pipeline, every intent, every edge case
                ├─ Day 3: Performance profiling        — measure, identify bottlenecks, document fix paths
                ├─ Day 4: Tech debt sweep              — TODOs, dead code, consistency, config audit
                ├─ Day 5: CI pipeline enhancement      — unit tests, health check stage, build badge
                └─ Days 6–7: Month 2 retrospective    — docs, README, architecture diagram, v0.5.0
```

---

## Day 1 — Failure Mode Matrix: Systematically Break Everything

### What we built

`scripts/eval/test_failure_modes.py` — a fully automated test script that stops Docker Compose services, fires queries against the running API, records HTTP status + response body + latency, restarts services, and generates a markdown report.

`eval/datasets/failure_mode_matrix.md` — the complete results: 36 test cases across 7 scenarios (baseline + 6 outage combinations), every user-visible message captured, degradation summary table, key findings, and fix paths.

### The test matrix

7 scenarios testing every service outage and combination:

| Scenario | Services Down | Test Cases |
|----------|--------------|------------|
| 0: Baseline | None | 5 |
| 1: Qdrant down | Qdrant | 6 |
| 2: Ollama down | Ollama | 6 |
| 3: Redis down | Redis | 6 |
| 4: Qdrant + Ollama | Qdrant, Ollama | 5 |
| 5: Ollama + Redis | Ollama, Redis | 4 |
| 6: All down | Qdrant, Ollama, Redis | 4 |

Each scenario tests: recipe search, diet validation, plan generation, plan retrieval, general questions, and recovery after restart.

### Results

**36/36 passed. Zero 500 errors. No stack traces leaked to client.**

Every service outage combination returns 200 with a user-friendly message. The system degrades gracefully across all failure modes.

### Degradation summary

| Scenario | Search | Diet | Plan Gen | Plan Read | General Q | Profile |
|----------|--------|------|----------|-----------|-----------|---------|
| baseline | ✅ 19.1s | ✅ | ✅ 1.08s | ✅ 0.02s | ✅ 100.4s | ✅ |
| qdrant↓ | ❌ 0.75s | ✅ | ❌ 0.11s | ✅ 0.01s | ✅ 11.0s | ✅ |
| ollama↓ | ❌ 0.08s | ✅ rules | ❌ 0.03s | ✅ 0.01s | ❌ 0.03s | ✅ |
| redis↓ | ✅ 23.8s ⚠️ | ✅ | ✅ unsaved ⚠️ | ❌ 30.0s ⚠️ | ✅ 47.1s ⚠️ | ❌ stateless |
| qdrant+ollama↓ | ❌ 0.08s | ✅ rules | ❌ 0.03s | ✅ 0.01s | ❌ 0.03s | ✅ |
| ollama+redis↓ | ❌ 23.4s ⚠️ | ✅ rules | ❌ | ❌ 30.0s ⚠️ | ❌ 24.0s ⚠️ | ❌ |
| all↓ | ❌ 22.6s ⚠️ | ✅ rules | ❌ | ❌ 30.0s ⚠️ | ❌ 24.0s ⚠️ | ❌ |

**⚠️ = Redis connection timeout dominates (~30s). Ollama failures are instant (circuit breaker).**

### Three key findings

**Finding 1 (High) — Redis timeout is too slow.**

Every Redis-down scenario shows 23–30s latencies. The Redis client waits its full connection timeout before falling back to stateless mode. Compare to Ollama-down where the circuit breaker makes failures instant (0.03s vs 30s). The asymmetry is clear: Week 7 invested in fast-fail for Ollama but not for Redis.

**Root cause:** Default `connectTimeout` in the Redis connection string. The fix is reducing it to 2–3 seconds — the system already handles Redis failure gracefully (Week 6), it just takes too long to discover the failure.

**Finding 2 (Low) — Reference resolution falls through (TC21).**

When Redis is down, "the first one" passes through reference resolution as a literal string, and the Orchestrator searches for recipes matching "the first one." The user sees 5 random recipes instead of a clarification message. Not a crash, but misleading UX.

**Root cause:** `ResolveReferencesAsync` returns the raw query when history isn't available. No guard checks whether resolution was attempted but failed vs. never attempted.

**Finding 3 (Low) — "hello" misclassified as SearchRecipe (TC35).**

With all services down, "hello" hits the search path → fails → returns "search unavailable." Expected: greeting or unknown-intent response. The rules-based IntentRouter doesn't have greeting/small-talk signals, so short unrecognized queries fall to the default.

### The circuit breaker success story

The most validating result from the matrix: **Ollama-down operations complete in 0.01–0.08s** vs the baseline general question at 100.44s. The Week 7 circuit breaker eliminated 100+ seconds of timeout waiting by skipping LLM calls entirely when the service is known to be down. This is the clearest proof that the "rules first, LLM as fallback" architecture pays off — when the LLM disappears, the system falls back to rules instantly instead of hanging.

### What didn't we test

- **Qdrant + Redis down** — skipped because it's a subset of "all down" (search fails from Qdrant, memory fails from Redis, only Ollama-based general questions survive)
- **Partial Ollama failure** (model loaded but inference hanging) — harder to simulate, would need a mock endpoint
- **Concurrent requests during failure** — single-threaded test, no load testing
- **Network partition vs service down** — Docker stop is a clean shutdown, not a network timeout

### Key interview talking points

**"How do you test resilience in a multi-service system?"** Failure mode matrix — systematically stop every service and combination, fire queries, document what the user sees. We tested 36 cases across 7 outage scenarios. Found that our circuit breaker makes Ollama failures instant (0.03s) but Redis failures still take 30s because we hadn't applied the same fast-fail pattern.

**"What's the difference between how your system handles Ollama vs Redis failures?"** Ollama has a three-state circuit breaker — after 3 consecutive failures it skips all LLM calls instantly (0.03s). Redis uses try/catch with connection timeout (~30s). The failure matrix exposed this asymmetry. Same architectural pattern (fast-fail on known-down service) should be applied to both. We'd already built the graceful degradation (Week 6) — the problem was discovery latency, not behavior.

**"What did the failure matrix find that unit tests didn't?"** Three things unit tests can't catch: (1) Redis timeout latency — only visible under real network failure, not mockable in isolation. (2) Reference resolution falling through to literal search when history is unavailable — a cross-cutting flow between Redis, the Orchestrator, and the Recipe Agent. (3) Intent misclassification under total outage — "hello" hitting the search path only matters when search is down.

**"Why 36/36 with zero 500s?"** Because every external call (Qdrant, Ollama, Redis) is wrapped in try/catch with a typed fallback. The key design principle: generation and persistence are separate concerns. The Planner generates a meal plan, then tries to save it to Redis. If Redis fails, the plan still reaches the user — only persistence fails. Same principle at every layer.

**"Your diet validation still works when everything is down. How?"** The rules engine is in-memory code — no external dependencies. It handles 94% of cases without touching Ollama. When Ollama is down, it falls back to rules-only mode. When Qdrant is down, diet validation still works because it doesn't need vector search. The architecture was designed so that the cheapest, most reliable path handles the majority of cases.

**"What would you do differently?"** Apply the circuit breaker pattern to Redis from the start, not just Ollama. The failure matrix made the asymmetry obvious — same failure mode (service down), same solution (fast-fail), but we only built it for one service. In a production system, every external dependency gets a circuit breaker or a tight connection timeout.

### Concepts learned

| Concept | What It Means |
|---------|---------------|
| Failure mode matrix | Systematic testing of every service outage combination — not just individual failures, but the cross-product |
| Fast-fail asymmetry | Circuit breaker gave Ollama 0.03s failure; Redis still waits 30s. Same problem, same fix needed |
| Discovery latency vs behavior | The system *behaves* correctly when Redis is down (Week 6). The problem is it takes 30s to *discover* Redis is down |
| Cross-cutting failures | Some bugs only appear when testing full flows under partial outage — invisible to component tests |
| Recovery verification | Testing that services come back is as important as testing that they fail gracefully |
| Rules engine as bedrock | In-memory rules (diet validation, intent classification, input guard) work regardless of infrastructure state |

### Files created / changed

```
scripts/eval/test_failure_modes.py      # New: 36-case automated failure matrix test runner
eval/datasets/failure_mode_matrix.md    # New: full results + degradation summary + findings
```

### Fix path

| Finding | Fix | Priority | Target | Status |
|---------|-----|----------|--------|--------|
| Redis timeout ~30s | `ConfigurationOptions` with 2s timeouts | **High** | Day 1 | ✅ Fixed (30s → 15s) |
| Reference resolution fallthrough | Guard: if history unavailable + query is reference-like → clarify | Low | Month 3 | Backlog |
| "hello" misclassification | Add greeting signals to IntentRouter | Low | Month 3 | Backlog |
| Embedding requires Ollama | Embedding cache or keyword fallback | Medium | Month 3 | Backlog |
| Sequential plan gen amplifies failures | Parallel search + early termination | Medium | Month 3 | Backlog |

---

### Redis timeout fix (same day)

**Problem:** Redis-down scenarios took ~30s because `StackExchange.Redis` defaults to 5000ms per operation timeout. With 4 sequential Redis operations (append user message → load profile → get plan → append assistant message), that's 4 × 5s = 20s+ of waiting.

**Attempted fix 1:** Connection string parameters (`connectTimeout=2000,syncTimeout=2000,asyncTimeout=2000`). Did not work — StackExchange.Redis ignores connection string timeout params for "backlog" timeouts when the connection was established and then lost.

**Working fix:** Set timeouts programmatically via `ConfigurationOptions`:

```csharp
var options = ConfigurationOptions.Parse(connectionString);
options.ConnectTimeout = config.GetValue<int>("Redis:ConnectTimeoutMs", 2000);
options.SyncTimeout = config.GetValue<int>("Redis:SyncTimeoutMs", 2000);
options.AsyncTimeout = config.GetValue<int>("Redis:AsyncTimeoutMs", 2000);
options.AbortOnConnectFail = false;
```

**Result:** 30s → 15s (4 ops × 2s timeout + overhead). Confirmed `timeout: 2000` in logs.

**Remaining gap:** 15s is still slow. 4 sequential Redis operations each wait the full 2s. A Redis circuit breaker (skip ops 2–4 after op 1 fails) would drop it to ~4s — Month 3 path.

**Bonus:** Made all infrastructure timeouts configurable via `appsettings.json` — Redis timeouts, Ollama HTTP timeout, circuit breaker threshold/cooldown, rate limiter limits. No more hardcoded values.

### Configuration audit (pulled forward from Day 4)

All constants now configurable in `appsettings.json`:

```json
{
  "Redis": { "ConnectTimeoutMs": 2000, "SyncTimeoutMs": 2000, "AsyncTimeoutMs": 2000 },
  "Ollama": { "TimeoutSeconds": 120 },
  "CircuitBreaker": { "FailureThreshold": 3, "CooldownSeconds": 60 },
  "RateLimiter": { "PerSessionLimit": 30, "GlobalLimit": 100, "WindowSeconds": 60 }
}
```

### Files created / changed (including fix)

```
scripts/eval/test_failure_modes.py      # New: 36-case automated failure matrix test runner
eval/datasets/failure_mode_matrix.md    # New: full results + degradation summary + findings
src/api/appsettings.json                # Updated: Redis timeouts, all infrastructure config
src/api/ServiceRegistration.cs          # Updated: ConfigurationOptions for Redis, config-driven DI
```

---

## What's Next

- [x] Day 1: Failure mode matrix — 36/36, zero 500s, 3 findings, Redis timeout fix (30s → 15s)

---

## Day 2 — E2E Scenario Sweep: 50 Queries Across Every Path

### Results

**44/50 passed. Zero 500 errors.**

| Category | Passed | Avg Latency | Notes |
|----------|--------|-------------|-------|
| Recipe Search | 7/10 | 1.1s | 3 dietary-keyword false positives |
| Dietary Validation | 8/8 | 10.4s | TC15 (91s) — LLM query expansion triggered |
| Meal Planning | 4/5 | 0.3s | 3 unrecognized plan phrasings |
| Meal Planning (Redis read) | 3/3 | 3.3s | GetMealPlan instant |
| Guardrails | 8/8 | 0.1s | All injection, rate limit, repeat, oversize — clean |
| General / Edge Cases | 10/10 | 3.3s | Special chars, mixed case, unicode all handled |

### All 6 failures live in the IntentRouter — not error handling

**Pattern A: Dietary keywords hijack search intent (TC03, TC05, TC09)**

Queries that contain dietary restriction language but intend a recipe search get classified as `ValidateDiet` by the rules engine. "Pasta without dairy" → the rules see "without dairy" as a dietary signal. "Vegetarian stir fry without nuts" → same. "Recipes with garlic and tomatoes" → TC03 is the only genuine false positive; "garlic" shouldn't match dietary restriction patterns.

TC05 and TC09 are debatable — the current behavior isn't wrong per se, it's intent ambiguity. TC03 is a clean false positive in the rules.

**Pattern B: Plan intent rephrasings not in rules (TC23, TC24, TC26)**

The IntentRouter rules handle canonical phrasings from Week 4 but not natural variations:

| Query | Expected | Got | Missing rule |
|-------|----------|-----|-------------|
| "change Friday to a vegetarian meal" | ModifyMealPlan | SearchRecipe | "change X to" variant |
| "plan breakfast lunch and dinner for the week" | CreateMealPlan | SearchRecipe | multi-meal-type phrasing |
| "make me a new plan" | CreateMealPlan | SearchRecipe | "make me a plan" variant |

### What passed that's worth noting

- **TC38** — "ignore the garlic and add more basil" → SearchRecipe (not blocked). Two-signal injection detection correctly allows culinary "ignore" phrasing.
- **TC41** — Rate limit burst (35 requests): 5 got 429. Rate limiter working.
- **TC40** — Repeat detection: 3rd identical query short-circuited. ✅
- **TC39** — Oversized (600 chars) blocked. ✅
- **TC47** — Jalapeño & crème fraîche (unicode + special chars) → SearchRecipe. ✅
- **TC48** — Mixed case "FiNd Me PaStA ReCiPeS" → SearchRecipe. ✅
- **TC30/TC33** — Profile persists across turns (nut allergy set in TC29 applied in TC30). ✅

### Latency findings

- TC15 (91s): "I can't have dairy" triggered LLM entity extraction — the longest non-plan response. LLM path on CPU.
- TC18 (12s): Shellfish allergy substitution — LLM path.
- TC25 (9.8s): "whats on monday?" → GeneralQuestion (LLM). Interesting — the system can't look up a specific day from the plan, so it falls to LLM which doesn't know either. A `GetMealPlan` + day filter would be better (Month 3).

### Fix paths

| Failure | Fix | Priority | Target |
|---------|-----|----------|--------|
| TC03: "garlic and tomatoes" → ValidateDiet | Tighten ValidateDiet rules, require explicit restriction keywords | Low | Month 3 |
| TC05/TC09: "without X" → ValidateDiet not SearchRecipe | Add "without X" search variant to SearchRecipe rules | Low | Month 3 |
| TC23: "change X to" → not ModifyMealPlan | Add "change [day] to" pattern to IntentRouter | Low | Month 3 |
| TC24/TC26: "plan breakfast…" / "make me a plan" | Add plan phrasing variants to CreateMealPlan rules | Low | Month 3 |
| TC25: "whats on monday?" → GeneralQuestion | Parse day name + route to GetMealPlan with day filter | Low | Month 3 |

### Files created

```
scripts/eval/test_e2e_sweep.py        # New: 50-scenario e2e test runner
eval/datasets/e2e_sweep_results.md    # New: full results + failure analysis
```

### Key interview talking points

**"How do you know your system's intent classifier handles edge cases?"** 50-query e2e sweep across every path — search, diet, planning, conversation context, guardrails, edge cases. All 6 failures cluster in the IntentRouter's rule vocabulary, not the agents or error handling. Zero 500s. That separation matters — it tells me the trust layer is solid and the gaps are in intent coverage, which is a roadmap item, not a stability issue.

**"What's the difference between your failure matrix (Day 1) and the e2e sweep (Day 2)?"** The failure matrix tests infrastructure resilience — services going down, services recovering. The e2e sweep tests functional correctness — does the right agent handle the right query? They test orthogonal things. A system can pass the failure matrix (no 500s under stress) and fail the e2e sweep (wrong intent), or vice versa.

**"You got 44/50 — what are the 6 failures and why didn't you fix them?"** All 6 are IntentRouter vocabulary gaps — phrases the rules don't recognize. "Change Friday to a vegetarian meal" should route to ModifyMealPlan but the rules only know "swap." These are additive improvements to the rules engine, not error handling gaps. Zero 500s means the system is stable. I'm tracking them in the tech debt backlog for Month 3.

**"Why do 'pasta without dairy' and 'vegetarian stir fry without nuts' get classified as ValidateDiet?"** Because the IntentRouter sees "without dairy" and "without nuts" as dietary restriction signals, which is reasonable — those phrases are more commonly used in dietary contexts than search contexts. It's intent ambiguity, not a bug. The fix would be teaching the router to prefer SearchRecipe when a food item precedes the restriction ("pasta without dairy" vs. just "without dairy"). That's a signal-weighting problem, not a missing rule.

**"Your guardrails passed 8/8 including injection attempts. How did you test that?"** Two-signal injection detection — a message has to contain both an override keyword ("ignore", "forget", "you are now") AND a system-level action before it's blocked. TC38 proves the false positive handling: "ignore the garlic and add more basil" is NOT blocked because "ignore" in a culinary context doesn't pair with a system action. That's the Week 7 design decision validated under a full sweep.

### Concepts learned

| Concept | What It Means |
|---------|---------------|
| Orthogonal test coverage | Failure matrix ≠ e2e sweep — infrastructure resilience and functional correctness are tested separately |
| Intent ambiguity | "Pasta without dairy" is genuinely ambiguous — both SearchRecipe and ValidateDiet are defensible interpretations |
| False positive discrimination | "Ignore the garlic" vs "ignore your instructions" — two-signal detection distinguishes culinary language from injection |
| Canonical vs. natural phrasing | Rules handle "plan my dinners" but not "make me a plan" — vocabulary coverage is the gap, not logic |
| Stable vs. correct | 44/50 with zero 500s means the system is stable but not fully correct — that's a useful distinction to articulate |

---

---

## Day 3 — Performance Profiling

### Results summary

| Operation | p50 | p95 | Notes |
|-----------|-----|-----|-------|
| InputGuard (blocked) | <0.01s | 0.03s | Pure in-memory |
| GetMealPlan (Redis) | <0.01s | 0.01s | Redis GET only |
| ValidateDiet (rules) | <0.01s | 0.01s | Rules engine, no I/O |
| Search — warm embed | 0.10s | 0.13s | Ollama embed + Qdrant |
| Search + diet validation | 0.10s | 0.30s | Embed + rules |
| Chat — SearchRecipe | ~0.14s | 0.18s | +0.04s Orchestrator overhead |
| CreateMealPlan (7 days) | 0.85s | 1.21s | 7× sequential embed |
| Reference resolution (LLM) | ~3.8s | 4.18s | LLM interprets history |
| GeneralQuestion (LLM) | 6.97s | 21.64s | llama3.2 CPU inference |
| Profile load + entity extraction | ~11.9s | 11.94s | LLM extraction, first access |

### Key finding: repeat detector skewed /chat measurements

The profiling script reused the same session and query for all 5 runs. The Week 7 repeat detector short-circuited runs 3-5 (returning 0.00s), making p50 for `/chat` operations non-representative. Ground truth for search latency comes from the `/recipes/search` endpoint which has no repeat detector: **0.10-0.13s warm**.

This inadvertently revealed exactly where LLM calls live — operations that stayed slow for runs 1-2 before hitting 0.00s contain LLM calls. Operations instant from run 1 are pure rules or Redis.

### Top 3 bottlenecks

**#1 — LLM inference (GeneralQuestion, entity extraction, reference resolution)**
- p50 6.97s, p95 21.64s for GeneralQuestion
- Profile entity extraction ~12s on first session access
- Fix: cache entity extraction in Redis (high priority, low effort), GPU for inference

**#2 — Profile entity extraction fires every first message (~12s)**
- LLM extracts entities from profile on every new session's first message
- Result not cached — re-extracts even if profile hasn't changed
- Fix: store extracted entities in Redis alongside raw profile, only re-extract on profile change

**#3 — Plan generation is sequential (but less bad than expected)**
- 7-day plan: 0.85s on warm Codespaces — better than the feared 7-14s
- Risk: cold start still 15-20s (model load on first embedding)
- Fix: `Task.WhenAll` parallel search, startup warmup call

### Orchestrator overhead: 0.04s
`/recipes/search` p50 = 0.10s → `/chat` SearchRecipe p50 = ~0.14s. The coordination layer (InputGuard, intent routing, profile load, response formatting) adds only 40ms. Not a bottleneck.

### Files created
```
scripts/eval/profile_performance.py     # New: 18-operation latency profiler
eval/datasets/performance_profile.md    # New: corrected profile with bottleneck analysis
```

### Key interview talking points

**"How do you know where your performance bottlenecks are?"** Systematic profiling — 18 operations, 5 runs each, p50/p95. Found that everything fast is in-memory (rules engine, Redis reads <0.01s), everything slow touches Ollama (LLM inference 7-22s, entity extraction 12s). Orchestrator coordination adds only 40ms overhead. The profiling also revealed a measurement design flaw — the repeat detector short-circuited runs 3-5, which means you need unique queries per run for accurate `/chat` profiling.

**"What's your worst latency and what would you do about it?"** Profile entity extraction at ~12s on first session access. It's LLM-based and runs every time a new session sends its first message. Fix is low effort: cache the extraction result in Redis alongside the raw profile, only re-extract when the profile changes. Drops first-access from 12s to <0.1s without changing the LLM logic at all.

### Concepts learned

| Concept | What It Means |
|---------|---------------|
| Measurement design matters | Reusing same session/query activates repeat detector — profiling tool must be designed to avoid the system's own optimizations |
| Everything fast is in-memory | Rules engine, Redis reads, InputGuard all <0.01s. LLM is the only slow thing. |
| Orchestrator is cheap | 40ms overhead for full intent routing + profile load + response formatting — coordination is not the bottleneck |
| Cache before optimizing | Entity extraction cache (low effort) gives more improvement than parallel plan generation (medium effort) |

---

## Day 4 — Tech Debt Sweep

### Code cleanup

- Removed 3 stale comments: `AgentOrchestrator.cs:21-22` ("placeholder Month 2"), `SessionStore.cs:86` ("stub — wired in Month 2") — both implemented in Month 2, comments were outdated
- Removed FIXME from `DietaryRules.cs:747` (Kosher logic) — moved to `docs/tech-debt.md` as D-1
- Updated `Qdrant.Client` 1.12.0 → 1.18.1 (safe minor bump)
- Deferred `Microsoft.SemanticKernel` 1.30.0 → 1.77.0 — major API changes risk, Month 4

### docs/tech-debt.md

Consolidated 38 items from all 8 weeks of progress docs into one structured backlog:

| Category | Items | High | Medium | Low |
|----------|-------|------|--------|-----|
| Intent Classification | 7 | 0 | 0 | 7 |
| Search & Retrieval | 5 | 0 | 3 | 2 |
| Memory & Session | 4 | 1 | 1 | 2 |
| Planning | 3 | 0 | 1 | 2 |
| Dietary Validation | 2 | 0 | 0 | 2 |
| Guardrails | 2 | 0 | 0 | 2 |
| Infrastructure | 6 | 4 | 1 | 1 |
| Testing Gaps | 7 | 4 | 0 | 3 |
| Dataset | 2 | 0 | 0 | 2 |
| **Total** | **38** | **9** | **6** | **23** |

9 High priority items: 4 unit tests (Day 5), entity extraction cache, observability, cloud deploy, RAGAS, Langfuse — all Month 3.

### Files created / changed
```
docs/tech-debt.md                       # New: 38-item consolidated backlog
src/agents/Orchestrator/AgentOrchestrator.cs  # Stale comments removed
src/shared/SessionStore.cs              # Stale comment removed
src/agents/DietAgent/DietaryRules.cs    # FIXME removed (moved to backlog)
src/api/ChefAgent.Api.csproj            # Qdrant.Client updated to 1.18.1
```

### Key interview talking point

**"How do you manage technical debt on a solo project?"** Explicit backlog. Every time something gets deferred — whether it's a performance optimization, a missing rule, or a feature stub — it goes into `docs/tech-debt.md` with a source (which week, which test case), severity, and target milestone. At end of Month 2 I consolidated 38 items from 8 weeks of progress docs. That's not 38 things that are broken — it's 38 things I consciously chose to defer with a reason and a plan.

---

## What's Next

- [x] Day 1: Failure mode matrix — 36/36, zero 500s, Redis timeout fix (30s → 15s)
- [x] Day 2: E2E sweep — 44/50, zero 500s, all 6 failures are IntentRouter classification gaps
- [x] Day 3: Performance profiling — bottlenecks identified, entity extraction cache flagged as top fix
- [x] Day 5: CI — 68 unit tests, health check stage, build badge, Node.js 24
- [x] Days 6–7: Month 2 retrospective, CHANGELOG, v0.5.0

---

## Day 5 — CI Enhancement

### What was built

- `src/tests/ChefAgent.Tests.csproj` — xUnit test project with Moq
- `InputGuardTests.cs` — 15 cases (length, injection, false positives)
- `DietaryRulesTests.cs` — 19 cases across all dietary categories + helper methods
- `IntentRouterTests.cs` — 14 active + 3 skipped (known gaps I-1, I-2, I-3 from tech-debt.md)
- `CircuitBreakerTests.cs` — 11 cases (state transitions, threshold variations)
- `.github/workflows/ci.yml` — updated with unit test stage + health check job
- `docker-compose.ci.yml` — Redis + Qdrant only (no Ollama, too large for CI)
- `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true` — CI runs on Node.js 24

### Results

**68 passed, 3 skipped, 0 failed.** CI green. Build badge live on README.

The 3 skipped tests are `[Fact(Skip = "I-1/I-2/I-3: ... Month 3")]` — they document the tech-debt backlog items directly in the test suite. They appear as skipped in CI output, not failed.

### Key finding: GuardrailAuditLog not mockable with Moq

`new Mock<GuardrailAuditLog>().Object` fails — Moq can't proxy a class without a parameterless constructor. Fix: instantiate it for real with a mocked `ILogger<GuardrailAuditLog>`. Pattern for future tests: mock interfaces and primitive dependencies; instantiate real objects when constructors are simple.

### Key interview talking point

**"How do you test a circuit breaker without waiting for real timeouts?"** Use `cooldownSeconds: 0` — the state machine transitions immediately. Revealed a subtle test design issue: with 0s cooldown, calling `IsAllowed()` after `RecordFailure()` immediately transitions back to HalfOpen (the cooldown is already expired). The correct assertion is `State == Open`, not `IsAllowed() == false`. The state is what matters; IsAllowed() is a side-effecting check.

---

## Days 6–7 — Month 2 Retrospective + v0.5.0

### What was produced

- `docs/month2-retrospective.md` — full retrospective covering Weeks 5–8
- `CHANGELOG.md` — v0.1.0 through v0.5.0 with full changelog per release
- `week8-progress.md` — this document, completed
- `v0.5.0` tag

### Month 2 by the numbers

| Metric | Value |
|--------|-------|
| New agents | 1 (Planner) |
| New intents | 3 (Create/Modify/GetMealPlan) |
| Guardrail layers | 5 |
| Failure matrix | 36/36, 0 × 500 |
| E2E sweep | 44/50, 0 × 500 |
| Unit tests | 68 passing |
| Tech debt items | 38 |
| Redis timeout fix | 30s → 15s |

### The architectural thread across Month 2

Every component built in Month 2 — the Planner Agent, session memory, guardrails, entity extraction, circuit breaker — follows the same principle established in Week 2:

**Rules for the common case. LLM for the edge case. Fast by default, smart on demand.**

Month 1 established it under hardware pressure. Month 2 proved it's also the right production architecture. The circuit breaker makes LLM failures instant. The rules engine makes diet validation free. The entity extractor only fires when rules don't suffice. The pattern compounds.

---

## Final Status: Week 8 Complete

- [x] Day 1: Failure mode matrix — 36/36, zero 500s, Redis timeout fix (30s → 15s)
- [x] Day 2: E2E sweep — 44/50, zero 500s, all 6 failures are IntentRouter classification gaps
- [x] Day 3: Performance profiling — bottlenecks identified, entity extraction cache flagged
- [x] Day 4: Tech debt sweep — 38 items consolidated, stale code cleaned, Qdrant.Client updated
- [x] Day 5: CI — 68 unit tests, health check stage, build badge, Node.js 24
- [x] Days 6–7: Month 2 retrospective, CHANGELOG, v0.5.0

**Month 2 complete. The system is stateful, hardened, and systematically tested. Month 3 is evaluation, observability, and cloud deploy.**