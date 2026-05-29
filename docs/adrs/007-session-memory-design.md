# ADR-007: Session Memory Design

**Status:** Accepted  
**Date:** May 2026 (Week 6)  
**Decision Makers:** Aayush Desai  

---

## Context

Week 5 shipped the Planner Agent with Redis-backed plan persistence. But the system still had no memory of conversations — every `/chat` request was independent. This created three UX gaps:

1. **Reference blindness:** "is the first one vegan?" had no referent — the system didn't know what "the first one" was.
2. **Profile amnesia:** Every request had to re-send the dietary profile. Toggling "vegetarian" in the sidebar was lost the moment the user didn't explicitly include it in the next message.
3. **Implicit constraint loss:** "I cannot have dairy, find me dinner" — the constraint was expressed naturally but the rules engine only caught explicit vocabulary like "dairy-free". The constraint was silently dropped.

Week 6 closes all three gaps. The core question: how do you make a stateless HTTP API feel like it remembers you?

---

## Decisions

### 1. Structured conversation history over raw message text

**Decision:** Store `ConversationEntry` records (role, content, intent, recipeTitles, planId) in a Redis list — not raw message strings.

**Alternatives considered:**

| Option | Why rejected |
|--------|-------------|
| Raw message strings | Resolving "the first one" requires sending history to LLM — slow, probabilistic, expensive |
| Full `RecipeDocument` objects | ~2KB per recipe × 5 recipes × 20 turns = 200KB just for history — too bloated |
| Structured entries with titles only | O(1) deterministic resolution — "the first one" → `history[-1].RecipeTitles[0]` |

**Why `Intent` on assistant entries only?** The resolver needs to know what domain the previous turn was about. `"is it vegan?"` after `SearchRecipe` → the `"it"` is a recipe. After `CreateMealPlan` → the `"it"` is a plan day. User entries don't need intent — the resolver only looks back at assistant entries.

**Why `RecipeTitles` and not recipe IDs?** Titles are what the user sees. Resolving "the first one" to a title is what gets injected into the search query — Qdrant searches by text, not by ID.

---

### 2. Redis list for history (not a JSON array string key)

**Decision:** History stored as a Redis list (`RPUSH` + `LTRIM`) under `session:{sessionId}:history`.

**Why not a JSON array in a string key?**

History is append-heavy: one entry per turn. A JSON string key requires:
- Deserialize full array
- Append one entry
- Re-serialize and write back

That's O(n) per turn. Redis list gives O(1) `RPUSH` + O(1) `LTRIM`. With 20 turns stored, the difference is small — but the pattern is correct and scales cleanly.

**Sliding window:** Store last 20, read last 10 by default. The buffer of 10 means future features (LLM entity extraction, richer context) can fetch more without losing entries. Reading 6 for reference resolution is the current default.

**TTL refreshed on every append** — active sessions never expire mid-conversation. A session used 6 days ago resets to 7 days on the next message.

---

### 3. Deterministic reference resolution (no LLM)

**Decision:** Detect reference words (`"it"`, `"the first one"`, `"again"`, etc.) via a `HashSet`, load last 6 history entries, resolve to concrete entities using structured data.

**Resolution logic:**
- Reference detected + last intent was `SearchRecipe` + recipe titles exist → inject `RecipeTitles[0]` (or `[1]` for "second") as `SearchQuery`, override intent to `ValidateDiet`
- Reference detected + last intent was `ModifyMealPlan` + "again" → keep `ModifyMealPlan` intent, ask clarifying question for day

**Why not LLM for resolution?** The structured history makes this deterministic. "The first one" after a recipe search is always `RecipeTitles[0]`. LLM resolution would cost ~30s for something answerable in ~1ms.

**Key ordering insight:** Resolution runs after saving the user message to history but before routing to handlers. This ensures the handler receives the resolved query, not the raw reference.

---

### 4. Union merge profile persistence

**Decision:** On every `/chat` request: load stored profile from Redis, union merge with request profile (request wins on conflicts), save merged profile back, use merged for all agent calls.

**Why union merge, not replacement?**

If stored has `"vegetarian"` and the current message extracts `"nut-free"`, both should apply. Replacement would silently drop `"vegetarian"` on any turn where it wasn't re-sent.

**Why save back on every turn?**

The merged result becomes the new stored profile. Turn 3's `"nut-free"` extraction persists so Turn 4 doesn't need to re-extract it. Constraints accumulate across the conversation without the user re-stating them.

**Separate keys for plan and profile** (`session:{id}:plan`, `session:{id}:profile`) — same decision as ADR-006. Profile changes more frequently; co-locating would require deserializing the full plan on every profile update.

---

### 5. LLM entity extraction as a gated fallback (not default)

**Decision:** Rules extract explicit dietary vocabulary (`"gluten-free"`, `"vegan"`). LLM extracts implicit natural language constraints only when:
1. Rules extracted nothing
2. Message contains an implicit constraint signal (`"i cannot"`, `"plant-based"`, `"i follow"`, etc.)
3. A 90s timeout budget is available

**Why gated?** LLM extraction costs ~30–60s on Codespaces CPU. Running it on every request would make the system unusable. The heuristic signal check is ~0ms and fires only for natural language constraint expressions.

**Why pass history to the LLM?** A single message lacks context. `"something plant-based"` on Turn 5 is more confidently resolved when the LLM can see `"I've been vegetarian for years"` from Turn 2. History also prevents re-extraction — the known profile is passed explicitly so the LLM only returns NEW constraints.

**Confidence field:** The LLM returns `"confidence": "high" | "medium" | "low"`. `low` → don't apply. `high/medium` → merge into profile. Prevents hallucinated constraints from being saved.

**Known limitation:** Over-extraction — the LLM sometimes extracts constraints beyond what was stated (`"plant-based"` → also extracts nuts, gluten). Prompt tuning deferred to Month 3.

---

### 6. Contraction normalization before signal matching

**Decision:** `NormalizeContractions` runs once on lowercased input before all signal word matching. Expands contractions and no-apostrophe variants (`"whats"` → `"what is"`, `"cant"` → `"cannot"`, `"im "` → `"i am "`).

**Why a preprocessing step rather than signal variants?**

Adding contracted variants to every signal set is whack-a-mole — every new signal phrase needs multiple variants maintained forever. A single normalization pass benefits all signal sets automatically, including future ones.

**Acknowledged limitation:** Not exhaustive. Users type `"wuts"`, `"lemme see"`, `"gimme"`. The comment in code says so explicitly and points to the Month 2 LLM classifier as the real fix. This is honest engineering — document what the code does and doesn't cover.

---

### 7. `GetMealPlan` as a distinct intent

**Decision:** `GetMealPlan` is a separate intent from `CreateMealPlan`. Signal words: "show me my plan", "what is my plan", "my plan", "my weekly plan", etc. Handler is a pure Redis read (~1ms), no agent calls.

**Why separate from `CreateMealPlan`?**

"My plan" without `GetMealPlan` would route to `SearchRecipe` (rules-default) and return recipe results instead of the stored plan. Separating read from generate as distinct intents avoids re-generating a plan the user already has.

**Ordering in `ClassifyIntent`:** `GetMealPlan` checked before `CreateMealPlan` — both contain "my plan" and "plan", but retrieval is more specific than generation.

---

### 8. Graceful stateless fallback on Redis failure

**Decision:** Every Redis call in the Orchestrator is wrapped in try/catch. Redis failure → log warning → continue as stateless. Never propagate to 500.

**Failure behavior per operation:**

| Operation | Redis down behavior |
|-----------|-------------------|
| `AppendMessageAsync` (Step 1) | Skip — history not saved this turn |
| `AppendMessageAsync` (Step 5) | Skip — assistant response not saved |
| `GetHistoryAsync` | Return empty list — no reference resolution |
| `LoadAndMergeProfileAsync` (get) | Use request profile only |
| `LoadAndMergeProfileAsync` (save) | Log warning — profile not persisted |
| `GetPlanAsync` | Return null — treat as no plan |
| `SavePlanAsync` | Log warning — plan generated but not persisted |

**Key insight:** `SavePlanAsync` is split out of the main `HandleCreateMealPlanAsync` try/catch. A Redis failure during save should not discard a successfully generated plan — the plan is returned to the user even if it can't be persisted.

**Verified:** Redis stopped mid-session → `"find me a chicken dinner"` returns 5 recipes with no exception. Warnings logged, stateless degradation confirmed.

---

## Consequences

**Positive:**
- "Is the first one vegan?" resolves correctly in ~1ms — zero LLM calls
- Dietary profile persists across turns and page reloads — user never re-configures
- "I cannot have dairy" correctly extracts `dairy-free` constraint via LLM fallback
- "What's my plan?" returns stored plan in ~2ms — no agent calls
- Redis failure degrades to stateless mode — no 500s

**Negative:**
- LLM entity extraction takes 30–90s when it fires — blocks the response
- Over-extraction: LLM sometimes adds hallucinated constraints beyond what was stated
- Query cleaning for implicit prefixes ("i cannot have dairy") is string-replacement based — brittle for novel phrasing
- Contraction normalization is not exhaustive — arbitrary phrasing still fails

**Deferred to Month 3:**
- LLM entity extraction prompt tuning (reduce over-extraction)
- Async LLM extraction with background profile update (don't block response)
- Full LLM intent classifier replacing rules-default
- `_ollamaUrl` / `_chatModel` rename to `_llmUrl` / `_llmModel` (abstraction cleanup)
- Proper NLP-based query cleaning for implicit constraint prefixes

---

## Test Results

15 test cases across conversation history, profile persistence, GetMealPlan, LLM entity extraction, and edge cases.

**15/15 passed. 0 system bugs.**

TC10 (LLM dairy extraction) initially failed due to test design issues (log timing + query cleaning bug found during investigation). Query cleaning bug fixed during Day 5. TC15 (full 6-step end-to-end flow) passed: profile → search → reference check → plan → get plan → swap.

---

## Related ADRs

- ADR-001 — Orchestration Framework (Semantic Kernel)
- ADR-002 — Vector Database (Qdrant)
- ADR-003 — LLM Provider (Ollama + llama3.2)
- ADR-004 — Diet Agent Architecture (rules engine + LLM fallback)
- ADR-005 — Orchestrator Design (intent classification, agent routing)
- ADR-006 — Planner Agent Architecture (stateful generation, Redis persistence, variety enforcement)