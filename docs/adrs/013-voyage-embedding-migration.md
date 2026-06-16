# ADR-013: Embedding Provider Migration — Nomic → Voyage AI

**Date:** June 2026  
**Status:** Accepted  
**Deciders:** Aayush Desai  
**Tags:** infrastructure, embeddings, providers, retrieval

---

## Context

ChefAgent's embedding pipeline has two responsibilities:

1. **Offline (one-time):** embed 52,155 recipes and store vectors in Qdrant Cloud
2. **Online (every search):** embed the user's query at request time and search Qdrant

Both sides must use the same model. If stored vectors and query vectors come from
different models, cosine similarity scores become meaningless — the vector spaces are
incompatible.

### The Nomic Failure

Week 12 (cloud deployment) selected Nomic Atlas API as the query-time embedding provider.
The stored vectors were generated with `nomic-embed-text-v1` via local Ollama — Nomic
Atlas API serves the same model, so vector spaces matched perfectly. This worked well
for the 10k recipe corpus.

Week 15 expanded the corpus from 10k → 52k recipes. The Nomic Atlas API free tier
(10M tokens) was exhausted during the offline embedding run. Once exhausted:

- Offline re-embedding: blocked (no tokens remaining)
- Online query embedding: `HTTP 400 Bad Request` on every search request
- Production status: **down**

The free tier limit of 10M tokens is insufficient for a 52k recipe corpus at ~300
tokens per recipe (~15.6M tokens total). Any future corpus expansion would hit the
same wall.

### Constraints (unchanged from ADR-012)

1. **No paid services** — free tier must be sufficient for portfolio demo traffic
2. **No agent rewrites** — `IEmbeddingProvider` abstraction must hold
3. **Vector space compatibility** — stored and query vectors must use the same model

---

## Options Considered

### Option A — Nomic paid tier

Top up Nomic Atlas API credits. 10M tokens costs approximately $0.10. Unblocks
immediately.

**Rejected:** Requires payment. Also doesn't solve the structural problem — the next
corpus expansion would hit the limit again, and the free tier is permanently too small
for this use case.

### Option B — HuggingFace Inference API

`api-inference.huggingface.co` is blocked at DNS level in both GitHub Codespaces and
Railway (documented in ADR-012). `HuggingFaceEmbeddingProvider.cs` was implemented and
retained in the codebase but cannot be used in either environment.

**Rejected:** DNS-blocked in all deployment environments.

### Option C — Self-hosted model via Ollama (Railway)

Run `nomic-embed-text` via Ollama on Railway alongside the API. Zero external API calls.

**Rejected:** Railway's free tier ($5 trial credit) does not support the memory footprint
required to run Ollama. This approach would require a paid Railway plan or a dedicated
GPU instance.

### Option D — Google Colab for offline, Ollama for online

Embed the 52k recipes using Ollama on a local or Colab GPU. Use Ollama at query time
on Railway.

**Rejected:** Railway does not run Ollama. Query-time embedding on Railway requires
either a cloud API or a GPU instance.

### Option E — Voyage AI (selected)

Voyage AI offers `voyage-4-lite` with 200M free tokens per account. At ~300 tokens
per recipe × 52,155 recipes ≈ 15.6M tokens for offline embedding, plus negligible
query-time usage, the free tier is more than sufficient.

**Key properties:**
- 200M free tokens — 12× more than the Nomic free tier
- `voyage-4-lite` outputs 1024-dimensional vectors (different from Nomic's 768d, but
  self-consistent — both sides use the same model)
- Endpoint: `POST https://api.voyageai.com/v1/embeddings`
- Auth: `Authorization: Bearer {key}` header
- No prefix convention (unlike Nomic's `search_document:` / `search_query:` requirement)
- Batch API available for offline bulk embedding — no rate limits, 12h completion window

**Selected.**

---

## Decision

Migrate embedding provider from Nomic Atlas API to Voyage AI `voyage-4-lite`.

### Implementation

**New file: `src/shared/Providers/Embeddings/VoyageEmbeddingProvider.cs`**
- Implements `IEmbeddingProvider` — same interface as Nomic, HuggingFace, Ollama
- ~30 lines — structurally identical to `NomicEmbeddingProvider.cs`
- Default model: `voyage-4-lite`
- Retry logic: up to 5 attempts, respects `Retry-After` header on 429, exponential
  backoff fallback

**Updated: `src/api/ServiceRegistration.cs`**
- Added `voyage` registration block alongside existing providers
- Config: `EmbeddingProvider=voyage`, `Voyage__ApiKey=...`, `Voyage__Model=voyage-4-lite`
- Zero agent code changes

**Offline embedding: Voyage Batch API**

Standard Voyage API has 3 RPM on the free tier. 52k recipes at batch size 50 would
take ~5.8 hours with constant 429 errors. The Batch API has no RPM limit:

1. Prepare `batch_input.jsonl` — 1,044 requests × 100 recipes each
2. Upload to Voyage Files API
3. Submit batch job (`endpoint: /v1/embeddings`, `model: voyage-4-lite`, `window: 12h`)
4. Poll until complete (completed in ~12 minutes for 52k recipes)
5. Download results, convert to `recipe_vectors_voyage.jsonl`
6. Reload Qdrant: delete 768d collection, create 1024d, upload 52,155 points

**Qdrant collection change:**
- Old: `size=768` (nomic-embed-text-v1 dimensions)
- New: `size=1024` (voyage-4-lite dimensions)
- `load_qdrant.py` auto-detects dimension from first document — no script changes needed

**Config change (Railway):**
```
EmbeddingProvider=voyage
Voyage__ApiKey=pa-...
Voyage__Model=voyage-4-lite
```

---

## Architecture Validation

This is the second provider swap for embeddings (HuggingFace → Nomic was Month 2,
Nomic → Voyage is Month 4). Both validated the `IEmbeddingProvider` abstraction:

| Change | Agent code changed | Provider files changed | Config changed |
|---|---|---|---|
| HuggingFace → Nomic (Month 2) | 0 files | 1 new file | env vars |
| Nomic → Voyage (Month 4) | 0 files | 1 new file | env vars |

**Zero agent changes across two provider migrations.** The abstraction held.

---

## Rate Limit Constraint

Voyage free tier: 3 RPM (requests per minute) at query time. This creates a known
failure mode for meal plan generation:

- A 7-day dinner plan fires 7 embedding calls sequentially
- A 7-day breakfast/lunch/dinner plan fires 21 calls
- At 3 RPM, 21 calls exhaust the rate limit window immediately
- The retry logic in `VoyageEmbeddingProvider.cs` handles individual 429s but cannot
  recover from sustained saturation across 21 concurrent calls

**Mitigation:** Retry with `Retry-After` header respect. The plan eventually completes
as the rate limit window resets between retries.

**Fix paths considered:**

| Option | Effect | Decision |
|---|---|---|
| Throttle planner (Task.Delay 20s between slots) | Plan takes 7+ minutes | Rejected — bad UX |
| Redis-backed embedding cache | Helps repeat searches, not fresh meal plans | Deferred |
| Voyage paid tier | No rate limit, ~$0.02/1M tokens | Deferred — not needed at demo traffic |
| Accept + retry (current) | Plans succeed eventually | Accepted |

---

## Eval Impact

Re-running the RAGAS retrieval eval after migration (100-question golden dataset):

| Metric | Nomic baseline (spell_check) | Voyage 52k | Delta |
|---|---|---|---|
| context_relevance | 0.524 | 0.578 | ↑ +0.054 |
| faithfulness | 0.444 | 0.477 | ↑ +0.033 |
| answer_relevancy | 0.234 | 0.279 | ↑ +0.045 |

Overall improvement driven by corpus size increase (10k → 52k) more than embedding
model quality. Category-level regressions observed for negation (-0.223) and x_free
(-0.138) — `voyage-4-lite` encodes exclusion queries differently than Nomic. Post-retrieval
filtering in `QueryPreprocessor.cs` is correct but cannot rescue weaker initial candidates.
Documented as tech debt S-6.

---

## Consequences

**Positive:**
- Production restored — query-time embedding working
- 200M free tokens sufficient for current corpus and future expansion
- Batch API enables future re-embedding without rate limit constraint
- Third provider swap validated with zero agent changes

**Negative:**
- Dimension changed from 768d → 1024d — requires Qdrant collection recreation
- 3 RPM free tier causes meal plan 429 cascade (documented, mitigated)
- negation/x_free RAGAS regression — embedding model encodes exclusion differently
- `VoyageEmbeddingProvider.cs` default model string needs updating if Voyage releases
  newer free-tier models

**Neutral:**
- No prefix convention for Voyage (unlike Nomic's `search_document:` / `search_query:`)
  — simpler code, different semantic behavior
- `NomicEmbeddingProvider.cs` retained — correct provider for environments with Nomic
  API access

---

## Related ADRs

- ADR-002: Ollama over Azure OpenAI (provider-agnostic LLM)
- ADR-012: Cloud deployment strategy (Nomic selected as original cloud embedding provider)