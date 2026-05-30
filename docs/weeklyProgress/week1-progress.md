# ChefAgent — Day 1 Progress Log

**Date:** May 23, 2026  
**Goal:** Stand up infrastructure, build data pipeline, achieve end-to-end RAG search  
**Status:** ✅ Complete

---

## What We Built

A working RAG (Retrieval-Augmented Generation) pipeline that takes a natural language query, embeds it, searches 10,000 recipe vectors in Qdrant, and returns ranked results through a .NET API.

```
User Query → .NET API → Ollama (embed) → Qdrant (vector search) → Ranked Recipes
```

---

## 1. Repository & Version Control

- Created GitHub repo: `git@github.com:aayushmdesai/ChefAgent.git`
- Built a comprehensive `.gitignore` covering C#/.NET, Python, Docker, IDE files, data volumes, and secrets
- Learned: `.gitignore` only prevents _future_ tracking — already-committed files need `git rm -r --cached .`

**Key takeaway:** Always set up `.gitignore` before the first commit, not after.

---

## 2. Infrastructure (Docker)

### Services running:

| Service | Image                 | Port                     | Purpose                 |
| ------- | --------------------- | ------------------------ | ----------------------- |
| Qdrant  | qdrant/qdrant:v1.12.1 | 6333 (REST), 6334 (gRPC) | Vector database         |
| Redis   | redis:7-alpine        | 6379                     | Memory/caching (future) |
| Ollama  | ollama/ollama:latest  | 11434                    | LLM + embeddings        |

### Issues encountered & resolved:

- **Docker CLI not found:** Docker Desktop installed but symlinks not created → `sudo ln -s` to `/usr/local/bin/`
- **Credential helper error:** `docker-credential-desktop` not in PATH → emptied `~/.docker/config.json` to `{}`
- **`version` key warning:** Removed deprecated `version` field from `docker-compose.yml` (Compose V2 doesn't use it)

### Architecture decision — Ollama native vs Docker:

On 8GB RAM, running Ollama inside Docker (capped at 3.8GB) caused embedding to stall at 0% CPU. Switched to running Ollama **natively** via `brew install ollama` for direct RAM access. Docker reserved for Qdrant and Redis only.

---

## 3. Models Pulled

| Model            | Size  | Dimension | Purpose                              |
| ---------------- | ----- | --------- | ------------------------------------ |
| nomic-embed-text | 274MB | 768       | Embedding (query + document vectors) |
| llama3.2         | 2GB   | —         | Chat/reasoning (future)              |

---

## 4. Data Pipeline

### 4a. Dataset Selection

- **Original plan:** RecipeNLG (`mbien/recipe_nlg`) — gated dataset requiring manual download
- **Switched to:** `corbt/all-recipes` — freely available on HuggingFace, no auth required
- **Reason:** RecipeNLG requires manual download from external site; `corbt/all-recipes` works with a simple `load_dataset()` call
- **Note:** Plan to revisit RecipeNLG later if richer metadata is needed

### 4b. Python Environment

- Python 3.14 on macOS blocks system-wide `pip install` (PEP 668)
- Created project-level virtual environment: `python3 -m venv .venv`
- macOS uses `python3`/`pip3`, not `python`/`pip` — added aliases to `.zshrc`

### 4c. Recipe Preparation (`scripts/prepare_recipes.py`)

- Downloads dataset from HuggingFace (bypassed `datasets` library incompatibility with Python 3.14 by using pandas + parquet directly)
- Parses single `input` field into structured fields: title, ingredients, directions
- Builds `combined_text` field for embedding (title + ingredients + directions in one block)
- Generates stable document IDs via MD5 hash
- Output: `data/processed/recipes.jsonl` — 10,000 recipes

**Why combine fields into one `combined_text`?**  
A user query like "easy chicken stir fry with soy sauce" touches title ("stir fry"), ingredients ("soy sauce", "chicken"), and directions ("easy" = few steps). With one combined embedding, a single vector captures the full semantic meaning. Separating fields would require multiple searches + complex merging (late fusion) — that's a Month 3 optimization.

### 4d. Embedding Generation (`scripts/generate_embeddings.py` + Colab)

- Local Ollama embedding was too slow on 8GB RAM (estimated 67 hours for 10K recipes)
- Used **Google Colab with GPU** and `sentence-transformers` library instead
- Key detail: documents prefixed with `search_document:` and queries with `search_query:` (nomic-embed-text training convention)
- Output: `data/embeddings/recipe_vectors.jsonl` — 10,000 recipes with 768-dim vectors

**Embedding dimension (768) matters because:**

1. Qdrant collection must be created with exact `size: 768` — mismatch = failure
2. Memory scales linearly: 768 floats × 4 bytes × 10K = ~30MB; at 2M recipes = ~6GB

### 4e. Qdrant Loading (`scripts/load_qdrant.py`)

- Creates collection with `Distance.COSINE` (not Euclidean or Dot Product)
- Uploads in batches of 200 via `upsert`
- Stores payload: title, ingredients, directions, counts
- Cleaned out dead fields from RecipeNLG (`ingredient_names`, `source`, `source_url`)

**Why Cosine distance?**  
Cosine measures angle between vectors, ignoring magnitude. A short recipe description and a long one about the same dish would have different vector lengths but similar directions. Cosine normalizes this — only meaning matters, not text length.

---

## 5. Search Verification

### 5a. Python Test Script (`scripts/test_loaded_qdrant.py`)

Tested three query types:
| Query | Top Result | Score | Verdict |
|-------|-----------|-------|---------|
| easy chicken dinner with garlic | Easy Chicken Dinner | 0.7646 | ✅ Relevant |
| something warm and comforting | Happiness (joke recipe) | 0.6026 | ❌ Semantic match on emotion, not food |
| how to fix a flat tire | Vegetable Crunch | 0.5708 | ✅ Correctly low score |

### 5b. Structured Quality Test (`scripts/test_search_quality.py`)

Ran 12 queries across 6 categories:

| Category       | Avg Top Score | Assessment                                          |
| -------------- | ------------- | --------------------------------------------------- |
| By Ingredients | 0.8282        | 🟢 Strongest — concrete nouns match well            |
| Exact Match    | 0.8053        | 🟢 Recipe titles are concrete                       |
| Dietary        | 0.6995        | 🟡 Matched food type but ignored dietary constraint |
| Cuisine        | 0.6815        | 🟡 Matched "spicy" but missed "Mexican"             |
| Situation      | 0.6373        | 🔴 Abstract queries need LLM reasoning              |
| Irrelevant     | 0.5822        | ✅ Correctly low                                    |

**Score gap (relevant vs irrelevant): 0.1482** — this is the Month 1 baseline.

---

## 6. .NET API (`src/api`)

- Built and ran the API: `cd src/api && dotnet run`
- Endpoint: `POST /recipes/search` accepts `{ "query": "...", "maxResults": N }`
- Wired to `RecipeSearchPlugin` (Semantic Kernel plugin) → Ollama → Qdrant
- Runs on `http://localhost:5000` (Development mode)
- Added `search_query:` prefix in `GetEmbeddingAsync` to match document prefix convention

### Compatibility note:

- `qdrant-client` Python package v1.18 warns about server v1.12 mismatch — works but should align versions later
- .NET `Qdrant.Client` v1.12 matches the server

---

## 7. Evaluation Dataset (`eval/datasets/retrieval_baseline.md`)

Generated 24-query evaluation dataset across 12 categories:

| Category       | Tests | Purpose                       |
| -------------- | ----- | ----------------------------- |
| Exact Match    | 2     | Baseline — should always work |
| By Ingredients | 2     | "What can I make with X"      |
| Dietary        | 2     | Constraint understanding      |
| Cuisine        | 2     | Cultural/regional matching    |
| Situation      | 2     | Abstract/mood queries         |
| Irrelevant     | 2     | Should return low scores      |
| Misspelling    | 2     | Typo resilience               |
| Constraint     | 2     | Time/quantity limits          |
| Negative       | 2     | "Without X" understanding     |
| Multi-Intent   | 2     | Complex queries               |
| Conversational | 2     | Vague/open-ended              |
| Technique      | 2     | Cooking method matching       |

### Key findings from new categories:

- **Negative constraints fail:** "pasta without tomatoes" returned "Pasta With Tomatoes" as #2 result — vector search treats "with" and "without" nearly identically
- **Misspellings degrade:** "chiken noodle soop" matched "Noodle Nibbles" (chocolate chow mein) — lost "chicken" and "soup" semantics
- **Technique queries work well (0.78):** "slow cooker beef stew" found relevant crock-pot recipes

---

## Concepts Learned

| Concept                                 | What It Means                                                                                          |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| RAG Pipeline                            | Retrieve relevant documents via vector search, then augment LLM response with them                     |
| Vector Embedding                        | Converting text to a fixed-size array of floats (768 dims) that captures semantic meaning              |
| Cosine Similarity                       | Measuring meaning similarity by angle between vectors, ignoring text length                            |
| Combined Text                           | Merging title + ingredients + directions into one embedding for single-vector search                   |
| search_document / search_query prefixes | nomic-embed-text convention to distinguish stored docs from search queries                             |
| Post-retrieval filtering                | Two-stage: score threshold (cheap) + LLM reranker (smart) to improve result quality                    |
| Limitations of vector search            | Cannot handle negation ("without"), struggles with abstract/mood queries, misspellings degrade quality |

---

## Files Changed / Created

```
scripts/
├── prepare_recipes.py          # Updated: corbt/all-recipes + pandas/parquet
├── generate_embeddings.py      # Original (used Colab instead for speed)
├── load_qdrant.py              # Updated: removed dead fields
├── test_loaded_qdrant.py       # Updated: query_points API
├── test_search_quality.py      # New: structured 12-query test
├── generate_eval_dataset.py    # New: 24-query eval generator

eval/datasets/
├── retrieval_baseline.md       # New: evaluation dataset (needs manual ratings)

data/processed/
├── recipes.jsonl               # 10K chunked recipes

data/embeddings/
├── recipe_vectors.jsonl        # 10K recipes with 768-dim embeddings

.gitignore                      # New: comprehensive C#/Python/Docker ignore
chefagent_embeddings.ipynb      # Colab notebook for GPU embedding
```

---

## What's Next (Month 1 Remaining)

- [ ] Fill in subjective ratings in `eval/datasets/retrieval_baseline.md`
- [ ] Add `search_query:` prefix in `RecipeSearchPlugin.GetEmbeddingAsync`
- [ ] Build the Diet Agent
- [ ] Wire up basic chat endpoint through Orchestrator
- [ ] Commit and push to GitHub

---

_This document serves as both a progress log and a learning journal. Each section captures not just what was done, but why decisions were made — useful for portfolio interviews and Month 3 retrospective._
