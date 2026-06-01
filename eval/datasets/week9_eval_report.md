# Week 9 — Retrieval Evaluation Report

**Date:** 2026-06-01  
**Judge model:** llama3.2 (Ollama, sequential)  
**Questions:** 100 across 12 categories  
**Pipeline:** retrieve.py (local) → score_simple.py (Colab GPU)

---

## Overall Scores

| Metric | Score | Notes |
|--------|-------|-------|
| Context Relevance | 0.470 | How relevant are retrieved recipes to the query |
| Faithfulness | 0.489 | Are responses grounded in retrieved context |
| Answer Relevancy | 0.267 | Does response address the question |

**Note on answer relevancy:** Scores are suppressed by template-based generation —
returning a recipe title rather than a natural language answer confuses the LLM judge.
This is expected. Faithfulness is inherently high for template-based systems.

---

## Per-Category Breakdown

| Category | Relevance | Faithfulness | Ans. Relevancy | Assessment |
|----------|-----------|-------------|----------------|------------|
| by_ingredients | 0.63 | 0.52 | 0.20 | ✅ Strong — ingredient matching suits vector search |
| cuisine | 0.62 | 0.69 | 0.30 | ✅ Strong — cuisine terms embed well |
| exact_match | 0.53 | 0.62 | 0.25 | ✅ Good — named dishes found reliably |
| situation | 0.53 | 0.51 | 0.30 | ⚠️ Moderate — abstract queries partially work |
| filtering | 0.45 | 0.35 | 0.45 | ⚠️ Moderate — metadata filtering not in embeddings |
| technique | 0.44 | 0.54 | 0.44 | ⚠️ Moderate — technique terms partially embed |
| negation | 0.46 | 0.49 | 0.20 | ⚠️ Weak — regex catches explicit negation but semantic gap remains |
| misspelling | 0.44 | 0.53 | 0.22 | ⚠️ Weak — embeddings handle mild typos, severe ones fail |
| dietary | 0.41 | 0.43 | 0.25 | ❌ Weak — dietary labels not in recipe text |
| x_free | 0.44 | 0.42 | 0.20 | ❌ Weak — "dairy-free" doesn't match recipes semantically |
| multi_intent | 0.36 | 0.54 | 0.24 | ❌ Weak — combined constraints confuse single-vector search |
| edge_case | 0.27 | 0.17 | 0.17 | ✅ Expected — garbage queries should score low |

---

## Root Cause Analysis

### dietary + x_free (relevance ~0.40)
**Root cause:** Dietary labels ("gluten-free", "vegan", "dairy-free") are not present
in recipe text. The corpus contains recipes with ingredients like "milk" and "flour"
but no metadata tagging them as non-gluten-free or non-vegan. Vector search finds
semantic similarity on dish names, not dietary properties.

**Fix path (Month 3):** Pre-filter using Diet Agent at retrieval time — run dietary
validation before returning results, not after. Tag recipes with dietary metadata
during ingestion using LLM classification.

### negation (relevance 0.46, relevancy 0.20)
**Root cause:** Regex negation handler catches "pasta without tomatoes" → removes
tomato recipes post-retrieval. But the retrieval step still fetches tomato-heavy
recipes first, wasting context budget. Relevancy is low because top results often
violate the negation before filtering.

**Fix path:** Push negation filtering earlier — into Qdrant payload filter or
pre-retrieval query rewriting.

### multi_intent (relevance 0.36)
**Root cause:** "quick healthy chicken dinner for weeknight" combines 3 constraints
(speed + health + ingredient) into one vector. A single embedding can't capture all
three simultaneously — it averages them, weakening each signal.

**Fix path (Month 4):** Multi-vector retrieval — decompose query into sub-queries,
retrieve separately, merge results.

### misspelling (relevance 0.44)
**Root cause:** nomic-embed-text handles mild typos through subword tokenization
("choclate" → close to "chocolate"). Severe misspellings ("chiken noodl soop") 
fail because the embedding drifts too far from the correct term.

**Fix path:** Add spell-check preprocessing step before embedding (e.g. `pyspellchecker`).

### filtering (relevance 0.45)
**Root cause:** Queries like "quick dinner ready in 20 minutes" or "simple recipes
under 5 ingredients" are metadata queries, not semantic ones. Vector search has no
concept of prep time or ingredient count — these map to Qdrant payload filters
but the user's natural language doesn't always trigger them.

**Fix path:** Improve query preprocessor to extract numeric constraints and route
to payload filters instead of pure vector search.

---

## Comparison vs Week 2 Subjective Ratings

| Category | Week 2 Subjective | Week 9 RAGAS | Match? |
|----------|------------------|-------------|--------|
| exact_match | Strong | 0.53 | ✅ |
| by_ingredients | Strong | 0.63 | ✅ |
| dietary | Weak | 0.41 | ✅ |
| negation | Improved but imperfect | 0.46 | ✅ |
| situation | Weakest | 0.53 | ⚠️ Better than expected |
| misspelling | Moderate | 0.44 | ✅ |

RAGAS scores largely confirm Week 2 intuition. The only surprise is situation
scoring higher than expected — likely because abstract queries still retrieve
broadly relevant dinner recipes even without semantic understanding.

---

## Baseline Experiment

Results saved to `eval/experiments/2026-06-01_baseline.json`.
Future changes that affect retrieval must be re-evaluated against this baseline.

---

## Priority Fix List

1. **Spell-check preprocessing** — highest ROI, small change, fixes misspelling category
2. **Dietary pre-filtering** — fixes dietary + x_free, requires Diet Agent integration
3. **Negation query rewriting** — push negation earlier in pipeline
4. **Multi-vector retrieval** — fixes multi_intent, deferred to Month 4