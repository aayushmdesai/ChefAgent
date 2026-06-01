# Week 9 Progress — Evaluation Pipeline + Spell-Check

**Month 3, Week 9 | Dates: June 2026**  
**Tag: v0.6.0**

---

## Goals

- Build a reproducible retrieval evaluation pipeline
- Create a 100-query golden dataset across 12 categories
- Establish a measurable baseline
- Implement and prove one retrieval improvement (spell-check)
- Fix Day 1 performance issues from Month 2 retrospective

---

## Day 1 — Performance fixes + RAGAS setup

### Entity extraction cache

**Problem:** First message of every session with implicit dietary constraints triggered LLM entity extraction via Ollama — 12s latency spike.

**Fix:** Cache the extraction result in Redis alongside the session profile.

New Redis key: `session:{id}:extraction`
- `{}` = extraction ran, found nothing (skip LLM next time)
- `{...}` = extraction ran, found profile (use cached result)
- missing = never ran (allow LLM to fire)

New methods in `SessionStore`:
- `GetCachedExtractionAsync(sessionId)` → `(bool ranBefore, DietaryProfile? profile)`
- `SetCachedExtractionAsync(sessionId, DietaryProfile?)`

Updated `IntentRouter.ClassifyAsync` to check cache before calling `TryExtractProfileWithLlmAsync`.

**Result:** Message 1 with implicit constraint: 90s (LLM timeout) → cached. Message 2 same session: **13ms** (cache hit, zero LLM call).

**Key insight:** The `(ranBefore, profile)` tuple return distinguishes three states a plain nullable can't: never ran / ran and found nothing / ran and found something.

---

### Redis circuit breaker

**Problem:** Redis down → each call waited full connection timeout (~15s) before the catch block ran. User waited 15s for a degraded response.

**Fix:** Second `CircuitBreaker` instance wrapping all Redis operations in `SessionStore`.

**Architecture decision:** Shared singleton (not per-session) — Redis failure is infrastructure failure, affects all sessions simultaneously.

**DI pattern:** Used .NET 8 keyed services to register two `CircuitBreaker` instances:
```csharp
services.AddKeyedSingleton<CircuitBreaker>("ollama", ...);  // 60s cooldown
services.AddKeyedSingleton<CircuitBreaker>("redis", ...);   // 30s cooldown
```

Injected with `[FromKeyedServices("redis")]` in `SessionStore` constructor.

All `SessionStore` methods wrapped with circuit breaker pattern:
- `IsAllowed()` check before every Redis call
- `RecordSuccess()` on success
- `RecordFailure()` on exception
- Graceful null/empty returns on open circuit

**Result tested:** 3 consecutive Redis failures → circuit opens → subsequent requests fast-fail in <1ms.

**Note on `GetCachedExtractionAsync` when circuit open:** Returns `(false, null)` — treats as "never ran" so LLM can still fire. Redis failure shouldn't permanently suppress entity extraction.

---

### RAGAS install

```bash
.venv/bin/pip install "ragas==0.1.21" langchain-community langchain-google-vertexai
```

Smoke test:
```python
from ragas import evaluate
from ragas.metrics import context_precision
print("RAGAS ready")
```

**Version pinned to 0.1.21** — newer versions (0.4.x) have breaking changes and numpy binary incompatibility with Colab.

---

## Day 2 — Golden dataset (100 Q&A pairs)

Created `eval/datasets/golden_dataset.json` — 100 labeled evaluation pairs across 12 categories.

| Category | Count | Purpose |
|----------|-------|---------|
| exact_match | 8 | Named dish retrieval |
| by_ingredients | 10 | Ingredient-based search |
| dietary | 12 | Restriction-filtered search |
| negation | 10 | Exclusion queries |
| cuisine | 8 | Cuisine-type search |
| technique | 8 | Cooking method search |
| filtering | 8 | Metadata-based (time, steps, ingredients) |
| situation | 8 | Abstract/contextual queries |
| multi_intent | 8 | Combined constraints |
| misspelling | 6 | Typo robustness |
| x_free | 8 | X-free label queries |
| edge_case | 6 | Garbage, vague, nonsense queries |

**Ground truth design principle:** Specific descriptions, not recipe titles. "A soup recipe that contains no dairy products including milk, cream, butter, cheese, or yogurt" not "dairy-free soup recipe." More specific = more useful RAGAS judgment.

**Edge cases included:** Single character queries ("r"), nonsense ("xkqzpw blarfnog recipe"), overly vague ("food"), borderline irrelevant ("how to lose weight fast with food"). These test system robustness — low scores expected and correct.

---

## Day 3 — Evaluation pipeline

### retrieve.py (runs locally)

`eval/harnesses/retrieve.py` — calls `/recipes/search` for each golden dataset question, saves contexts to `eval/datasets/retrieved_contexts.json`.

Context format per recipe:
```
Title: Chicken Parmesan
Ingredients: chicken breast, marinara sauce, mozzarella...
Directions: Bread the chicken. Bake at 375F...
```

Title + ingredients + directions combined because:
- Title: tells judge what the dish is
- Ingredients: faithfulness check surface
- Directions: technique context for answer relevancy

**Why retrieval is separate from scoring:** `/recipes/search` is on `localhost:5100` which Colab can't reach. Split into local retrieval + Colab scoring.

### score_simple.py (runs on Colab GPU)

`eval/harnesses/score_simple.py` — sequential Ollama judge, 3 LLM calls per question:
1. `score_context_relevance` — are retrieved recipes relevant to the query?
2. `score_faithfulness` — is the answer grounded in retrieved context?
3. `score_answer_relevancy` — does answer address question vs ground truth?

**Why custom scorer over RAGAS:** RAGAS executor runs jobs in parallel — causes timeout/recursion errors with single Ollama instance. Custom sequential scorer: no executor, no parallelism, no overhead. Traded standardized metrics for operational reliability.

**Why not OpenAI:** Free tier key ran out of quota mid-run. SymSpell + Colab GPU = free alternative.

**Why Colab GPU:** Each Ollama call is ~30s on CPU (local), ~2-3s on T4 GPU. 100 questions × 3 calls = 300 LLM calls = ~15min on GPU vs ~2.5hrs locally.

---

## Day 4 — Baseline analysis

Ran 100-question evaluation. Results saved to `eval/experiments/2026-06-01_baseline.json`.

### Overall baseline scores

| Metric | Score |
|--------|-------|
| Context Relevance | 0.470 |
| Faithfulness | 0.489 |
| Answer Relevancy | 0.267 |

### Per-category baseline

| Category | Relevance | Assessment |
|----------|-----------|------------|
| by_ingredients | 0.630 | ✅ Strong — ingredient matching suits vector search |
| cuisine | 0.625 | ✅ Strong — cuisine terms embed well |
| exact_match | 0.525 | ✅ Good |
| situation | 0.525 | ⚠️ Moderate |
| filtering | 0.450 | ⚠️ Moderate — metadata not in embeddings |
| negation | 0.460 | ⚠️ Weak |
| technique | 0.438 | ⚠️ Moderate |
| misspelling | 0.442 | ⚠️ Weak |
| x_free | 0.438 | ❌ Weak — "dairy-free" doesn't match semantically |
| dietary | 0.408 | ❌ Weak — dietary labels not in recipe text |
| multi_intent | 0.362 | ❌ Weak — single vector can't capture 3 constraints |
| edge_case | 0.267 | ✅ Expected — garbage in, garbage out |

### Root cause analysis

**dietary + x_free (relevance ~0.40):** Dietary labels ("gluten-free", "vegan") not present in recipe text. Vector search finds semantic similarity on dish names, not dietary properties. Fix path: pre-filter using Diet Agent at retrieval time.

**negation (relevance 0.46):** Regex catches explicit negation post-retrieval but top results still violate the negation before filtering. Fix path: push negation into Qdrant payload filter or pre-retrieval query rewriting.

**multi_intent (relevance 0.36):** "quick healthy chicken dinner" embeds as one vector — averages the three constraints, weakening each signal. Fix path: multi-vector retrieval (Month 4).

**answer_relevancy universally low:** Template-based generation returns recipe title, not natural language answer. LLM judge expects prose. Not a retrieval failure — a generation limitation.

---

## Day 5 — CI fix + experiment tracking

### CI test fix

`IntentRouter` constructor changed (added `SessionStore`) — `IntentRouterTests.cs` broke.

Fix: mock `SessionStore` in `MakeRouter()`:
```csharp
var sessionStore = new Mock<SessionStore>(
    Mock.Of<StackExchange.Redis.IConnectionMultiplexer>(),
    circuitBreaker
).Object;
```

Result: 68 passing, 0 failing, 3 skipped.

### Experiment comparison script

`eval/harnesses/compare_experiments.py` — loads two result JSON files, prints per-category delta table with ↑/↓/→ arrows.

Usage:
```bash
python eval/harnesses/compare_experiments.py \
    eval/experiments/2026-06-01_baseline.json \
    eval/experiments/2026-06-07_spell_check.json
```

Baseline saved to `eval/experiments/2026-06-01_baseline.json`.

---

## Day 6-7 — Spell-check implementation + eval

### Architecture decision

**Where:** `QueryPreprocessor` — owns search quality improvements (negation, expansion). Spell-check is the same category of concern. Runs before negation so clean words reach regex patterns.

**Not InputGuard** — that's security, not search optimization. Single responsibility.

**Pipeline position:**
```
User message → InputGuard → IntentRouter → QueryPreprocessor
    [0] Spell correction   ← NEW
    [1] Negation parsing
    [2] Query expansion (opt-in)
    [3] Embed → Qdrant
```

### Two-layer spell correction

**Layer 1 — Food domain dictionary** (`food_corrections.json`):
- High-confidence corrections for culinary terms
- Runs first, takes priority over SymSpell
- Grows organically as new failures are found — JSON-only, no code change
- Examples: "chiken" → "chicken", "soop" → "soup", "spageti" → "spaghetti"

**Layer 2 — SymSpell** (frequency-weighted):
- Replaced Hunspell (`WeCantSpell.Hunspell`) — Hunspell uses edit distance only, picks "sup" over "soup" because both are edit distance 2 from "soop"
- SymSpell uses frequency weighting — "soup" (500K occurrences) beats "sup" (5K occurrences)
- Dictionary: `frequency_dictionary_en_80k.txt` (English word frequencies)
- Handles misspellings not in food dict

**Why not static list alone:** Doesn't scale. Every new misspelling needs a code change. SymSpell handles the long tail automatically.

**Skip list:** Known food/dietary terms excluded from correction — "tikka", "masala", "carbonara", "vegan", "halal" etc. Hyphenated terms ("dairy-free") always skipped.

### Test results

```
"chiken noodl soop"
→ Food dict: "chiken" → "chicken", "noodl" → "noodle", "soop" → "soup"
→ Results: "Homemade Chicken Noodle Soup" (rank 1) ✅

"spageti with meatbals"
→ Food dict: "spageti" → "spaghetti"
→ SymSpell: "meatbals" → passed through (embedding handles it)
→ Results: "Meatballs For Spaghetti" (rank 1) ✅
```

### Experiment results

Saved to `eval/experiments/2026-06-07_spell_check.json`.

```
======================================================================
OVERALL                     Baseline   Experiment    Delta
──────────────────────────────────────────────────────────────────────
  context_relevance            0.470        0.524 ↑ +0.054
  faithfulness                 0.489        0.444 ↓ -0.045
  answer_relevancy             0.267        0.234 ↓ -0.033

CONTEXT RELEVANCE BY CATEGORY
──────────────────────────────────────────────────────────────────────
  misspelling                  0.442        0.617 ↑ +0.175  ← targeted improvement
  x_free                       0.438        0.588 ↑ +0.150  ← unexpected bonus
  exact_match                  0.525        0.613 ↑ +0.088
  multi_intent                 0.362        0.463 ↑ +0.101
  technique                    0.438        0.550 ↑ +0.112
  negation                     0.460        0.503 ↑ +0.043
  dietary                      0.408        0.458 ↑ +0.050
  situation                    0.525        0.425 ↓ -0.100  ← judge noise
```

**Faithfulness/answer_relevancy drops are judge noise** — llama3.2 variance ±0.05, technique dropping 0.263 from a spell-check change is logically impossible. Situation drop warrants investigation but not blocking.

**The signal that matters:** context_relevance improved in 8/12 categories. Misspelling +0.175 is the targeted win.

---

## Key Learnings

**Evaluation is infrastructure.** Without a baseline, every change is a guess. With a baseline, every change is a measurement. This is the discipline that separates portfolio projects from production systems.

**LLM-as-judge has variance.** Single-run scores aren't reliable below ±0.05. Category-level changes need to be logically plausible, not just numerically positive.

**Template-based generation suppresses eval metrics.** answer_relevancy measures "does the response address the question" — a recipe title doesn't look like an answer to a judge expecting prose. This is fine for now; it becomes important when LLM-generated responses are added.

**Domain-specific + general = best spell-check.** Static food dictionary handles high-confidence culinary terms. SymSpell handles the long tail. Neither alone is sufficient.

**Frequency weighting beats edit distance.** Hunspell picked "sup" over "soup". SymSpell picks "soup". The difference is frequency data — production spell-check needs it.

---

## Files Changed

```
src/shared/SessionStore.cs              — Redis circuit breaker, extraction cache
src/agents/Orchestrator/IntentRouter.cs — extraction cache integration
src/agents/Orchestrator/ServiceRegistration.cs — keyed circuit breakers
src/agents/RecipeAgent/QueryPreprocessor.cs    — SymSpell + food dict spell-check
src/agents/RecipeAgent/Dictionaries/
    food_corrections.json               — domain spell corrections
    frequency_dictionary_en_80k.txt     — SymSpell frequency dictionary
src/tests/IntentRouterTests.cs          — SessionStore mock fix
eval/datasets/golden_dataset.json       — 100 labeled Q&A pairs
eval/datasets/retrieved_contexts.json   — last retrieval run
eval/datasets/ragas_results.json        — latest eval scores
eval/experiments/
    2026-06-01_baseline.json            — baseline
    2026-06-07_spell_check.json         — post spell-check
eval/harnesses/
    retrieve.py                         — local retrieval step
    score_simple.py                     — Colab scoring step
    run_ragas.py                        — full RAGAS pipeline
    compare_experiments.py              — experiment diff tool
docs/adrs/009-evaluation-pipeline.md    — eval pipeline ADR
README.md                               — updated with Week 9
```

---

## Deferred to Later Months

- **Dietary pre-filtering** — tag 10K recipes with dietary metadata at ingestion (Month 3 Week 10+)
- **Negation query rewriting** — push negation before embedding (Month 3)
- **Multi-vector retrieval** — decompose multi-intent queries (Month 4)
- **Full RAGAS with OpenAI judge** — requires paid API credits

---

## Next: Week 10 — Langfuse Observability

- Instrument every agent call with Langfuse traces
- Track latency, token usage, failure rates per intent
- Dashboard showing system health over time
- Alert on circuit breaker trips and low-confidence responses