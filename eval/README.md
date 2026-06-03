# ChefAgent — Evaluation Pipeline

This directory contains the evaluation infrastructure for ChefAgent. Three layers of evaluation are maintained:

1. **Retrieval quality** — RAGAS pipeline measuring vector search quality
2. **End-to-end quality** — LLM judge measuring full pipeline response quality  
3. **System performance** — Langfuse traces measuring latency per operation

---

## Prerequisites

```bash
# Python dependencies
pip install requests ragas langchain-community tabulate symspellpy

# Services must be running
docker compose up -d   # Qdrant, Redis, Langfuse, API
ollama serve           # Ollama (native on macOS, not in Docker)
```

---

## 1. Retrieval Eval (RAGAS)

Tests `/recipes/search` in isolation. Measures whether vector search finds the right documents.

### Step 1 — Retrieve contexts (runs locally)

```bash
python eval/harnesses/retrieve.py
```

Calls `/recipes/search` for all 100 queries in `eval/datasets/golden_dataset.json`.  
Output: `eval/datasets/retrieved_contexts.json`

### Step 2 — Score with RAGAS (runs on Colab GPU)

Upload `eval/datasets/retrieved_contexts.json` and `eval/datasets/golden_dataset.json` to a Colab notebook.

```python
# In Colab
!pip install ragas langchain-community
# Run eval/harnesses/score_simple.py
```

Download the result and save as:
```
eval/experiments/YYYY-MM-DD_<experiment_name>.json
```

### Step 3 — Compare experiments

```bash
python eval/harnesses/compare_experiments.py \
    eval/experiments/2026-06-01_baseline.json \
    eval/experiments/2026-06-07_spell_check.json
```

Prints per-category delta table with ↑/↓/→ arrows.

### Golden dataset

`eval/datasets/golden_dataset.json` — 100 queries across 12 categories:

| Category | Count | Tests |
|---|---|---|
| by_ingredients | 10 | Ingredient-based search |
| cuisine | 8 | Cuisine type search |
| dietary | 10 | Dietary restriction search |
| edge_case | 6 | Unusual or ambiguous queries |
| exact_match | 8 | Specific recipe names |
| filtering | 8 | maxIngredients / maxSteps |
| misspelling | 8 | Typos and misspellings |
| multi_intent | 8 | Multiple constraints in one query |
| negation | 8 | "without X", "no Y" |
| situation | 8 | Context-based ("weeknight dinner") |
| technique | 8 | Cooking method search |
| x_free | 10 | X-free dietary queries |

### Experiment files

| File | Description |
|---|---|
| `2026-06-01_baseline.json` | Baseline — no improvements |
| `2026-06-07_spell_check.json` | After SymSpell + food domain dictionary |
| `2026-06-03_semantic_negation.json` | After X-free semantic expansion |

---

## 2. End-to-End Eval (LLM Judge)

Tests `/chat` — the full pipeline including intent classification, dietary validation, session state, and guardrails.

### Step 1 — Run the e2e harness

```bash
python eval/harnesses/eval_e2e.py
```

Calls `/chat` for all 60 cases in `eval/datasets/e2e_golden_dataset.json`.  
Handles stateful sequences (setup_messages sent before the test message).  
Output: `eval/datasets/e2e_results.json`

### Step 2 — Run the LLM judge

```bash
python eval/harnesses/llm_judge.py
```

Scores each response on helpfulness (1-5), safety (1-5), coherence (1-5) using Ollama.  
Output: `eval/datasets/e2e_judge_results.json`

### E2E golden dataset

`eval/datasets/e2e_golden_dataset.json` — 60 test cases across 10 intent categories:

| Category | Count | Tests |
|---|---|---|
| search_simple | 8 | Intent classification + retrieval |
| search_with_diet | 10 | Intent + retrieval + diet validation |
| search_negation | 6 | Intent + negation + retrieval |
| validate_diet | 8 | Direct diet validation |
| create_meal_plan | 3 | Plan generation + diet compliance |
| modify_meal_plan | 6 | Session state + plan modification |
| get_meal_plan | 8 | Session state retrieval |
| implicit_dietary | 6 | LLM entity extraction |
| guardrail | 4 | Injection blocked, rate limited |
| general_question | 2 | Fallback Q&A |

**Session ID strategy:** All session IDs are prefixed with a run-scoped timestamp (`RUN_ID`). This prevents Redis state collision across multiple runs. Each run gets fresh sessions automatically.

**Stateful sequences:** Cases with `setup_messages` send those first (to prime Redis state) before sending the actual test message. The harness handles this automatically.

### Known limitations

- LLM judge uses `llama3.2` to judge `llama3.2` output — same model, same blind spots. Scores are directionally useful for comparing categories, not absolute quality claims.
- `implicit_dietary` safety scores are null — the judge doesn't have visibility into the LLM-extracted profile. Requires storing the extracted profile in `e2e_results.json`.
- `get_meal_plan` helpfulness is artificially low — the judge sees only the response message string, not the `mealPlan` object content.

---

## 3. Negation Fix Validation

```bash
python scripts/eval/test_semantic_negation.py
```

7 X-free test cases + 1 control. Checks that dairy-free, gluten-free, and nut-free queries don't return recipes containing the excluded ingredients.  
Output: `eval/datasets/week11_negation_test_<timestamp>.json`

---

## 4. File Reference

```
eval/
├── datasets/
│   ├── golden_dataset.json              # 100-query RAGAS dataset
│   ├── retrieved_contexts.json          # Last retrieval run output
│   ├── ragas_results.json               # Latest RAGAS scores
│   ├── e2e_golden_dataset.json          # 60-case e2e dataset
│   ├── e2e_results.json                 # Last e2e harness run
│   ├── e2e_judge_results.json           # Last LLM judge run
│   └── month3-eval-report.md            # Consolidated Month 3 report
├── experiments/
│   ├── 2026-06-01_baseline.json
│   ├── 2026-06-07_spell_check.json
│   └── 2026-06-03_semantic_negation.json
└── harnesses/
    ├── retrieve.py                      # Local retrieval step
    ├── score_simple.py                  # Colab scoring step
    ├── run_ragas.py                     # Full RAGAS pipeline
    ├── compare_experiments.py           # Experiment diff tool
    ├── eval_e2e.py                      # E2E harness
    └── llm_judge.py                     # LLM judge scorer
```