# eval/ — evaluation pipeline

Standalone Python scripts (no pytest, no CI integration — the `eval` job in `.github/workflows/ci.yml` is fully commented out), run manually against the live deployed API.

## Structure

- `eval/harnesses/` — `retrieve.py`, `score_ragas.py` (current pipeline — see [[run-eval-pipeline]] for the full procedure and footguns), plus `eval_e2e.py`, `llm_judge.py`, `score_simple.py`, `compare_experiments.py` (older/adjacent harnesses)
- `eval/datasets/` — `golden_dataset.json` (100 queries / 12 categories, retrieval eval), `e2e_golden_dataset.json` (60 cases / 10 categories, end-to-end eval), plus various `*_results.json`/`*.md` report files
- `eval/experiments/` — dated JSON snapshots, one per eval run (e.g. `2026-06-01_baseline.json`, `2026-06-23_final_portfolio.json`)

## `eval/README.md` is stale — don't follow it as-is

It documents an older pipeline shape: it says `retrieve.py` calls `/recipes/search` and that scoring happens on a Colab notebook via `score_simple.py`. As of Week 19, `retrieve.py` targets `/chat` directly (`API_URL = "https://chefagent-production.up.railway.app/chat"`, hardcoded in the script) and scoring runs locally via `score_ragas.py` with a Claude judge + Voyage embeddings — not on Colab, not with `score_simple.py`. The README's own `pip install` line (`requests ragas langchain-community tabulate symspellpy`) is also incomplete for the current pipeline — it's missing `datasets`, `langchain-anthropic`, and `langchain-voyageai`, which `score_ragas.py` actually imports. Use [[run-eval-pipeline]] for the accurate current procedure and dependency list, not this README.

## No dependency manifest

Neither `eval/` nor the repo root has a `requirements.txt`/`pyproject.toml` for these scripts. See [[run-eval-pipeline]] for the actual import list and the Python 3.11 requirement (RAGAS 0.1.21 is not compatible with Python 3.14, per the Week 19 progress doc).

## Comparing experiments

`compare_experiments.py` diffs two experiment JSON files by category. Don't diff the current RAGAS-scored experiment (Claude judge, `score_ragas.py`) against any pre-Week-19 experiment (`voyage_52k`, `semantic_negation`, etc.) — those were scored by a different judge (llama3.2, via the old `score_simple.py`) and a different metric set. The delta between them is a methodology change, not a real signal; this was an explicit warning in the Week 19 progress doc that's easy to miss if you only look at the JSON files.
