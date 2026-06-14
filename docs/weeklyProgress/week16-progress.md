# Week 16 Progress — Production Fix + Eval + IntentRouter

**Month 4, Week 16 | Dates: June 2026**  
**Repos: ChefAgent + mcp-dotnet-diagnostics**

---

## Goals

- Fix production (Nomic exhausted → Voyage migration) ✅
- Re-run eval pipeline on 52k dataset + new embeddings ✅
- Fix IntentRouter vocabulary gaps
- Publish Post #2
- Publish Post #3 + community seeding
- Month 5 prep

---

## Day 1 — Voyage AI Migration ✅

### Problem

Production was down. Nomic Atlas API free tier (10M tokens) exhausted
mid-run during the Week 15 dataset expansion. Query-time embedding calls
were returning HTTP 400, breaking recipe search entirely.

### Fix

Migrated query-time embeddings from Nomic Atlas API to Voyage AI.

**New file: `src/shared/VoyageEmbeddingProvider.cs`**
- Implements `IEmbeddingProvider` — same interface as Nomic
- Endpoint: `POST https://api.voyageai.com/v1/embeddings`
- Auth via `Authorization: Bearer {key}` header
- Parses `data[0].embedding` from response
- ~30 lines, structurally identical to `NomicEmbeddingProvider.cs`
- Default model: `voyage-4-lite`

**Updated: `src/api/ServiceRegistration.cs`**
- Added `voyage` registration block alongside existing `nomic`, `huggingface`, `ollama` blocks
- Config: `EmbeddingProvider=voyage`, `Voyage__ApiKey=...`, `Voyage__Model=voyage-4-lite`
- Zero agent code changes — provider-agnostic architecture validated again

### Re-embedding 52k Recipes

The stored vectors in Qdrant were 768d Nomic vectors. Voyage outputs
different dimensions, requiring a full re-embed and Qdrant reload.

**Model selected: `voyage-4-lite`**
- 200M free tokens per account (52k recipes × ~300 tokens = ~15.6M tokens — well within free tier)
- 1024d output vectors
- Designed for retrieval

**Embedding approach: Voyage Batch API**

Standard Voyage API has 3 RPM rate limit on free tier — 52k recipes at
batch size 50 would take ~5.8 hours with constant 429 errors. The Batch
API has no RPM limit: bundle all requests into a JSONL file, submit once,
poll until complete.

Process:
1. Prepared `batch_input.jsonl` — 1,044 requests × 100 recipes each
2. Uploaded to Voyage Files API
3. Submitted batch job with `voyage-4-lite`, 12h completion window
4. Polled every 60s — completed in ~12 minutes (1,044/1,044 requests)
5. Downloaded results, converted to `recipe_vectors_voyage.jsonl`
6. Ran `load_qdrant.py` — deleted 768d collection, created 1024d, uploaded 52,155 points

**Qdrant Cloud final state:**
- Points: 52,155
- Vectors: dim=1024, distance=Cosine

**Local verification:**
```bash
curl -X POST http://localhost:5100/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query":"chicken stir fry","maxResults":3}'
# → "Easy Chicken Stir-Fry" (0.745), "Stir-Fry Chicken" (0.731),
#   "Stir-Fry Minced Chicken With Vegetables" (0.727) ✅
```

### Architecture Validation (Again)

Nomic → Voyage swap required:
- 1 new file (`VoyageEmbeddingProvider.cs`)
- 1 registration block in `ServiceRegistration.cs`)
- 0 agent code changes

Time from start to working local test: ~1 day (mostly waiting for
Qdrant upload and debugging env var issues).

This is the second provider swap validated in production
(first was Ollama → Groq in Week 12). The `IEmbeddingProvider`
abstraction held.

### Debugging Notes

- `.env.local` vars not picked up by `dotnet run` directly — need
  `export $(grep -v '^#' .env.local | xargs)` or inline env vars
- Docker Compose `--env-file` injects vars for `${VAR}` substitution
  in the compose file, but `env_file:` in the service block is what
  actually injects them into the container — both needed
- Voyage Batch API output line order is not guaranteed — use `custom_id`
  to map results back to input docs, not line position
- `voyage-4-lite` outputs 1024d (not 512d as older `voyage-3-lite` docs
  suggested) — dimension auto-detected by `load_qdrant.py` from first doc
- Colab notebook loaded only 26,100 of 52,155 docs on first pass (likely
  encoding issue mid-file) — fixed by reloading with `errors='ignore'`
  and iterating over `results` dict instead of `docs` list in step 7

### Commit

```
feat(embeddings): migrate from Nomic to Voyage AI

- VoyageEmbeddingProvider.cs: new IEmbeddingProvider backed by
  voyage-4-lite (1024d, 200M free tokens)
- ServiceRegistration.cs: add voyage provider registration block
- generate_embeddings_voyage.py: batch embedding script with resume
  support and rate limit handling
- Qdrant Cloud reloaded: 52,155 recipes at 1024d cosine
- Zero agent code changes — provider-agnostic architecture validated

EmbeddingProvider=voyage in Railway env vars to activate
```

---

## Day 2 — Eval Pipeline Re-run (voyage-4-lite, 52k corpus) ✅

### Setup

- Added `time.sleep(25)` between questions in `retrieve.py` to respect
  Voyage's 3 RPM free tier limit at query time
- Added retry with backoff on 429 in `VoyageEmbeddingProvider.cs` —
  respects `Retry-After` header, falls back to `20s × attempt`
- `score_simple.py` unchanged — Ollama running on Colab (llama3.2)

### Results

**Experiment:** `eval/experiments/2026-06-14_voyage_52k.json`  
**Baseline:** `eval/experiments/2026-06-07_spell_check.json`

| Metric | Baseline | Experiment | Delta |
|---|---|---|---|
| context_relevance | 0.524 | 0.578 | ↑ +0.054 |
| faithfulness | 0.444 | 0.477 | ↑ +0.033 |
| answer_relevancy | 0.234 | 0.279 | ↑ +0.045 |

**Per-category context relevance:**

| Category | Baseline | Experiment | Delta |
|---|---|---|---|
| technique | 0.550 | 0.800 | ↑ +0.250 |
| dietary | 0.458 | 0.675 | ↑ +0.217 |
| misspelling | 0.617 | 0.800 | ↑ +0.183 |
| multi_intent | 0.463 | 0.600 | ↑ +0.137 |
| situation | 0.425 | 0.562 | ↑ +0.137 |
| cuisine | 0.613 | 0.725 | ↑ +0.112 |
| exact_match | 0.613 | 0.725 | ↑ +0.112 |
| filtering | 0.438 | 0.488 | ↑ +0.050 |
| by_ingredients | 0.610 | 0.630 | ↑ +0.020 |
| x_free | 0.588 | 0.450 | ↓ -0.138 |
| negation | 0.503 | 0.280 | ↓ -0.223 |
| edge_case | 0.433 | 0.167 | ↓ -0.266 |

### Analysis

**Wins driven by corpus size (10k → 52k):** technique, dietary, cuisine,
multi_intent, situation all improved significantly. More candidates in
Qdrant means better top-k matches.

**Indian recipes paying off:** dietary +0.217 is the clearest signal —
the Indian recipe dataset added in Week 15 directly improved retrieval
for dietary-specific queries.

**SymSpell holding up:** misspelling +0.183 across an embedding model
change validates that spell correction is doing real work upstream of
the vector search.

**Regressions — negation (-0.223), x_free (-0.138), edge_case (-0.266):**
Likely `voyage-4-lite` encodes "without X" and "X-free" queries
differently than Nomic did — pulling toward recipes containing X rather
than excluding it. This is not a RAG problem, it's an IntentRouter /
DietAgent filtering gap. The edge_case regression is mostly noise (tiny
sample, inherently low-signal queries like "food", "r", "xkqzpw blarfnog").

**Root cause of negation/x_free regressions:** these query types need
post-retrieval filtering in DietAgent, not better embedding. The embedding
model retrieves semantically similar recipes; exclusion logic must happen
at the agent layer. Flagged as tech debt.

### Commits

```
eval: voyage-4-lite 52k experiment results

Overall: context_relevance +0.054, faithfulness +0.033, answer_relevancy +0.045
Wins: technique +0.250, dietary +0.217, misspelling +0.183
Regressions: negation -0.223, edge_case -0.266, x_free -0.138
```

```
fix(voyage): add retry with backoff on 429
```

---

## Deferred

- Colab notebook (`chefagent_embeddings_voyage_batch.ipynb`) — commit
  to `scripts/pipeline/` once cleaned up
- IntentRouter vocabulary gaps (Day 3)
- Post #2 publish (Day 4)
- Post #3 publish + community seeding (Day 5)
- Month 5 prep (Days 6-7)
- Negation/x_free regression investigation (tech debt)