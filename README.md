# README changes to make

## 1. Replace the badge line at top with:
![CI](https://github.com/aayushmdesai/ChefAgent/actions/workflows/ci.yml/badge.svg)

## 2. Add Live Demo section after the intro paragraph:

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

## 3. Update Current Status table — replace "Month 3 (Week 12)" row:

| **Cloud Deployment** | ✅ Week 12 complete | Railway API + Vercel UI, 21x LLM speedup (Groq), zero agent code changed |

## 4. Update Latest tag line:
**Latest tag:** `v1.0.0`

## 5. Update Tech Stack table:

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

## 6. Update System performance table:

| Operation | Local | Cloud | Notes |
|---|---|---|---|
| SearchRecipe | ~100ms | ~499ms cold / ~65ms warm | Nomic embed + Qdrant Cloud |
| SearchRecipe (cache hit) | ~12ms | ~65ms | Embedding cache, skip embed |
| GetMealPlan | ~15ms | ~15ms | Redis only |
| CreateMealPlan | ~640ms | ~1,118ms | 7 Qdrant searches |
| GeneralQuestion | ~14,000ms | ~340ms | **21x faster via Groq** |
| Full pipeline + diet LLM | — | ~739ms | Groq LLM validation included |
| Load test p50 (10 concurrent) | — | 4,314ms | Upstash cold start dominates |

## 7. Update Roadmap table:

| Month | Focus | Status |
|---|---|---|
| Month 1 (Weeks 1–4) | Recipe Agent, Diet Agent, Orchestrator, React UI | ✅ Complete |
| Month 2 (Weeks 5–8) | Planner Agent, Session Memory, Guardrails | ✅ Complete |
| Month 3 (Weeks 9–12) | Eval pipeline, Observability, Cloud deployment | ✅ Complete — v1.0.0 |
| Month 4 | MCP server, LinkedIn posts | 🔜 Planned |
| Month 5 | Portfolio site, resume, outreach | 🔜 Planned |

## 8. Add ADR-012 to the ADR list:
- [ADR-012: Cloud Deployment Strategy](docs/adrs/012-cloud-deployment.md)

## 9. Add to Quick Start — Cloud deployment section:

### Cloud deployment (Railway)

```bash
# All environment variables set as Railway Variables in dashboard
# Auto-deploys on push to main

# Required Railway Variables:
# Qdrant__Endpoint, Qdrant__ApiKey, Qdrant__CollectionName
# LlmProvider=groq, Groq__ApiKey, Groq__Model
# EmbeddingProvider=nomic, Nomic__ApiKey, Nomic__Model, Nomic__BaseUrl
# Redis__ConnectionString (Upstash)
# Langfuse__Enabled, Langfuse__BaseUrl, Langfuse__PublicKey, Langfuse__SecretKey
# ASPNETCORE_ENVIRONMENT=Production
```

## 10. Update Project Structure — add Week 12 files to src/shared/:
│       ├── ILlmProvider.cs                   # Chat provider interface + ChatMessage record
│       ├── IEmbeddingProvider.cs             # Embedding provider interface
│       ├── OllamaLlmProvider.cs              # Ollama chat implementation
│       ├── GroqProvider.cs                   # Groq OpenAI-compatible, 429 retry backoff
│       ├── OllamaEmbeddingProvider.cs        # Ollama embed, search_query: prefix
│       ├── HuggingFaceEmbeddingProvider.cs   # HF Inference API (DNS-blocked in prod)
│       └── NomicEmbeddingProvider.cs         # Nomic Atlas API, production embeddings