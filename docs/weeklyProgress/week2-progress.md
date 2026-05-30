# ChefAgent — Week 2 Progress Log (Week 2)

**Date:** May 25, 2026  
**Goal:** Add payload filtering, LLM re-ranking, negation handling, and query expansion to the search pipeline  
**Status:** ✅ Filtering complete | ✅ Negation handling complete | ⚠️ Re-ranker & query expansion built but hardware-limited

---

## What We Built

Extended the search pipeline from pure vector search to a multi-stage retrieval system with query preprocessing, structured filtering, negation handling, and LLM-based re-ranking/expansion.

```
Week 1:  Query → Embed → Qdrant (vector search) → Results

Week 2:  Query → Parse negation → Expand if abstract (LLM) → Embed cleaned query
              → Qdrant (vector search + payload filter) → Filter negation violations
              → Re-rank (LLM) → Results
```

---

## 1. Payload Filtering (Qdrant)

### What it does

Adds optional hard filters on `ingredient_count` and `step_count` to the vector search. Qdrant applies these _before_ computing similarity, so only matching recipes are considered as candidates.

### Changes made

**`RecipeSearchPlugin.cs`** — Added optional parameters and Qdrant filter construction:

- New parameters: `int? maxIngredients`, `int? maxSteps`
- Builds a `Filter` with `FieldCondition` using `Range { Lte = value }` when parameters are provided
- Passes filter to `QdrantClient.SearchAsync`
- Added logging when filters are active

**`Program.cs`** — Updated request DTO and endpoint:

- `RecipeSearchRequest` now includes `MaxIngredients`, `MaxSteps`, and `Rerank` fields
- Endpoint passes all parameters through to the plugin

### Test results — "simple chicken dinner"

| #   | Without Filter (baseline) | Ingredients | With maxIngredients: 6 | Ingredients |
| --- | ------------------------- | ----------- | ---------------------- | ----------- |
| 1   | Easy Chicken Dinner       | 3           | Easy Chicken Dinner    | 3           |
| 2   | One Dish Chicken Dinner   | **9**       | Easy Chicken Surprise  | 5           |
| 3   | Easy Chicken Surprise     | 5           | Super Quick Chicken    | 5           |
| 4   | Light 'N Easy Casserole   | **11**      | Sunday Dinner          | 4           |
| 5   | Chicken Dinner In Foil    | **12**      | Easy Chicken Casserole | 4           |

**Observation:** Filter removed 9, 11, and 12-ingredient recipes. Replacements (4–5 ingredients) are arguably _more_ relevant to "simple chicken dinner" than the originals. Relevance scores dropped slightly (0.71 → 0.70 for position 3) — acceptable tradeoff.

### Key insight — When filtering hurts

Filters are **hard cutoffs**, not soft preferences. Setting `maxIngredients: 4` would exclude a 5-ingredient recipe that's a perfect match. Unlike vector similarity (which degrades gracefully), a filter at N vs N+1 is a binary wall. This means:

- Filters work best when user intent aligns with the structural constraint ("simple" → few ingredients)
- Filters are wrong for subjective queries ("best chicken dinner" doesn't imply few ingredients)
- The orchestrator agent (future) should decide _when_ to apply filters based on query analysis

---

## 2. LLM Re-Ranker

### What it does

Takes the top candidates from vector search and asks `llama3.2` (via Ollama) to judge true relevance. Vector search finds candidates by semantic similarity; the re-ranker applies _reasoning_ — understanding that "simple" means few steps, or that "Mexican food" needs Mexican ingredients, not just spicy ones.

### Architecture

```
Qdrant returns N+5 candidates (vector similarity order)
    ↓
RecipeReranker.RerankAsync()
    ↓
Builds prompt: query + candidate titles + ingredients (first 5 each)
    ↓
Calls Ollama /api/chat (llama3.2, stream: false)
    ↓
Parses JSON response: [{index, score}, ...]
    ↓
Returns top N reordered by LLM relevance score
```

### Files created

**`RecipeReranker.cs`** — New class with:

- `RerankAsync()` — orchestrates the re-ranking flow
- `BuildPrompt()` — constructs the LLM prompt with candidates (title + up to 5 ingredients each)
- `CallOllamaAsync()` — calls Ollama's `/api/chat` endpoint with `stream: false`
- `ParseRanking()` — extracts JSON array from LLM response, handles markdown fences
- Graceful fallback: if LLM fails (timeout, bad JSON), returns original vector search order
- Missing candidate handling: if LLM only ranks some candidates, unranked ones get score 0.0

### Design decisions

**Title + ingredients only (not directions):** Directions contain useful signal (cooking method, time) but add significant token count. On constrained hardware, keeping prompts small is critical. Title + ingredients is enough for the LLM to judge relevance for most query types.

**Structured JSON output:** The prompt asks for `[{"index": 0, "score": 0.95}, ...]` — no explanation, no markdown. This makes parsing reliable and reduces output tokens.

**Opt-in via request parameter:** Re-ranking is gated behind `"rerank": true` in the request DTO. Normal searches skip the LLM call entirely.

### Hardware constraint

| Factor      | Detail                                                           |
| ----------- | ---------------------------------------------------------------- |
| Machine     | 8GB RAM MacBook                                                  |
| Ollama mode | CPU-only (`size_vram: 0`)                                        |
| Model       | llama3.2 (3.2B params, Q4_K_M quantization)                      |
| Issue       | Re-ranking 10 candidates timed out at both 100s and 300s         |
| Root cause  | CPU inference on 3.2B model with ~1000+ token prompt is too slow |

**Mitigations applied:**

1. Reduced candidate pool from 20 → `maxResults + 5`
2. Limited ingredients shown per recipe to 5
3. Made re-ranking opt-in (`rerank: false` by default)
4. Increased HttpClient timeout to 5 minutes

**Status:** Re-ranker is architecturally complete and correctly wired (logs confirm it's called, graceful fallback works). Not usable for interactive queries on current hardware. Options for later:

- Run on a machine with GPU / more RAM
- Use Google Colab for LLM calls (like we did for embeddings in Week 1)
- Switch to a smaller/faster model
- Only re-rank for query categories where vector search is known to be weak

---

## 3. Query Preprocessor (Negation + Abstraction)

### What it does

Handles two query types that pure vector search fundamentally can't solve, combined in a single `QueryPreprocessor.cs` class:

1. **Negation** ("pasta without tomatoes") — embeddings capture _topic_, not _logic_. "Pasta without tomatoes" and "pasta with tomatoes" produce nearly identical vectors. Fix: parse negation pre-search, filter violations post-search.
2. **Abstraction** ("something warm and comforting") — the query is semantically valid but too vague for ingredient/title matching. Fix: expand to concrete food terms via LLM before embedding.

### Negation handling (no LLM — works on current hardware ✅)

**Pre-search:** Regex parses negation patterns from the query:

- Patterns recognized: `without`, `no`, `excluding`, `exclude`, `free of`, `hold the`, `skip the`, `minus`
- Also handles `X-free` pattern (e.g., "gluten-free", "dairy-free")
- Multi-exclusions supported: "without tomatoes and onions" → excludes both
- Returns cleaned query (negation stripped) + list of excluded terms

**Post-search:** After Qdrant returns results, checks each recipe's ingredient list against excluded terms. Any recipe containing an excluded ingredient is removed.

**Buffer for removals:** `fetchLimit` increases by 10 when negation is detected, so filtered-out recipes don't leave you with too few results.

### Test results — "pasta without tomatoes"

| #   | Week 1 (no negation handling) | Week 2 (with negation handling) |
| --- | ----------------------------- | ------------------------------- |
| 1   | Pasta Tomato Soup             | **Homemade Pasta** (0.71)       |
| 2   | **Pasta With Tomatoes** ❌    | **Fettuccine Alfredo** (0.71)   |
| 3   | Pasta Fagioli                 | **Creamy Pasta Salad** (0.71)   |
| 4   | Baked Ziti                    | **Spaghetti Salad** (0.70)      |
| 5   | Pasta Primavera               | **Pasta Marco Polo** (0.70)     |

**Week 1:** Returned tomato-heavy recipes — vector search treated "without tomatoes" as "about tomatoes."
**Week 2:** All 5 results are tomato-free pasta dishes. The negation handler parsed out "tomatoes", searched for just "pasta", then filtered any result containing tomatoes in the ingredient list.

### Query expansion (LLM-dependent — built but hardware-limited ⚠️)

**How it works:**

1. `IsAbstract()` checks query against signal words: "something", "anything", "feeling like", "cozy", "warm", "comforting", "quick", "easy", "fancy", etc.
2. If abstract, sends a short prompt to Ollama asking it to rewrite as concrete food terms
3. Example: "something warm and comforting" → "soup, stew, chili, casserole, pot roast"
4. The expanded query is what gets embedded and sent to Qdrant

**Prompt design:** Few-shot with 4 examples covering different abstract categories (comfort, speed, impressiveness, health). Asks for concrete terms only, no explanation.

**Status:** Code complete, opt-in via `"expand": true`. Same CPU inference bottleneck as the re-ranker — times out on current hardware. Graceful fallback: if expansion fails, uses original query.

### Design decisions

**Single class for both:** `QueryPreprocessor.cs` handles negation (pre + post search) and expansion (pre-search only). Both are query-level concerns that happen around the core vector search.

**Negation is always-on, expansion is opt-in:** Negation parsing is pure regex — zero cost. Expansion requires an LLM call, so it's gated behind the `expand` flag like re-ranking.

**Cleaning the query before embedding:** Critical insight — if you embed "pasta without tomatoes", the vector is pulled toward tomato-related recipes. By stripping the negation and embedding just "pasta", the vector search finds pasta recipes in general, then post-filtering removes tomato ones.

---

## 4. Pipeline Summary

The search pipeline now has five stages:

| Stage            | Method                   | Speed        | Intelligence                    | Status                         |
| ---------------- | ------------------------ | ------------ | ------------------------------- | ------------------------------ |
| Negation parsing | Regex pattern matching   | Instant      | Detects "without X" patterns    | ✅ Always-on                   |
| Query expansion  | Ollama llama3.2          | Slow (CPU)   | Rewrites vague → concrete terms | ⚠️ Opt-in, hardware-limited    |
| Vector search    | Qdrant cosine similarity | Fast (~50ms) | Semantic matching               | ✅ Always-on                   |
| Payload filter   | Qdrant FieldCondition    | Fast (~0ms)  | Structural constraints          | ✅ Opt-in via params           |
| Negation filter  | Ingredient list check    | Instant      | Removes excluded ingredients    | ✅ Auto when negation detected |
| LLM re-rank      | Ollama llama3.2          | Slow (CPU)   | Intent understanding            | ⚠️ Opt-in, hardware-limited    |

---

## 5. Files Changed / Created

```
src/agents/RecipeAgent/
├── RecipeSearchPlugin.cs    # Updated: payload filtering, re-ranker integration, preprocessor integration, opt-in flags (rerank, expand)
├── RecipeReranker.cs        # New: LLM-based re-ranking via Ollama
├── QueryPreprocessor.cs     # New: negation parsing/filtering + query expansion via Ollama

src/api/
├── Program.cs               # Updated: reranker + preprocessor DI registration, request DTO (MaxIngredients, MaxSteps, Rerank, Expand), HttpClient timeout
```

---

## Concepts Learned

| Concept                        | What It Means                                                                                               |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------- |
| Payload filtering              | Combining vector similarity with structured field constraints (Qdrant Filter + FieldCondition)              |
| Hard cutoff vs soft preference | Filters are binary walls; vector similarity degrades gracefully — choose accordingly                        |
| Two-stage retrieval            | Fetch wide (candidates) then narrow (re-rank) — retrieval is fast/dumb, reasoning is slow/smart             |
| Graceful degradation           | Re-ranker falls back to vector order on failure — the system never breaks, just gets less smart             |
| Opt-in complexity              | Gate expensive features behind flags — fast by default, smart on demand                                     |
| CPU vs GPU inference           | 3.2B model on CPU can't handle interactive re-ranking; same model on GPU would be fine                      |
| Negation blindness             | Embeddings capture topic, not logic — "with X" and "without X" produce nearly identical vectors             |
| Query cleaning                 | Strip negation before embedding so the vector isn't polluted by the excluded term                           |
| Post-retrieval filtering       | Check results against constraints after search — cheap and effective for negation                           |
| Query expansion                | Rewrite vague/abstract queries into concrete search terms before embedding                                  |
| Pre-search vs post-search      | Some problems are solved before search (expansion, cleaning), others after (negation filtering, re-ranking) |

---

## What's Next (Week 2 Remaining)

- [x] ~~Day 4: Query expansion (abstract queries) + negation handling (post-retrieval filtering)~~ — Done (same session as Day 2-3)
- [ ] Day 5: Search quality test harness — measure improvement over Week 1 baseline
- [ ] Day 6-7: Clean up, add logging at each pipeline stage, update README, commit and push

---

_The gap between "search engine" and "intelligent agent" is the reasoning layer. Today we added four of them — two that work fast (negation, filtering) and two that work smart (re-ranking, expansion). On better hardware, all four run together. On 8GB, the fast ones carry the weight while the smart ones wait in the wings._
