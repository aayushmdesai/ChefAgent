# ADR-012: Cloud Deployment Strategy

**Date:** June 2026  
**Status:** Accepted  
**Deciders:** Aayush Desai  
**Tags:** infrastructure, deployment, cloud, providers

---

## Context

ChefAgent was built with a local Docker stack: Qdrant, Ollama, Redis, Langfuse, Postgres, and the .NET API — six services, all running on an 8GB RAM Mac via Docker Compose. This worked for development but cannot serve as a public demo.

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
| Ollama Embeddings | HuggingFace Inference API | nomic-embed-text-v1, ~1,000 calls/day |
| Redis (Docker) | Upstash | 10K commands/day |
| Langfuse + Postgres | Langfuse Cloud | 50K observations/month |
| .NET API (Docker) | Railway | $5 trial credit, existing Dockerfile |
| React UI | Vercel | Free static hosting |

No single provider offers all of these. The composition approach lets each service use its natural free tier.

---

## Provider Abstraction — What Changed

The core claim of ADRs 001, 002, and 003 was that ChefAgent was designed to be provider-agnostic. Week 12 tests that claim.

**What actually changed to deploy to cloud:**

`docker-compose.yml` — environment variable updates only:
```yaml
- Qdrant__Endpoint=https://cluster.cloud.qdrant.io:6334
- Qdrant__ApiKey=${QDRANT_API_KEY}
- LlmProvider=groq
- Groq__ApiKey=${GROQ_API_KEY}
- EmbeddingProvider=huggingface
- HuggingFace__ApiKey=${HUGGINGFACE_API_KEY}
```

`ServiceRegistration.cs` — provider selection logic added (one file):
```csharp
services.AddSingleton<ILlmProvider>(sp =>
    config["LlmProvider"] == "groq"
        ? new GroqProvider(httpClient, apiKey, model)
        : new OllamaLlmProvider(httpClient, ollamaUrl, chatModel));
```

**Zero changes to:**
- Any agent (`RecipeSearchPlugin`, `DietValidationPlugin`, `AgentOrchestrator`, `IntentRouter`, `MealPlannerPlugin`)
- Any domain model
- Any evaluation harness
- Any guardrail

The abstraction held. Config-only swap, not a rewrite.

---

## Performance Impact

| Operation | Local (Ollama CPU) | Cloud (Groq) | Delta |
|-----------|-------------------|--------------|-------|
| GeneralQuestion | ~14,000ms | 651ms | **21x faster** |
| Recipe search | ~1,500ms | ~1,361ms | Similar |
| GetMealPlan (Redis) | ~1ms | ~1ms | No change |

The GeneralQuestion improvement is significant. Local Ollama on CPU-only hardware with a 3B parameter model took 14 seconds. Groq's LPU hardware running Llama 3.3 70B (a larger, better model) took 651ms. The provider abstraction enabled this improvement with zero agent changes.

---

## Embedding Compatibility

Stored vectors were generated with `nomic-embed-text-v1` via Ollama (Google Colab for the initial batch). HuggingFace Inference API also offers `nomic-ai/nomic-embed-text-v1`. Using the same model for query-time embedding preserves vector space compatibility — cosine similarity scores remain meaningful.

Switching to a different embedding model would require re-embedding all 10K stored vectors. The model name is therefore a constraint on provider selection, not an arbitrary choice.

Both `OllamaEmbeddingProvider` and `HuggingFaceEmbeddingProvider` apply the `search_query:` prefix internally — callers pass clean text. This encapsulation means the nomic-embed-text convention is enforced at the implementation layer, not scattered across call sites.

---

## 429 Handling — Groq vs Circuit Breaker

Groq's free tier rate limits return HTTP 429. This is a different failure mode from Ollama CPU timeouts:

- **Groq 429** = service healthy, caller too fast → retry with exponential backoff (1s, 2s, 4s)
- **Ollama timeout** = service saturated or unavailable → circuit breaker opens

`GroqProvider` handles 429 internally with retry logic. The circuit breaker in callers remains unchanged — it fires on timeouts and 5xx errors, not on 429s. These are correctly treated as separate resilience concerns.

---

## Free Tier Limitations

These constraints apply in the current deployment. A production version would address each:

| Limitation | Current | Production |
|-----------|---------|------------|
| Groq rate limit | 1,000 req/day | Paid tier or Cerebras |
| HuggingFace cold start | 10-20s first call | Dedicated endpoint or Jina AI |
| Upstash commands | 10K/day | Upstash paid or managed Redis |
| Qdrant storage | 1GB free | Qdrant Cloud paid cluster |
| Railway hosting | $5 trial | Railway paid or Fly.io |
| Render cold start | 30s (free tier) | Paid instance |

For a portfolio demo with occasional traffic, free tiers are sufficient. The pre-warm on API startup (dummy embed call) addresses the HuggingFace cold start for real user requests.

---

## Secrets Management

Secrets are never committed to the repository. The pattern across all environments:

**Local development:** `.env.local` (gitignored) → `docker compose --env-file .env.local up`

**Railway/Render:** Platform secret management → environment variables injected at runtime

**Codespaces:** GitHub Codespaces secrets → auto-injected as environment variables

`docker-compose.yml` references secrets as `${VAR_NAME}`. The file itself contains no credentials. `appsettings.json` contains only localhost defaults for `dotnet run` outside Docker.

---

## What Production Would Look Like

The free-tier composition demonstrates the architecture. A production deployment would upgrade each component:

- **Qdrant:** Dedicated cluster with persistence SLA
- **LLM:** Groq paid tier or self-hosted vLLM on GPU
- **Embeddings:** HuggingFace dedicated endpoint or Jina AI
- **Redis:** Managed Redis (Upstash Pro, Redis Cloud, or AWS ElastiCache)
- **Observability:** Langfuse Cloud Pro or self-hosted with ClickHouse
- **Hosting:** Railway Pro, Fly.io, or Azure Container Apps

The application code would not change. Only environment variables and service tiers.

---

## Consequences

**Positive:**
- System accessible at public URL without Docker or Ollama
- 21x latency improvement on LLM calls (14s → 651ms)
- Architecture claims from ADRs 001-003 validated — zero agent code changed
- Free tier composition keeps portfolio demo cost at $0

**Negative:**
- Free tier rate limits impose daily request caps
- HuggingFace embedding cold starts (mitigated by pre-warm)
- Codespaces egress restrictions prevent local HuggingFace testing — must verify on Railway
- Railway $5 credit depletes over time; need monitoring

**Neutral:**
- `IntentRouter`, `QueryPreprocessor`, `RecipeReranker` still call Ollama directly — deferred to Month 4 cleanup. These are opt-in LLM paths that rarely fire in normal use; deferring them was a deliberate scope decision, not an oversight.

---

## Related ADRs

- ADR-001: Qdrant over Azure AI Search (provider-agnostic vector DB)
- ADR-002: Ollama over Azure OpenAI (provider-agnostic LLM)
- ADR-003: LangGraph/Semantic Kernel over Azure AI Agent Service
- ADR-010: Langfuse v2 for observability
- ADR-011: Evaluation strategy (RAGAS + e2e + LLM judge)