![CI](https://github.com/aayushmdesai/ChefAgent/actions/workflows/ci.yml/badge.svg)

# ChefAgent 🍳

A multi-agent system where specialized AI agents collaborate to handle recipe search, dietary reasoning, and meal planning — built with production-grade orchestration, guardrails, and observability.

**100% open-source stack. No cloud subscriptions required.**

---

## Current Status

| Component | Status | Notes |
|---|---|---|
| **Recipe Agent** | ✅ Week 2 complete | Multi-stage retrieval — negation, filtering, reranking, spell-check, embedding cache |
| **Diet Agent** | ✅ Week 3 complete | Two-layer validation + substitutions (94% rules coverage) |
| **Orchestrator** | ✅ Week 4 complete | Intent routing, /chat endpoint, React UI |
| **Planner Agent** | ✅ Week 5 complete | Stateful meal plans, Redis session memory, multi-slot, swap UI |
| **Memory & State** | ✅ Week 6 complete | Conversation history, profile persistence, reference resolution |
| **Guardrails** | ✅ Week 7 complete | Input validation, output guard, circuit breaker, rate limiting, audit log |
| **Failure Matrix** | ✅ Week 8 complete | 36/36 failure modes tested, e2e sweep, 68 unit tests |
| **Eval Pipeline** | ✅ Week 9 complete | 100-query golden dataset, RAGAS-style eval, spell-check +0.175 |
| **Observability** | ✅ Week 10 complete | Self-hosted Langfuse v2, 14 span types, < 1ms overhead, p50/p95/p99 |
| **E2E Eval + Fixes** | ✅ Week 11 complete | Semantic negation fix, 60-case e2e harness, LLM judge, Month 3 report |

**Latest tag:** `v0.8.0`

---

## Evaluation

Three measurement layers. See [`eval/README.md`](eval/README.md) for full pipeline docs.

| Layer | Tool | What It Measures |
|---|---|---|
| Retrieval quality | RAGAS-style scorer | Vector search finds right documents |
| E2E quality | LLM judge (/chat) | Full pipeline produces useful responses |
| Performance | Langfuse traces | Latency per operation |

### Retrieval quality (RAGAS pipeline, 100 queries)

| Metric | Baseline | + Spell-Check | + Semantic Negation |
|--------|----------|---------------|---------------------|
| Context Relevance | 0.470 | 0.524 | 0.482 |
| Faithfulness | 0.489 | 0.444 | 0.488 |
| Answer Relevancy | 0.267 | 0.234 | 0.249 |

| Category | Baseline | Latest | Delta |
|---|---|---|---|
| misspelling | 0.442 | 0.617 | **+0.175** |
| x_free | 0.438 | 0.525 | **+0.087** |
| dietary | 0.408 | 0.475 | **+0.067** |
| exact_match | 0.525 | 0.588 | **+0.063** |
| by_ingredients | — | 0.700 | — |

### End-to-end quality (LLM judge, 60 cases via /chat)

| Category | Helpfulness | Safety | Coherence |
|---|---|---|---|
| general_question | **4.00** | N/A | **4.00** |
| search_with_diet | **4.00** | 3.30 | 3.91 |
| search_negation | **4.00** | N/A | 3.50 |
| search_simple | 3.75 | N/A | 3.62 |
| validate_diet | 3.33 | **2.50** | 3.56 |
| implicit_dietary | **2.33** | N/A | 3.17 |

E2E pass rate: **47/60 (78%)**, **49/57 (86%) adjusted** (excluding cascading failures).  
Intent accuracy: 78% — intent classifier is the weakest link; all other layers perform correctly once routed.

### System performance (Langfuse, Week 10)

| Operation | p50 | p95 | Notes |
|---|---|---|---|
| SearchRecipe | ~100ms | ~300ms | Embedding + Qdrant |
| SearchRecipe (cache hit) | ~12ms | ~12ms | Embedding cache, skip Ollama |
| GetMealPlan | ~15ms | ~15ms | Redis only |
| CreateMealPlan | ~640ms | ~2400ms | 7 sequential embedding calls |
| GeneralQuestion | ~9800ms | ~9800ms | Ollama CPU inference |
| Guardrail blocked | ~4ms | ~4ms | No agent calls |

Tracing overhead: **< 1ms per request**.

```bash
# Run retrieval eval
python eval/harnesses/retrieve.py
# upload to Colab, run score_simple.py, download result
python eval/harnesses/compare_experiments.py \
    eval/experiments/2026-06-01_baseline.json \
    eval/experiments/2026-06-07_spell_check.json

# Run e2e eval
python eval/harnesses/eval_e2e.py
python eval/harnesses/llm_judge.py
```

---

## Architecture

```
User /chat message
    │
    ├─ InputGuard           injection detection · sanitization · length limits
    ├─ RateLimiter          30 req/min per session · repeated query detection
    │
    ├─ IntentRouter         rules <1ms · LLM entity extraction (opt-in)
    │                       entity extraction cache (Redis) — 90s → 13ms on hit
    │
    ├─ AgentOrchestrator    routes by intent · history · profile persistence
    │       ├─ ResolveReferences   "the first one" → recipe title from history
    │       └─ AppendDisclaimer    confidence flags · allergy warnings
    │
    ├──────────────────────────────────────────────────┐
    │                    │                             │
    ▼                    ▼                             ▼
Recipe Agent         Diet Agent               Planner Agent
search · filter      rules 94%                generate · modify
rerank · expand      LLM fallback             variety enforcement
embedding cache      substitutions
    │                    │                             │
    ├── Qdrant            ├── Rules engine              ├── calls Recipe ×7
    │   10K vectors       │   420+ phrases              ├── calls Diet ×7
    │                     │   12 categories             │
    └── Ollama            └── Ollama                    └── Redis SessionStore
        nomic-embed-text      llama3.2 fallback             plan · profile · history
        in-memory cache       CircuitBreaker                TTL 7 days
        CircuitBreaker        OutputGuard
        OutputGuard
    │
    ├─ Langfuse (self-hosted v2)
    │   14 span types · fire-and-forget · < 1ms overhead
    │   embed.cache_hit / embed.ollama visible in traces
    │
    └─ OrchestratorResponse
           recipes · dietary validation · message · meal plan · confidence
```

---

## Guardrails (Week 7)

Five independent layers protect every `/chat` request.

```
Request → InputGuard → RateLimiter → RepeatCheck → Classify → Route
                                                        ↓
                                            CircuitBreaker (optional LLM)
                                            OutputGuard (LLM responses)
                                                        ↓
                                            AppendConfidenceDisclaimer
                                                        ↓
                                            GuardrailAuditLog
```

| Layer | Component | What it does |
|---|---|---|
| Input | `InputGuard` | Two-signal injection detection, sanitization, length limits |
| Output | `OutputGuard` | JSON schema validation, recipe sanity, retry/fallback |
| Resilience | `CircuitBreaker` | 3-state breaker — skips LLM after 3 failures, auto-recovers |
| Abuse | `RateLimiter` | 30 req/min per session, repeated query detection |
| Trust | Confidence + `GuardrailAuditLog` | Disclaimers, allergy warnings, observable audit trail |

Two circuit breakers: `ollama` (60s cooldown) and `redis` (30s cooldown).

---

## Memory & State (Week 6)

```
Session memory (Redis, TTL 7 days):
    session:{id}:plan       → MealPlan (JSON)
    session:{id}:profile    → DietaryProfile (JSON)
    session:{id}:history    → ConversationEntry[] (Redis list, 20-entry window)
    session:{id}:extraction → Cached LLM entity extraction result (JSON)
```

---

## Agents

### Recipe Agent (Weeks 1–2 + Weeks 9 + 11)

```
Query
  ├─ [0] Spell correction       SymSpell + food domain dict
  ├─ [1] Negation parsing       regex, instant — "pasta without tomatoes" → excluded: ["tomatoes"]
  ├─ [2] X-free expansion       "dairy-free" → 35 ingredient exclusions via DietaryRules
  ├─ [3] Query expansion        Ollama opt-in — "cozy" → "soup, stew, chili"
  ├─ [4] Embedding cache        ConcurrentDictionary — cache hit skips Ollama (~12ms vs ~1800ms)
  ├─ [5] Qdrant vector search   cosine similarity + maxIngredients/maxSteps filter
  ├─ [6] OutputGuard sanity     drops empty titles, missing ingredients, score < 0.3
  ├─ [7] Negation filter        removes excluded ingredients post-retrieval
  └─ [8] LLM re-rank            Ollama opt-in
```

### Diet Agent (Week 3)

Two-layer validation: fast rules engine → LLM fallback for edge cases.

**12 restriction categories:** dairy, gluten, nuts, eggs, soy, sesame, seafood, meat, jain, sattvic, paleo, halal/kosher

### Orchestrator (Week 4)

| Intent | Agents | Confidence |
|---|---|---|
| `SearchRecipe` (no profile) | Recipe Agent | High |
| `SearchRecipe` (with profile) | Recipe + Diet | Medium |
| `ValidateDiet` | Recipe + Diet | Medium |
| `GetMealPlan` | Redis read only | High |
| `CreateMealPlan` | Planner Agent | Medium |
| `ModifyMealPlan` | Planner Agent | Medium |
| `GeneralQuestion` | Ollama direct | Medium |

---

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /health` | Health check |
| `POST /chat` | Orchestrator — natural language → agents |
| `POST /recipes/search` | Recipe Agent only |
| `POST /recipes/search-validated` | Recipe Agent + Diet Agent |
| `GET /profile/{sessionId}` | Load stored dietary profile |
| `POST /profile/{sessionId}` | Save dietary profile |
| `GET /admin/guardrails` | Live guardrail audit log (last 50 events) |
| `GET /admin/metrics` | p50/p95/p99 latency per intent (5-min window) |

---

## Tech Stack

| Layer | Technology |
|---|---|
| **Orchestration** | Semantic Kernel (C#) |
| **LLM** | Ollama — llama3.2 (local, CPU) |
| **Embeddings** | Ollama — nomic-embed-text (768d) + in-memory cache |
| **Vector DB** | Qdrant (self-hosted, Docker) |
| **Session state** | Redis (Docker, TTL 7 days) |
| **Spell correction** | SymSpell + food domain dictionary |
| **Backend** | ASP.NET Core Minimal API |
| **Frontend** | React + Tailwind (Vite) |
| **Observability** | Langfuse v2 (self-hosted, Docker) |
| **Eval** | RAGAS-style scorer + LLM judge + Colab GPU runner |

---

## Quick Start

```bash
# 1. Clone and start infrastructure
git clone https://github.com/aayushmdesai/ChefAgent.git
cd ChefAgent
docker compose up -d        # Qdrant + Redis + Langfuse

# 2. Start Ollama natively
brew install ollama
ollama pull nomic-embed-text
ollama pull llama3.2

# 3. Data pipeline
python3 -m venv .venv && source .venv/bin/activate
pip install -r scripts/requirements.txt
python3 scripts/prepare_recipes.py
python3 scripts/load_qdrant.py
# GPU embedding: use chefagent_embeddings.ipynb on Google Colab

# 4. Run the API
cd src/api && dotnet run     # http://localhost:5100

# 5. Run the frontend
cd src/frontend && npm install && npm run dev   # http://localhost:5173

# 6. Test
curl -X POST http://localhost:5100/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "find me a dairy-free pasta dinner", "sessionId": "test"}'

# Check performance metrics
curl http://localhost:5100/admin/metrics | python3 -m json.tool
```

---

## Project Structure

```
ChefAgent/
├── src/
│   ├── agents/
│   │   ├── RecipeAgent/
│   │   │   ├── RecipeSearchPlugin.cs         # Multi-stage retrieval, embedding cache
│   │   │   ├── RecipeReranker.cs             # LLM rerank via OutputGuard
│   │   │   └── QueryPreprocessor.cs          # Spell-check, negation, X-free expansion
│   │   ├── DietAgent/
│   │   │   └── DietValidationPlugin.cs       # Two-layer validation, CircuitBreaker
│   │   ├── PlannerAgent/
│   │   │   └── MealPlannerPlugin.cs          # Generate + Modify + variety enforcement
│   │   └── Orchestrator/
│   │       ├── IntentRouter.cs               # Rules classifier + LLM entity extraction
│   │       └── AgentOrchestrator.cs          # Dispatch + history + confidence
│   ├── api/
│   │   ├── Endpoints.cs                      # /chat, /profile, /admin/*
│   │   └── ServiceRegistration.cs
│   ├── frontend/
│   │   └── src/components/
│   └── shared/
│       ├── DietaryRules.cs                   # 420+ rules, 12 categories, GetCategoryIngredients()
│       ├── Models.cs                         # All domain records + enums
│       ├── SessionStore.cs                   # Redis — plan, profile, history, extraction cache
│       ├── Tracing.cs                        # Langfuse fire-and-forget client
│       ├── MetricsCollector.cs               # p50/p95/p99 sliding window
│       ├── TraceContext.cs                   # Lightweight trace context record
│       ├── InputGuard.cs                     # Two-signal injection detection
│       ├── OutputGuard.cs                    # JSON validation, sanity, retry/fallback
│       ├── CircuitBreaker.cs                 # 3-state breaker (ollama + redis instances)
│       ├── RateLimiter.cs                    # Per-session sliding window
│       └── GuardrailAuditLog.cs              # In-memory ring buffer, 9 event types
├── eval/
│   ├── README.md                             # How to run the full eval pipeline
│   ├── datasets/
│   │   ├── golden_dataset.json               # 100 labeled queries, 12 categories
│   │   ├── e2e_golden_dataset.json           # 60 e2e test cases, 10 intent categories
│   │   ├── e2e_results.json                  # Latest e2e harness run
│   │   ├── e2e_judge_results.json            # Latest LLM judge scores
│   │   └── month3-eval-report.md             # Consolidated Month 3 eval report
│   ├── experiments/
│   │   ├── 2026-06-01_baseline.json
│   │   ├── 2026-06-07_spell_check.json
│   │   └── 2026-06-03_semantic_negation.json
│   └── harnesses/
│       ├── retrieve.py                       # Local retrieval step
│       ├── score_simple.py                   # Colab scoring step
│       ├── compare_experiments.py            # Experiment diff tool
│       ├── eval_e2e.py                       # E2E harness
│       └── llm_judge.py                      # LLM judge scorer
├── docs/
│   └── adrs/
│       ├── 001-orchestration-framework.md
│       ├── 002-vector-database.md
│       ├── 003-llm-provider.md
│       ├── 004-diet-agent-architecture.md
│       ├── 005-orchestrator-design.md
│       ├── 006-planner-agent-architecture.md
│       ├── 007-session-memory-design.md
│       ├── 008-guardrails-architecture.md
│       ├── 009-evaluation-pipeline.md
│       ├── 010-observability-architecture.md
│       └── 011-evaluation-strategy.md
├── docker-compose.yml
└── chefagent_embeddings.ipynb
```

---

## Architecture Decision Records

- [ADR-001: Orchestration Framework](docs/adrs/001-orchestration-framework.md)
- [ADR-002: Vector Database](docs/adrs/002-vector-database.md)
- [ADR-003: LLM Provider](docs/adrs/003-llm-provider.md)
- [ADR-004: Diet Agent Architecture](docs/adrs/004-diet-agent-architecture.md)
- [ADR-005: Orchestrator Design](docs/adrs/005-orchestrator-design.md)
- [ADR-006: Planner Agent Architecture](docs/adrs/006-planner-agent-architecture.md)
- [ADR-007: Session Memory Design](docs/adrs/007-session-memory-design.md)
- [ADR-008: Guardrails Architecture](docs/adrs/008-guardrails-architecture.md)
- [ADR-009: Evaluation Pipeline](docs/adrs/009-evaluation-pipeline.md)
- [ADR-010: Observability Architecture](docs/adrs/010-observability-architecture.md)
- [ADR-011: Evaluation Strategy](docs/adrs/011-evaluation-strategy.md)

---

## Roadmap

| Month | Focus | Status |
|---|---|---|
| Month 1 (Weeks 1–4) | Recipe Agent, Diet Agent, Orchestrator, React UI | ✅ Complete |
| Month 2 (Weeks 5–8) | Planner Agent, Session Memory, Guardrails | ✅ Complete |
| Month 3 (Weeks 9–11) | Eval pipeline, Langfuse observability, semantic fixes | ✅ Complete |
| Month 3 (Week 12) | Cloud deploy | 🔄 Next |
| Month 4 | MCP server, LinkedIn posts | 🔜 Planned |
| Month 5 | Portfolio site, resume, outreach | 🔜 Planned |

---

## License

MIT