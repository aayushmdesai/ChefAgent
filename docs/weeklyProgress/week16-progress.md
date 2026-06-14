# Week 16 Progress — Production Fix + Eval + IntentRouter

**Month 4, Week 16 | Dates: June 2026**  
**Repos: ChefAgent + mcp-dotnet-diagnostics**

---

## Goals

- Fix production (Nomic exhausted → Voyage migration) ✅
- Re-run eval pipeline on 52k dataset + new embeddings
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

## Deferred

- Colab notebook (`chefagent_embeddings_voyage_batch.ipynb`) — commit
  to `scripts/pipeline/` once cleaned up
- Re-run eval pipeline on 52k + Voyage embeddings (Day 2)
- IntentRouter vocabulary gaps (Day 3)
- Post #2 publish (Day 4)
- Post #3 publish + community seeding (Day 5)
- Month 5 prep (Days 6-7)