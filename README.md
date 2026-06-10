![CI](https://github.com/aayushmdesai/ChefAgent/actions/workflows/ci.yml/badge.svg)

# ChefAgent

A multi-agent AI cooking assistant built with C#/.NET and Semantic Kernel. Ask it to find recipes, validate dietary restrictions, or plan a week of meals — in plain language.

**Latest tag:** `v1.0.0`

---

## Live Demo

| | URL |
|--|--|
| **Frontend** | https://chefagent.vercel.app |
| **API** | https://chefagent-production.up.railway.app |

```bash
# Try it now
curl -X POST https://chefagent-production.up.railway.app/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "find me a quick dairy-free pasta dinner", "sessionId": "demo"}'
```

---

## Current Status

| Milestone | Status | Notes |
|---|---|---|
| **RAG Pipeline** | ✅ Week 1 complete | 10K recipes, Qdrant vector search, Nomic embeddings |
| **Multi-stage Retrieval** | ✅ Week 2 complete | Negation parsing, spell correction, query expansion |
| **Diet Agent** | ✅ Week 3 complete | 420+ phrase rules, 12 dietary categories, LLM fallback |
| **Orchestrator + UI** | ✅ Week 4 complete | IntentRouter, /chat endpoint, React frontend |
| **Planner + Memory** | ✅ Weeks 5–6 complete | 7-day meal plans, Redis session memory, profile persistence |
| **Guardrails** | ✅ Week 7 complete | Input/output guard, circuit breaker, rate limiter, audit log |
| **CI + Perf baseline** | ✅ Week 8 complete | GitHub Actions, load test (10 concurrent), failure mode matrix |
| **Eval pipeline** | ✅ Week 9 complete | RAGAS-style scorer, 100-query golden dataset, spell correction |
| **Observability** | ✅ Week 10 complete | Langfuse tracing, 14 span types, embedding cache (1813ms → 12ms) |
| **E2E eval + Negation fix** | ✅ Week 11 complete | 60-case harness, LLM judge, semantic X-free expansion |
| **Cloud Deployment** | ✅ Week 12 complete | Railway API + Vercel UI, 21x LLM speedup (Groq), zero agent code changed |

---

## Architecture

```
User message (React UI)
        │
        ▼
POST /chat  (ASP.NET Core Minimal API)
        │
        ├─ InputGuard          — length, injection detection (two-signal)
        │
        ├─ IntentRouter        — rules-based classification, < 1ms
        │       SearchRecipe · ValidateDiet · CreateMealPlan
        │       ModifyMealPlan · GetMealPlan · GeneralQuestion
        │
        ├─ AgentOrchestrator
        │       ├─ Recipe Agent     — RAG: embed → Qdrant → rerank
        │       ├─ Diet Agent       — 420+ rules → LLM fallback
        │       └─ Planner Agent    — 7× Recipe Agent, Redis persistence
        │
        ├─ OutputGuard         — response validation, confidence signals
        │
        └─ OrchestratorResponse
                recipes + dietary notes + meal plan + confidence flag
```

**Provider abstraction:** `ILlmProvider` and `IEmbeddingProvider` interfaces decouple agents from specific LLM/embedding backends. Cloud migration (Ollama → Groq + Nomic) required zero agent code changes.

---

## Tech Stack

| Layer | Technology |
|---|---|
| **Orchestration** | Semantic Kernel (C#) |
| **LLM** | Groq — Llama 3.3 70B (cloud) / Ollama llama3.2 (local fallback) |
| **Embeddings** | Nomic Atlas API — nomic-embed-text-v1 (cloud) / Ollama (local fallback) |
| **Vector DB** | Qdrant Cloud (1GB free) / Qdrant Docker (local) |
| **Session state** | Upstash Redis (cloud) / Redis Docker (local) |
| **Spell correction** | SymSpell + food domain dictionary |
| **Backend** | ASP.NET Core Minimal API (.NET 8) |
| **Frontend** | React + Tailwind (Vite), deployed on Vercel |
| **Observability** | Langfuse Cloud (50K obs/month free) |
| **Eval** | RAGAS-style scorer + LLM judge + Colab GPU runner |
| **Hosting** | Railway (API) + Vercel (frontend) |

---

## Performance

| Operation | Local | Cloud | Notes |
|---|---|---|---|
| SearchRecipe | ~100ms | ~499ms cold / ~65ms warm | Nomic embed + Qdrant Cloud |
| SearchRecipe (cache hit) | ~12ms | ~65ms | Embedding cache, skip embed |
| GetMealPlan | ~15ms | ~15ms | Redis only |
| CreateMealPlan | ~640ms | ~1,118ms | 7 Qdrant searches |
| GeneralQuestion | ~14,000ms | ~340ms | **21x faster via Groq** |
| Full pipeline + diet LLM | — | ~739ms | Groq LLM validation included |
| Load test p50 (10 concurrent) | — | 4,314ms | Upstash cold start dominates |

---

## Quick Start

### Local development

Requires Docker and .NET 8 SDK.

```bash
git clone https://github.com/aayushmdesai/ChefAgent.git
cd ChefAgent
docker compose up -d          # Qdrant + Redis
ollama pull nomic-embed-text  # embeddings
ollama pull llama3.2          # LLM
dotnet run --project src/api
```

The API runs on `http://localhost:5000`. The React frontend is in `frontend/`.

### Cloud deployment (Railway)

```bash
# All environment variables set as Railway Variables in the dashboard.
# Auto-deploys on push to main.

# Required Railway Variables:
# Qdrant__Endpoint, Qdrant__ApiKey, Qdrant__CollectionName
# LlmProvider=groq, Groq__ApiKey, Groq__Model
# EmbeddingProvider=nomic, Nomic__ApiKey, Nomic__Model, Nomic__BaseUrl
# Redis__ConnectionString (Upstash)
# Langfuse__Enabled, Langfuse__BaseUrl, Langfuse__PublicKey, Langfuse__SecretKey
# ASPNETCORE_ENVIRONMENT=Production
```

---

## Evaluation

Three eval layers, each catching different failure modes:

| Layer | What it measures | Tool |
|---|---|---|
| Retrieval quality | Does vector search find the right documents? | RAGAS-style scorer (Colab GPU) |
| Pipeline correctness | Does the full system route and respond correctly? | E2E harness, 60 cases |
| Response quality | Is the answer actually helpful? | LLM-as-judge |

**Results (v1.0.0):** 86% e2e pass rate (after cascading failure exclusion). Remaining failures trace to intent classifier vocabulary gaps — question-form `ValidateDiet`, informal `CreateMealPlan` phrasing.

Run the eval pipeline:
```bash
cd eval
python harnesses/retrieve.py          # local retrieval step
# upload retrieved_contexts.json to Colab, run score_simple.py
python harnesses/eval_e2e.py          # full pipeline e2e
python harnesses/llm_judge.py         # subjective quality scoring
```

---

## Project Structure

```
ChefAgent/
├── src/
│   ├── api/                          # ASP.NET Core Minimal API, endpoints, DI
│   ├── agents/
│   │   ├── Orchestrator/             # IntentRouter, AgentOrchestrator
│   │   ├── RecipeAgent/              # RAG pipeline, QueryPreprocessor, embeddings
│   │   ├── DietAgent/                # DietValidationPlugin, DietaryRules
│   │   └── PlannerAgent/             # MealPlanGenerator, slot modification
│   ├── shared/
│   │   ├── Guards/                   # InputGuard, OutputGuard
│   │   ├── Resilience/               # CircuitBreaker, RateLimiter
│   │   ├── Tracing/                  # Langfuse client, MetricsCollector
│   │   ├── SessionStore.cs           # Redis abstraction
│   │   ├── ILlmProvider.cs           # Chat provider interface + ChatMessage record
│   │   ├── IEmbeddingProvider.cs     # Embedding provider interface
│   │   ├── OllamaLlmProvider.cs      # Ollama chat implementation
│   │   ├── GroqProvider.cs           # Groq OpenAI-compatible, 429 retry backoff
│   │   ├── OllamaEmbeddingProvider.cs # Ollama embed, search_query: prefix
│   │   ├── HuggingFaceEmbeddingProvider.cs # HF Inference API (DNS-blocked in prod)
│   │   └── NomicEmbeddingProvider.cs # Nomic Atlas API, production embeddings
│   └── tests/                        # xUnit, 80+ tests
├── frontend/                         # React + Tailwind (Vite)
├── eval/
│   ├── datasets/                     # golden datasets, experiment results
│   └── harnesses/                    # retrieve.py, score_simple.py, eval_e2e.py, llm_judge.py
├── docs/
│   └── adrs/                         # Architecture Decision Records
└── docker-compose.yml                # Local: Qdrant + Redis
```

---

## Architecture Decision Records

| ADR | Decision |
|---|---|
| [ADR-001](docs/adrs/001-semantic-kernel.md) | Semantic Kernel over raw HTTP clients |
| [ADR-002](docs/adrs/002-qdrant.md) | Qdrant as vector database |
| [ADR-003](docs/adrs/003-ollama.md) | Ollama for local LLM + embeddings |
| [ADR-004](docs/adrs/004-diet-rules-over-llm.md) | Rules-first dietary validation |
| [ADR-005](docs/adrs/005-intent-router.md) | Rules-based intent classification |
| [ADR-006](docs/adrs/006-redis-session.md) | Redis for session memory |
| [ADR-007](docs/adrs/007-circuit-breaker.md) | Circuit breaker for LLM resilience |
| [ADR-008](docs/adrs/008-ci-pipeline.md) | GitHub Actions CI |
| [ADR-009](docs/adrs/009-evaluation-pipeline.md) | RAGAS-style evaluation pipeline |
| [ADR-010](docs/adrs/010-langfuse-observability.md) | Langfuse for observability |
| [ADR-011](docs/adrs/011-evaluation-strategy.md) | Three-layer evaluation strategy |
| [ADR-012](docs/adrs/012-cloud-deployment.md) | Cloud deployment strategy |

---

## Roadmap

| Month | Focus | Status |
|---|---|---|
| Month 1 (Weeks 1–4) | Recipe Agent, Diet Agent, Orchestrator, React UI | ✅ Complete |
| Month 2 (Weeks 5–8) | Planner Agent, Session Memory, Guardrails | ✅ Complete |
| Month 3 (Weeks 9–12) | Eval pipeline, Observability, Cloud deployment | ✅ Complete — v1.0.0 |
| Month 4 | MCP server (`mcp-dotnet-diagnostics`), LinkedIn posts | 🔜 In progress |
| Month 5 | Portfolio site, resume, outreach | 🔜 Planned |

---

## Known Limitations

| Item | Detail |
|---|---|
| `GeneralQuestion` statelessness | Loses context across turns — "how to make it" loses its reference |
| IntentRouter vocabulary gaps | Question-form `ValidateDiet`, informal `CreateMealPlan` phrasing |
| Nut-free retrieval depth | Filter reduces candidate pool too aggressively for some queries |
| Upstash cold start | ~3,000ms first Redis call per session — pre-warm on API startup would fix it |

---

## Related

- **mcp-dotnet-diagnostics** — companion project: MCP server exposing .NET runtime diagnostics to AI assistants. [GitHub](https://github.com/aayushmdesai/mcp-dotnet-diagnostics) · [NuGet](https://www.nuget.org/packages/mcp-dotnet-diagnostics)

---

## License

MIT