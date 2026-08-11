# scripts/ — data pipeline & manual test scripts

Two unrelated things live under `scripts/`, easy to confuse:

## `scripts/pipeline/` — recipe ingestion pipeline

Chain, in order: `prepare_recipes.py` (and `prepare_recipes_2.py`, which adds a second dataset — `Anupam007/indian-recipe-dataset`, 5,938 recipes with a `cuisine` payload field, layered onto the original `corbt/all-recipes` 10K set per Week 15) → `generate_embeddings.py` (Ollama, `nomic-embed-text`) or `generate_embeddings_nomic.py` (Nomic Atlas API variant) → `load_qdrant.py` (writes vectors into the Qdrant collection). Run via `make reload-vectors` (`python3 scripts/pipeline/load_qdrant.py` alone — for a full rebuild use `make fresh`, which chains prepare → embed → load).

Dependencies: `scripts/requirements.txt` (`datasets>=2.20.0`, `pandas>=2.2.0`, `tqdm>=4.66.0`, `requests>=2.32.0`, `qdrant-client>=1.12.0`). This manifest is **only** for the pipeline scripts — it does not cover `eval/` (see [eval/CLAUDE.md](../eval/CLAUDE.md), which has no manifest at all) or `scripts/eval/` below.

## `scripts/eval/` — 15 standalone manual test/debug scripts

Not pytest-discovered, not covered by `scripts/requirements.txt`'s stated purpose (though they may happen to share some deps) — each is run individually against a **locally running API** (`http://localhost:5000` per script docstrings, note this differs from the `make health`/CI convention of port `5100` — check the specific script's docstring for which port it expects before running):

`test_circuit_breaker.py`, `test_diet_agent.py`, `test_e2e_sweep.py`, `test_failure_modes.py`, `test_guardrails.py`, `test_input_guard.py`, `test_loaded_qdrant.py`, `test_memory.py`, `test_orchestrator.py`, `test_output_guard.py`, `test_planner.py`, `test_search_quality.py`, `test_semantic_negation.py`, `week10_observability_test.py`, plus `load_test.py`, `profile_performance.py`, `generate_eval_dataset.py`.

These predate and are separate from the `eval/harnesses/` pipeline (see [[run-eval-pipeline]]) — despite the name overlap ("eval"), `scripts/eval/*.py` are ad hoc manual scripts written during specific weekly debugging sessions (per their filenames matching weekly-progress doc topics), not the maintained eval suite. Don't assume running one of these validates current behavior — check whether the underlying feature/endpoint it targets has changed since the script's era before trusting its output.

## `scripts/setup.sh`

Full local bootstrap in one script: checks prerequisites (`docker`, `ollama`, `python3`, `dotnet`), starts Qdrant+Redis (`docker compose up -d qdrant redis`), pulls Ollama models, creates a `.venv`, `pip install -r scripts/requirements.txt`, runs the pipeline (`prepare_recipes.py --limit 10000` → `generate_embeddings.py` → `load_qdrant.py`), verifies with `scripts/eval/test_loaded_qdrant.py`. Run after cloning, before `make up`.

## `.devcontainer/setup.sh` — a *different* script, don't confuse the two

Codespaces runs `.devcontainer/setup.sh` automatically on container create (per `.devcontainer/devcontainer.json`'s `postCreateCommand`), not `scripts/setup.sh` — the two are not the same file and take different approaches. `.devcontainer/setup.sh` runs the **full** `docker compose up -d --build` (not the lean qdrant+redis-only start), and rather than generating embeddings itself, it expects a pre-built `data/embeddings/recipe_vectors.jsonl` to already be present — if it's missing (the file is gitignored, so it always is on a fresh Codespace), the script just prints a warning and continues. Getting a working Codespace requires a manual step with no CLI equivalent: upload `recipe_vectors.jsonl` through the VS Code file-explorer UI into `data/embeddings/`, then run `make reload-vectors`. Full walkthrough, including a troubleshooting table and the port map, is in `CODESPACES.md` at the repo root — read that file directly for Codespaces work rather than inferring from this one. `.devcontainer/setup.sh` also globally installs `csharpier` (a .NET formatter) — this is unused elsewhere in the repo (no config file, not referenced in CI or any other script), so don't treat it as an enforced formatting convention.
