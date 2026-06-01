# ADR 009 — Evaluation Pipeline

**Date:** 2026-06-01  
**Status:** Accepted

## Context

After 8 weeks of building ChefAgent's retrieval pipeline, we needed a way to
move from "I think it works" to "I can prove it works." Manual testing with
24 queries gave subjective confidence but no measurable baseline. We needed:

1. A reproducible way to measure retrieval quality
2. A golden dataset to evaluate against
3. A process for proving that changes improve quality

## Decision

### Evaluation framework: custom scorer over RAGAS

We evaluated RAGAS (0.1.21) as the scoring framework. RAGAS is the industry
standard for RAG evaluation but has significant operational constraints:

- Requires an LLM judge for every metric, every question
- Parallel execution causes timeout/recursion errors with local Ollama
- OpenAI-based evaluation costs ~$0.10/run but requires paid API access
- numpy version conflicts with Colab's environment

We implemented a custom sequential scorer (`score_simple.py`) using Ollama
directly — 3 LLM calls per question, no parallelism, no executor overhead.
This trades RAGAS's standardized metrics for operational reliability on
constrained hardware.

**Metrics measured:**
- Context Relevance — are retrieved recipes relevant to the query?
- Faithfulness — is the response grounded in retrieved context?
- Answer Relevancy — does the response address the question?

### Golden dataset: 100 labeled Q&A pairs

100 questions across 12 categories covering the full range of user intents:
exact match, by ingredients, dietary, negation, cuisine, technique, filtering,
situation, multi-intent, misspelling, x-free, and edge cases.

Ground truths are written as specific descriptions rather than expected recipe
titles — this makes the evaluation robust to corpus changes.

### Experiment tracking

Every retrieval change produces a dated JSON file in `eval/experiments/`.
`compare_experiments.py` shows per-category deltas, making improvements
measurable and regressions visible.

## Baseline Results (2026-06-01)

| Metric | Score |
|--------|-------|
| Context Relevance | 0.470 |
| Faithfulness | 0.489 |
| Answer Relevancy | 0.267 |

Weakest categories: multi_intent (0.362), dietary (0.408), x_free (0.438)

## Spell-Check Experiment (2026-06-07)

Added two-layer spell correction to `QueryPreprocessor`:
1. Food domain dictionary (`food_corrections.json`) — high-confidence corrections
   for culinary terms Hunspell/SymSpell don't know
2. SymSpell — frequency-weighted general spell correction

**Results:**

| Category | Before | After | Delta |
|----------|--------|-------|-------|
| misspelling | 0.442 | 0.617 | +0.175 |
| x_free | 0.438 | 0.588 | +0.150 |
| exact_match | 0.525 | 0.613 | +0.088 |
| overall | 0.470 | 0.524 | +0.054 |

## Known Limitations

**Faithfulness and answer relevancy scores are suppressed** by template-based
generation. Returning a recipe title as the answer confuses the LLM judge —
it expects a natural language response. These metrics become meaningful when
LLM-generated responses are added (Month 4).

**Judge variance:** llama3.2 scores vary ±0.05 between runs due to temperature.
Category-level changes below 0.05 should be treated as noise.

## Deferred Improvements

**Dietary pre-filtering (Month 3):** Tag recipes with dietary metadata at
ingestion time. "gluten-free dinner" currently scores 0.2 on context relevance
because the corpus has no gluten-free metadata — only ingredient text.

**Negation query rewriting (Month 3):** Push negation filtering before
embedding instead of post-retrieval. Currently retrieves tomato-heavy recipes
then filters them out, wasting the context budget.

**Multi-vector retrieval (Month 4):** Decompose multi-intent queries
("quick healthy chicken dinner") into sub-queries, retrieve separately, merge.
Single-vector search averages the constraints, weakening each signal.

## Consequences

- Every retrieval change from now on must be evaluated against the baseline
- New experiments saved as dated JSON files in `eval/experiments/`
- Judge model (llama3.2) must remain consistent across experiment runs
  for valid comparisons