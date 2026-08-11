---
name: run-eval-pipeline
description: Run ChefAgent's RAGAS-based retrieval/answer-quality evaluation pipeline. Use when asked to "run the eval suite", "check retrieval quality", "re-score RAGAS", or "regenerate eval numbers" for ChefAgent.
---

# Run the eval pipeline

ChefAgent's current eval pipeline is two Python scripts run in sequence, against the live deployed API — not a pytest suite, and not what `eval/README.md` describes (that doc still says `retrieve.py` hits `/recipes/search` and to score on Colab with `score_simple.py`; both are stale as of Week 19 — see [[docs-are-unverified]]).

## What actually runs today

1. **`python eval/harnesses/retrieve.py`** — calls the live `/chat` endpoint (`API_URL = "https://chefagent-production.up.railway.app/chat"` in the script, hardcoded) for each question in `eval/datasets/golden_dataset.json`, writes `eval/datasets/retrieved_contexts.json`. This was retargeted from `/recipes/search` to `/chat` in Week 19 specifically so the eval measures the real production path (which includes query expansion) rather than the raw retrieval-only endpoint.

2. **`python eval/harnesses/score_ragas.py`** — reads `eval/datasets/retrieved_contexts.json`, scores with RAGAS 0.1.21 (`context_precision`, `faithfulness`, `answer_relevancy`) using a Claude judge (`ChatAnthropic(model="claude-sonnet-4-6")`) and Voyage embeddings (`VoyageAIEmbeddings(model="voyage-4-lite")`), writes `eval/experiments/<date>_final_portfolio.json`. Empty-context records are excluded from scoring and reported separately.

## Environment setup — footgun: there is no dependency manifest

Neither `eval/` nor the repo root has a `requirements.txt`/`pyproject.toml` covering these scripts (only `scripts/requirements.txt` exists, for the unrelated data-pipeline scripts under `scripts/pipeline/`). Before running, create a venv and install by hand from the actual imports found in the scripts:

```bash
python3.11 -m venv .venv-eval && source .venv-eval/bin/activate
pip install requests ragas==0.1.21 datasets langchain-anthropic langchain-voyageai tabulate
```

**Python 3.11 specifically** — RAGAS 0.1.21 is not compatible with Python 3.14 (confirmed by the Week 19 progress doc; not independently re-verified against current RAGAS releases, so re-check if this skill is stale).

You'll also need `ANTHROPIC_API_KEY` and `VOYAGE_API_KEY` in the environment for the judge/embeddings calls in `score_ragas.py` — these are not read from `.env.example` (which doesn't cover eval scripts at all).

## Other harnesses in the same directory (don't confuse with the above)

`eval/harnesses/eval_e2e.py` and `llm_judge.py` are the separate end-to-end harness (60-case golden dataset, different from the 100-query retrieval dataset). `compare_experiments.py` diffs two experiment JSON files — but do **not** diff the RAGAS-scored experiment against pre-Week-19 experiments (`voyage_52k` etc.): those were scored by a different judge (llama3.2, not Claude) and metric set, so the delta is noise, not signal — this was an explicit warning in the Week 19 progress doc.
