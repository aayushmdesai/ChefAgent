# ADR-012: Cloud Deployment Strategy

**Date:** June 2026  
**Status:** Accepted  
**Deciders:** Aayush Desai  
**Tags:** infrastructure, deployment, cloud, providers

---

## Context

ChefAgent was built with a local Docker stack: Qdrant, Ollama, Redis, Langfuse, Postgres, and the .NET API — six services, all running in GitHub Codespaces via Docker Compose. This worked for development but cannot serve as a public demo.

Month 3, Week 12 is the deployment week. The goal: make ChefAgent accessible at a public URL with zero local dependencies, using only free-tier cloud services.

Three constraints shaped the decision:

1. **No paid services.** Every service must have a workable free tier.
2. **No rewrites.** The architecture was explicitly designed to be provider-agnostic. Week 12 is the proof — if agent code has to change, the abstraction failed.
3. **Portfolio visibility.** The system must be accessible without Docker, Ollama, or any local setup.

---

## Decision

Deploy ChefAgent using a composition of free-tier cloud services, one per local service:

| Local | Cloud | Free Tier |
|-------|-------|-----------|
| Qdrant (Docker) | Qdrant Cloud | 1GB cluster, no expiry |
| Ollama LLM | Groq | Llama 3.3 70B, 1,000 req/day |
| Ollama Embeddings | Nomic Atlas API | nomic-embed-text-v1, free tier |
| Redis (Docker) | Upstash | 10K commands/day |
| Langfuse + Postgres | Langfuse Cloud | 50K observations/month |
| .NET API (Docker) | Railway | $5 trial credit, existing Dockerfile |
| React UI | Vercel | Free static hosting |

No single provider offers all of these. The composition approach lets each service use its natural free tier.

---

## Embedding Provider Selection — Decision Trail

The initial plan was HuggingFace Inference API (`api-inference.huggingface.co`) for embeddings. This was implemented as `HuggingFaceEmbeddingProvider.cs` and wired correctly. During deployment it was discovered that `api-inference.huggingface.co` is blocked at DNS level in both Codespaces and Railway:

```
curl: (6) Could not resolve host: api-inference.huggingface.co
```

This is not a code issue — the provider implementation is correct. Both environments block the domain at the network layer. HuggingFace Inference Endpoints (dedicated GPU instances) would work but violate the free-tier constraint.

**Decision: Nomic Atlas API.** Nomic built `nomic-embed-text` and hosts it at `api-atlas.nomic.ai`, which is reachable from both Codespaces and Railway. Key properties:

- Same model (`nomic-embed-text-v1`) as stored vectors — zero re-embedding, zero vector space mismatch
- Same `search_query:` prefix convention — `NomicEmbeddingProvider` is a drop-in replacement
- Free tier sufficient for portfolio demo traffic
- `HuggingFaceEmbeddingProvider` retained in codebase for environments without this DNS restriction

Definitive verification — Ollama stopped, Nomic returns results:
```bash
docker compose stop ollama
curl .../chat → "Homemade Pasta"  ✅
```

---

## Provider Abstraction — What Changed

The core claim of ADRs 001, 002, and 003 was that ChefAgent was designed to be provider-agnostic. Week 12 tests that claim.

**What actually changed to deploy to cloud:**

`docker-compose.local.yml` — environment variable updates only:
```yaml
- Qdrant__Endpoint=https://cluster.cloud.qdrant.io:6334
- Qdrant__ApiKey=${QDRANT_API_KEY}
- LlmProvider=groq
- Groq__ApiKey=${GROQ_API_KEY}
- EmbeddingProvider=nomic
- Nomic__ApiKey=${NOMIC_API_KEY}
- Nomic__Model=nomic-embed-text-v1
- Nomic__BaseUrl=https://api-atlas.nomic.ai/v1/embedding/text
- Redis__ConnectionString=${UPSTASH_CONNECTION_STRING}
- Langfuse__BaseUrl=https://us.cloud.langfuse.com
- Langfuse__PublicKey=${LANGFUSE_PUBLIC_KEY}
- Langfuse__SecretKey=${LANGFUSE_SECRET_KEY}
```

`ServiceRegistration.cs` — provider selection logic added (one file):
```csharp
services.AddSingleton<ILlmProvider>(sp =>
    config["LlmProvider"] == "groq"
        ? new GroqProvider(httpClient, apiKey, model)
        : new OllamaLlmProvider(httpClient, ollamaUrl, chatModel));

services.AddSingleton<IEmbeddingProvider>(sp =>
    config["EmbeddingProvider"] switch
    {
        "nomic" => new NomicEmbeddingProvider(httpClient, apiKey, model, baseUrl),
        "huggingface" => new HuggingFaceEmbeddingProvider(httpClient, apiKey, model, baseUrl),
        _ => new OllamaEmbeddingProvider(httpClient, ollamaUrl, embeddingModel)
    });
```

**Zero changes to:**
- Any agent (`RecipeSearchPlugin`, `DietValidationPlugin`, `AgentOrchestrator`, `IntentRouter`, `MealPlannerPlugin`)
- Any domain model
- Any evaluation harness
- Any guardrail

The abstraction held. Config-only swap, not a rewrite.

---

## Performance Impact

| Operation | Local (Ollama CPU) | Cloud | Delta |
|-----------|-------------------|-------|-------|
| GeneralQuestion | ~14,000ms | 651ms (Groq) | **21x faster** |
| Recipe search | ~1,500ms | ~1,361ms (Nomic + Qdrant Cloud) | Similar |
| GetMealPlan (Redis) | ~1ms | ~1ms (Upstash) | No change |
| Nomic embed (cold) | N/A | ~615ms | Cached on repeat |

The GeneralQuestion improvement is the headline result. Local Ollama on CPU with a 3B parameter model took 14 seconds. Groq's LPU hardware running Llama 3.3 70B — a larger, better model — took 651ms. The provider abstraction enabled this improvement with zero agent changes.

---

## Embedding Compatibility

Stored vectors were generated with `nomic-embed-text-v1` via Ollama. Nomic Atlas API serves the same model. Using the same model for query-time embedding preserves vector space compatibility — cosine similarity scores remain meaningful.

Switching to a different embedding model would require re-embedding all 10K stored vectors. The model name is therefore a hard constraint on provider selection, not an arbitrary choice. This constraint eliminated Jina AI (different model, different dimensions) and confirmed Nomic as the correct cloud provider.

All embedding providers (`OllamaEmbeddingProvider`, `HuggingFaceEmbeddingProvider`, `NomicEmbeddingProvider`) apply the `search_query:` prefix internally. Callers pass clean text. The nomic-embed-text convention is enforced at the implementation layer, not scattered across call sites.

---

## 429 Handling — Groq vs Circuit Breaker

Groq's free tier rate limits return HTTP 429. This is a different failure mode from Ollama CPU timeouts:

- **Groq 429** = service healthy, caller too fast → retry with exponential backoff (1s, 2s, 4s) inside `GroqProvider`
- **Ollama timeout** = service saturated or unavailable → circuit breaker opens in callers

The circuit breaker was not modified — it continues to fire on timeouts and 5xx errors. 429 handling is isolated to `GroqProvider`. Conflating them would make the circuit breaker either too sensitive (opening on transient rate limits) or too lenient (retrying a crashed service).

---

## Named HTTP Clients

Two named `HttpClient` registrations separate local from cloud concerns:

```csharp
services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromSeconds(120));
services.AddHttpClient("Cloud", client => client.Timeout = TimeSpan.FromSeconds(30));
```

`"Ollama"` — 120s timeout for CPU inference (slow by design).
`"Cloud"` — 30s timeout for Groq, Nomic, HuggingFace (fast; 30s failure = real problem).

Using the wrong client would either timeout cloud providers too aggressively or wait 120s on a crashed local service.

---

## Secrets Management

Secrets are never committed to the repository.

**Local development:** `.env.local` (gitignored) → injected via `env_file: .env.local` in `docker-compose.local.yml`. Direct injection avoids `${VAR}` substitution naming mismatch issues.

**Railway:** Platform Variables UI → injected as environment variables at runtime.

**Codespaces:** GitHub Codespaces Secrets → auto-injected on Codespace start.

`appsettings.json` contains only localhost defaults for `dotnet run` outside Docker. All runtime config comes from environment variables.

---

## Lean Local Stack

`docker-compose.local.yml` drops services now running on cloud:

| Service | docker-compose.yml | docker-compose.local.yml |
|---------|-------------------|--------------------------|
| API | ✅ | ✅ |
| Ollama | ✅ | ✅ (fallback embedding) |
| Redis | ✅ | ❌ → Upstash |
| Qdrant | ✅ | ❌ → Qdrant Cloud |
| Langfuse + Postgres | ✅ | ❌ → Langfuse Cloud |

`make up` → lean stack (API + Ollama, everything else cloud).
`make up-full` → full local stack (all 6 containers, for offline dev).

---

## Railway Deployment Notes

- **Builder:** Dockerfile (not Railpack). Railpack's auto-detector scans the repo root and can't find .NET in `src/`. Must specify `infra/docker/Dockerfile.api` explicitly.
- **Auto-deploy:** On push to main branch.
- **Public URL:** `https://chefagent-production.up.railway.app`

---

## Smoke Test Results (Railway)

| Intent | Query | Result |
|--------|-------|--------|
| SearchRecipe | "find me pasta recipes" | ✅ "Homemade Pasta" |
| GeneralQuestion | "what is blanching?" | ✅ Correct answer via Groq |
| CreateMealPlan | "plan my dinners for the week" | ✅ 7-day plan, Redis session persisted |
| Guardrail | injection attempt | ✅ Blocked |

---

## Free Tier Limitations

| Limitation | Current | Production |
|-----------|---------|------------|
| Groq rate limit | 1,000 req/day | Paid tier or Cerebras |
| Nomic API | Free tier | Nomic paid or self-hosted |
| Upstash commands | 10K/day | Upstash paid or managed Redis |
| Qdrant storage | 1GB free | Qdrant Cloud paid cluster |
| Railway hosting | $5 trial credit | Railway paid or Fly.io |

For a portfolio demo with occasional traffic, free tiers are sufficient.

---

## What Production Would Look Like

The free-tier composition demonstrates the architecture. A production deployment would upgrade each component without code changes:

- **Qdrant:** Dedicated cluster with persistence SLA
- **LLM:** Groq paid tier or self-hosted vLLM on GPU
- **Embeddings:** Nomic paid tier or self-hosted nomic-embed-text
- **Redis:** Upstash Pro or AWS ElastiCache
- **Observability:** Langfuse Cloud Pro or self-hosted with ClickHouse
- **Hosting:** Railway Pro, Fly.io, or Azure Container Apps

Only environment variables change. No code changes.

---

## Consequences

**Positive:**
- System live at public URL, zero local dependencies
- 21x LLM latency improvement (14,000ms → 651ms)
- Architecture claims from ADRs 001-003 validated — zero agent code changed
- Free tier composition: $0 cost for portfolio demo

**Negative:**
- Free tier daily limits (Groq 1K req, Upstash 10K cmd)
- `api-inference.huggingface.co` blocked in Codespaces and Railway — documented, `HuggingFaceEmbeddingProvider` retained for other environments
- Railway $5 credit depletes; needs monitoring

**Neutral:**
- `IntentRouter`, `QueryPreprocessor`, `RecipeReranker` still call Ollama directly — deferred to Month 4. These are opt-in LLM paths that rarely fire in normal use.

---

## Related ADRs

- ADR-001: Qdrant over Azure AI Search (provider-agnostic vector DB)
- ADR-002: Ollama over Azure OpenAI (provider-agnostic LLM)
- ADR-003: LangGraph/Semantic Kernel over Azure AI Agent Service
- ADR-010: Langfuse v2 for observability
- ADR-011: Evaluation strategy (RAGAS + e2e + LLM judge)