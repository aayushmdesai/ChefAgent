# ADR-006: Planner Agent Architecture

**Status:** Accepted  
**Date:** May 2026 (Week 5)  
**Decision Makers:** Aayush Desai  

---

## Context

Month 2 adds a Planner Agent — the first stateful agent in ChefAgent. The Recipe Agent and Diet Agent are stateless: every request is independent, no memory between calls. The Planner must:

1. Generate a 7-day meal plan by calling the Recipe and Diet agents repeatedly
2. Persist the plan between requests so the user can iteratively modify it
3. Allow the user to swap individual meal slots through natural language ("swap Tuesday dinner to something with pasta")
4. Support variable meal slot configurations (dinner only, breakfast + dinner, all three meals)
5. Enforce variety constraints (no consecutive protein or cuisine repeats)

This introduced three design problems that didn't exist in Weeks 1–4:

- **Where does state live?** The plan must survive server restarts and scale to multiple instances.
- **How do you scope state to a user?** ChefAgent has no auth in MVP.
- **How do you enforce variety without an LLM?** Querying the LLM per slot on 8GB CPU is too slow.

---

## Decisions

### 1. Redis for session state (not in-memory, not a database)

**Decision:** Persist meal plans in Redis under key `session:{sessionId}:plan` with a 7-day TTL.

**Alternatives considered:**

| Option | Why rejected |
|--------|-------------|
| In-memory (Dictionary) | Dies on restart, doesn't scale to multiple server instances |
| SQLite / Postgres | Overkill for MVP — full relational DB for one JSON blob per user |
| File system | Can't scale, no TTL, messy cleanup |
| Redis | Survives restarts, O(1) reads, TTL-based expiry, already in docker-compose |

**Why 7-day TTL?** A user generates a plan Sunday evening and returns Monday morning — 24 hours is too short. 7 days covers a full plan cycle. Plans expire automatically; no cleanup job needed.

**Plan and profile stored in separate keys** (`session:{id}:plan` and `session:{id}:profile`). Profile changes more frequently than the plan; co-locating them would require deserializing and re-serializing the full plan on every profile update.

---

### 2. `sessionId` as user identity (no auth in MVP)

**Decision:** Client generates a UUID on first load, stores it in `sessionStorage`, and sends it with every `/chat` request. Server keys Redis on it. No user accounts, no auth.

**Consequences:**
- Zero auth complexity in MVP
- Plan is lost if the user clears browser storage — acceptable for MVP
- Trivially upgradeable: swap `sessionId` for a real user ID when auth is added in Month 3

**Profile persistence design:** Two options considered:

- **Option A (MVP):** Client sends profile with every request. Simple, already wired from Week 4's `ProfileSidebar`.
- **Option B (Month 2):** First request sets the profile in Redis; subsequent requests load it automatically.

Option A chosen for MVP — profile storage methods (`SaveProfileAsync`, `GetProfileAsync`) are stubbed in `SessionStore` but not wired. One-line change to activate in Month 2.

---

### 3. Keyword-based variety enforcement (not LLM)

**Decision:** Infer `ProteinCategory` and `CuisineTag` from recipe title + ingredients using keyword dictionaries. Enforce variety by checking the last 2 days' categories before selecting a candidate.

**Why not LLM for variety?**
- Each slot requires an embedding call (~2s on CPU) already
- Adding an LLM classification call per slot = 2× latency per slot = 42+ seconds for a 7-day, 3-slot plan
- Keyword matching covers the common cases (poultry, beef, fish, pork, vegetarian) at zero cost

**Known limitation:** Recipes that don't match any keyword get `ProteinCategory = null`. Two consecutive `null` recipes are allowed — "unclassified" is not a category to avoid repeating.

**`CuisineFalsePositives` safe-list:** Same pattern as `DairyFalsePositives` in the Diet Agent (Week 3). Ingredient-level keyword matching produces false cuisine tags (e.g., `"pasta ready"` in an ingredient triggers the `italian` keyword). Safe-list checked before main keyword scan.

---

### 4. Generate-once, read-many performance model

**Decision:** Plan generation is expensive (7–21 vector searches depending on slot count). Accept this cost once; make every subsequent read instant via Redis.

**Performance profile (8GB RAM, CPU-only):**

| Operation | Latency |
|-----------|---------|
| Plan generation (7 dinners) | ~14–20s |
| Plan generation (7 × 3 meals) | ~45s |
| Plan read from Redis | ~1ms |
| Single slot modify (Redis load + 1 search + Redis save) | ~2s |

**Why not generate asynchronously?** Async generation would require a polling mechanism or WebSocket — significant complexity for MVP. The synchronous model is simpler and the latency is acceptable for a one-time generation.

---

### 5. Immutable record update for plan modification

**Decision:** C# `init`-only records can't be mutated in place. `ModifyPlanAsync` uses `Select` + `with` to rebuild only the changed node; everything else passes through unchanged.

```csharp
var updatedDays = plan.Days.Select(d =>
    d.Day != targetDay ? d :
    d with { Slots = d.Slots.Select(s =>
        s.SlotName != targetSlot ? s : newSlot).ToList() }
).ToList();
```

This is the standard immutable update pattern for C# records. It makes the modification auditable — the original slots are never destroyed, just replaced.

---

### 6. Query rotation for generation diversity

**Decision:** Each slot has 7 predefined queries (one per day of the week). Rotated by day index: `queries[dayIndex % queries.Length]`.

**Why not the same query every day?** Sending `"easy weeknight chicken dinner"` 7 times to Qdrant returns the same top-5 results every day. Different queries surface different regions of the vector space.

**Why not LLM-generated queries?** On 8GB CPU, LLM query generation per slot stacks to 60+ seconds for a 21-slot plan. Predefined queries with rotation give acceptable variety at zero cost.

---

### 7. Two variety selection methods (generation vs modify)

**Decision:** Separate `SelectWithVariety` (generation) and `SelectWithVarietyForModify` (modification) methods, because the available context differs:

| | Generation | Modification |
|--|-----------|-------------|
| Context available | Days planned so far (lookback only) | Full plan (both directions) |
| Neighbor check | Last 2 days | Closest 2 days by index distance |
| Excludes current recipe | N/A | Yes — `excludeTitle` prevents returning the same recipe |

During generation, future days don't exist yet — only lookback is possible. During modification, the full plan is loaded from Redis — both neighbors are available, and the current recipe must be excluded from candidates.

---

### 8. Multi-slot planning via intent extraction

**Decision:** `IntentRouter` extracts slot names from the user message. "plan my meals" → `["breakfast", "lunch", "dinner"]`. "plan my dinners" → `["dinner"]`. Extracted slots passed to `PlanConstraints.MealSlots`.

**Signal words:**
- `"breakfast"` → add breakfast slot
- `"lunch"` or `"lunches"` → add lunch slot  
- `"dinner"` or `"dinners"` → add dinner slot
- No slot words detected → default to all three (user said "meals" or "week")

---

## Consequences

**Positive:**
- First stateful agent in the system — architectural inflection point
- Redis read latency (~1ms) makes plan retrieval and modification feel instant after generation
- Variety enforcement works without LLM — zero additional latency per slot
- Multi-slot planning works from natural language with no code change required
- `excludeTitle` guarantee: swap always returns a different recipe than the current one

**Negative:**
- 21-slot (breakfast + lunch + dinner) plan generation takes ~45s on CPU — noticeable wait
- Keyword variety enforcement has limited coverage — ~60% of recipes get a protein category, ~20% get a cuisine tag
- No conversation history — "swap it again" has no context; user must be specific
- `sessionId` in `sessionStorage` is lost on browser close — plan lost without auth

**Deferred to Month 2/3:**
- Redis profile persistence (stubbed, not wired)
- Async plan generation with progress indicator
- LLM-based intent classification for ambiguous modify commands ("change the complicated one")
- Conversation history for multi-turn context
- `GetMealPlan` intent ("show me my plan")

---

## Test Results

15 test cases across generation, session persistence, modification, edge cases, and performance.

**14/15 passed. 0 system bugs. 1 test design issue.**

TC13 (variety after adversarial repeated swaps) failed because the test exhausted variety candidates — not a system bug. TC02 (variety on fresh generation) passed with 0 consecutive protein repeats.

Generation latency: 14.2s for 7-dinner plan on warmed Ollama — under the 30s target.

---

## Related ADRs

- ADR-001 — Orchestration Framework (Semantic Kernel)
- ADR-002 — Vector Database (Qdrant)
- ADR-003 — LLM Provider (Ollama + llama3.2)
- ADR-004 — Diet Agent Architecture (rules engine + LLM fallback)
- ADR-005 — Orchestrator Design (intent classification, agent routing)