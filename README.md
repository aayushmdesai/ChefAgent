![CI](https://github.com/aayushmdesai/ChefAgent/actions/workflows/ci.yml/badge.svg)

# ChefAgent 🍳

A multi-agent system where specialized AI agents collaborate to handle recipe search, dietary reasoning, and meal planning — built with production-grade orchestration, guardrails, and observability.

**100% open-source stack. No cloud subscriptions required.**

---

## Current Status

| Component            | Status             | Notes                                                                     |
| -------------------- | ------------------ | ------------------------------------------------------------------------- |
| **Recipe Agent**     | ✅ Week 2 complete | Multi-stage retrieval — negation, filtering, reranking, spell-check       |
| **Diet Agent**       | ✅ Week 3 complete | Two-layer validation + substitutions (94% rules coverage)                 |
| **Orchestrator**     | ✅ Week 4 complete | Intent routing, /chat endpoint, React UI                                  |
| **Planner Agent**    | ✅ Week 5 complete | Stateful meal plans, Redis session memory, multi-slot, swap UI            |
| **Memory & State**   | ✅ Week 6 complete | Conversation history, profile persistence, reference resolution           |
| **Guardrails**       | ✅ Week 7 complete | Input validation, output guard, circuit breaker, rate limiting, audit log |
| **Failure Matrix**   | ✅ Week 8 complete | 36/36 failure modes tested, e2e sweep                                     |
| **Eval Pipeline**    | ✅ Week 9 complete | 100-query golden dataset, RAGAS-style eval, SymSpell +0.175 improvement   |

**Latest tag:** `v0.6.0`

---

## Evaluation

Retrieval quality measured with a custom sequential scorer (Ollama as judge) across 100 golden queries in 12 categories.

| Metric | Baseline | Post Spell-Check | Delta |
|--------|----------|-----------------|-------|
| Context Relevance | 0.470 | 0.524 | +0.054 |
| Faithfulness | 0.489 | 0.444 | -0.045 |
| Answer Relevancy | 0.267 | 0.234 | -0.033 |

**Faithfulness and answer relevancy are suppressed by template-based generation** — returning a recipe title confuses the LLM judge. These metrics become meaningful when LLM-generated responses are added.

| Category | Baseline | Post Spell-Check | Delta |
|----------|----------|-----------------|-------|
| misspelling | 0.442 | 0.617 | **+0.175** |
| x_free | 0.438 | 0.588 | **+0.150** |
| exact_match | 0.525 | 0.613 | **+0.088** |
| by_ingredients | 0.630 | 0.610 | -0.020 |
| cuisine | 0.625 | 0.613 | -0.012 |

**Strongest categories:** by_ingredients, cuisine, exact_match
**Weakest categories:** multi_intent (0.46), dietary (0.46), edge_case (0.43)

```bash
# Run evaluation
python eval/harnesses/retrieve.py                    # fetch contexts locally
# upload retrieved_contexts.json to Colab
python eval/harnesses/score_simple.py --limit 100   # score with Ollama judge

# Compare experiments
python eval/harnesses/compare_experiments.py \
    eval/experiments/2026-06-01_baseline.json \
    eval/experiments/2026-06-07_spell_check.json
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
    │                    │                             │
    ├── Qdrant            ├── Rules engine              ├── calls Recipe ×7
    │   10K vectors       │   420+ phrases              ├── calls Diet ×7
    │                     │   12 categories             │
    └── Ollama            └── Ollama                    └── Redis SessionStore
        nomic-embed-text      llama3.2 fallback             plan · profile · history
        CircuitBreaker        CircuitBreaker                TTL 7 days
        OutputGuard           OutputGuard
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

| Layer      | Component                        | What it does                                                |
| ---------- | -------------------------------- | ----------------------------------------------------------- |
| Input      | `InputGuard`                     | Two-signal injection detection, sanitization, length limits |
| Output     | `OutputGuard`                    | JSON schema validation, recipe sanity, retry/fallback       |
| Resilience | `CircuitBreaker`                 | 3-state breaker — skips LLM after 3 failures, auto-recovers |
| Abuse      | `RateLimiter`                    | 30 req/min per session, repeated query detection            |
| Trust      | Confidence + `GuardrailAuditLog` | Disclaimers, allergy warnings, observable audit trail       |

Two circuit breakers: `ollama` (60s cooldown) and `redis` (30s cooldown) — keyed singletons, independent failure domains.

**Integration test:** 18/18 passing across all 5 layers. `GET /admin/guardrails` for live audit log.

### Confidence signaling

Every `/chat` response includes a `confidence` field:

| Value    | Meaning                        | When                                       |
| -------- | ------------------------------ | ------------------------------------------ |
| `High`   | Rules-only path                | Recipe search without profile, GetMealPlan |
| `Medium` | LLM involved, output validated | Diet validation, GeneralQuestion           |
| `Low`    | Fallback triggered             | LLM timeout, dietary check unavailable     |

---

## Memory & State (Week 6)

```
Session memory (Redis, TTL 7 days):
    session:{id}:plan       → MealPlan (JSON)
    session:{id}:profile    → DietaryProfile (JSON)
    session:{id}:history    → ConversationEntry[] (Redis list, 20-entry window)
    session:{id}:extraction → Cached LLM entity extraction result (JSON)
```

**Reference resolution:** "is the first one vegan?" → resolves to top recipe from prior turn. Zero LLM calls, ~1ms.

**Profile persistence:** Union merge on every request — stored + extracted, saved back to Redis. User never needs to re-send dietary constraints.

**LLM entity extraction:** "I can't have dairy, find me dinner" → `restrictions: [dairy]` extracted via Ollama fallback, merged into stored profile. Result cached in Redis — subsequent messages skip the LLM entirely (90s → 13ms).

---

## Agents

### Recipe Agent (Weeks 1–2 + Week 9)

Multi-stage semantic search pipeline over 10K recipes.

```
Query
  ├─ [0] Spell correction       SymSpell + food domain dict — "chiken" → "chicken"
  ├─ [1] Negation parsing       regex, instant — "pasta without tomatoes" → excluded: ["tomatoes"]
  ├─ [2] Query expansion        Ollama opt-in — "cozy" → "soup, stew, chili"
  ├─ [3] Embed cleaned query    Ollama nomic-embed-text (768d)
  ├─ [4] Qdrant vector search   cosine similarity + maxIngredients/maxSteps filter
  ├─ [5] OutputGuard sanity     drops empty titles, missing ingredients, score < 0.3
  ├─ [6] Negation filter        removes excluded ingredients post-retrieval
  └─ [7] LLM re-rank            Ollama opt-in — CallWithRetryAsync via OutputGuard
```

**Spell correction — two-layer architecture:**
- Food domain dictionary (`food_corrections.json`) — high-confidence culinary corrections
- SymSpell — frequency-weighted general spell correction, handles unknown misspellings

---

### Diet Agent (Week 3)

Two-layer validation: fast rules engine for common cases, LLM fallback for edge cases.

```
Recipe + DietaryProfile
  ├─ [1] Rules Engine      420+ phrase-level rules, 12 categories, <5ms, zero LLM
  │       Violations found → substitutions from SubstitutionMap
  ├─ [2] Ambiguous tier
  │       Allergy + ambiguous  → LLM (safety-critical)
  │       Restriction + ambiguous → skip (conservative)
  └─ [3] LLM fallback      unknown restrictions · CircuitBreaker protected
```

**12 restriction categories:** dairy, gluten, nuts, eggs, soy, sesame, seafood, meat, jain, sattvic, paleo, halal/kosher

---

### Orchestrator (Week 4)

Rules-based intent classifier with conversation memory.

| Intent                        | Agents          | Confidence |
| ----------------------------- | --------------- | ---------- |
| `SearchRecipe` (no profile)   | Recipe Agent    | High       |
| `SearchRecipe` (with profile) | Recipe + Diet   | Medium     |
| `ValidateDiet`                | Recipe + Diet   | Medium     |
| `GetMealPlan`                 | Redis read only | High       |
| `CreateMealPlan`              | Planner Agent   | Medium     |
| `ModifyMealPlan`              | Planner Agent   | Medium     |
| `GeneralQuestion`             | Ollama direct   | Medium     |

---

### Planner Agent (Week 5)

First stateful agent. Generates 7-day meal plans with slot-level modification.

**Multi-slot support:** "plan my meals" → breakfast + lunch + dinner. "plan my dinners" → dinner only.

**Performance (8GB RAM, CPU-only):**

| Operation                   | Latency              |
| --------------------------- | -------------------- |
| Plan generation (7 dinners) | ~5–14s (warm Ollama) |
| Plan read from Redis        | ~1ms                 |
| Single slot modify          | ~2s                  |

---

## Endpoints

| Endpoint                         | Description                               |
| -------------------------------- | ----------------------------------------- |
| `GET /health`                    | Health check                              |
| `POST /chat`                     | Orchestrator — natural language → agents  |
| `POST /recipes/search`           | Recipe Agent only                         |
| `POST /recipes/search-validated` | Recipe Agent + Diet Agent                 |
| `GET /profile/{sessionId}`       | Load stored dietary profile               |
| `POST /profile/{sessionId}`      | Save dietary profile                      |
| `GET /admin/guardrails`          | Live guardrail audit log (last 50 events) |

---

## Tech Stack

| Layer                | Technology                                      |
| -------------------- | ----------------------------------------------- |
| **Orchestration**    | Semantic Kernel (C#)                            |
| **LLM**              | Ollama — llama3.2 (local, CPU)                  |
| **Embeddings**       | Ollama — nomic-embed-text (768d)                |
| **Vector DB**        | Qdrant (self-hosted, Docker)                    |
| **Session state**    | Redis (Docker, TTL 7 days)                      |
| **Spell correction** | SymSpell + food domain dictionary               |
| **Backend**          | ASP.NET Core Minimal API                        |
| **Frontend**         | React + Tailwind (Vite)                         |
| **Eval**             | Custom scorer (Ollama judge) + Colab GPU runner |
| **Observability**    | Langfuse — Week 10                              |
| **Deployment**       | Docker Compose (local), cloud — Week 11–12      |

---

## Quick Start

> Ollama runs natively (not in Docker) for direct RAM access on 8GB machines.

```bash
# 1. Clone and start infrastructure
git clone https://github.com/aayushmdesai/ChefAgent.git
cd ChefAgent
docker compose up -d        # Qdrant + Redis

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
  -d '{"message": "find me a quick chicken dinner", "sessionId": "test"}'

curl -X POST http://localhost:5100/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "plan my dinners for the week", "sessionId": "test"}'

# Check guardrails audit log
curl http://localhost:5100/admin/guardrails
```

---

## Project Structure

```
ChefAgent/
├── src/
│   ├── agents/
│   │   ├── RecipeAgent/
│   │   │   ├── RecipeSearchPlugin.cs         # Multi-stage retrieval, OutputGuard sanity
│   │   │   ├── RecipeReranker.cs             # LLM rerank via OutputGuard.CallWithRetryAsync
│   │   │   ├── QueryPreprocessor.cs          # Spell-check, negation, query expansion
│   │   │   └── Dictionaries/
│   │   │       ├── food_corrections.json     # Domain-specific spell corrections
│   │   │       └── frequency_dictionary_en_80k.txt  # SymSpell frequency list
│   │   ├── DietAgent/
│   │   │   ├── DietaryRules.cs               # 420+ phrase rules, 12 categories
│   │   │   └── DietValidationPlugin.cs       # Two-layer validation, CircuitBreaker
│   │   ├── PlannerAgent/
│   │   │   └── MealPlannerPlugin.cs          # Generate + Modify + variety enforcement
│   │   └── Orchestrator/
│   │       ├── IntentRouter.cs               # Rules classifier + LLM entity extraction
│   │       └── AgentOrchestrator.cs          # Dispatch + history + confidence + disclaimers
│   ├── api/
│   │   ├── Program.cs
│   │   ├── Endpoints.cs                      # /chat, /profile, /admin/guardrails
│   │   └── ServiceRegistration.cs
│   ├── frontend/
│   │   └── src/components/
│   │       ├── ChatInput.jsx
│   │       ├── ChatMessages.jsx
│   │       ├── RecipeCard.jsx
│   │       ├── ProfileSidebar.jsx
│   │       └── MealPlanView.jsx              # 7-day grid, per-slot swap, dietary badges
│   └── shared/
│       ├── Models.cs                         # All domain records + enums
│       ├── SessionStore.cs                   # Redis — plan, profile, history, extraction cache
│       ├── InputGuard.cs                     # Two-signal injection detection
│       ├── OutputGuard.cs                    # JSON validation, sanity, retry/fallback
│       ├── CircuitBreaker.cs                 # 3-state breaker (ollama + redis instances)
│       ├── RateLimiter.cs                    # Per-session sliding window
│       └── GuardrailAuditLog.cs              # In-memory ring buffer, 9 event types
├── eval/
│   ├── datasets/
│   │   ├── golden_dataset.json               # 100 labeled Q&A pairs, 12 categories
│   │   ├── retrieved_contexts.json           # Last retrieval run output
│   │   └── ragas_results.json                # Latest eval scores
│   ├── experiments/
│   │   ├── 2026-06-01_baseline.json          # Baseline scores
│   │   └── 2026-06-07_spell_check.json       # Post spell-check scores
│   └── harnesses/
│       ├── retrieve.py                       # Step 1: fetch contexts from API
│       ├── score_simple.py                   # Step 2: score with Ollama judge
│       ├── run_ragas.py                      # Full RAGAS pipeline (requires OpenAI)
│       └── compare_experiments.py            # Diff two experiment result files
├── scripts/
│   ├── prepare_recipes.py
│   ├── generate_embeddings.py
│   ├── load_qdrant.py
│   └── eval/
│       ├── test_planner.py
│       ├── test_memory.py
│       ├── test_input_guard.py               # 15/15
│       ├── test_output_guard.py              # 10/10
│       ├── test_circuit_breaker.py           # 8/10 (2 expected — embedding needs Ollama)
│       └── test_guardrails.py                # 18/18 integration
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
│       └── 009-evaluation-pipeline.md
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

---

## Roadmap

| Month                 | Focus                                              | Status         |
| --------------------- | -------------------------------------------------- | -------------- |
| Month 1 (Weeks 1–4)   | Recipe Agent, Diet Agent, Orchestrator, React UI   | ✅ Complete    |
| Month 2 (Weeks 5–8)   | Planner Agent, Session Memory, Guardrails          | ✅ Complete    |
| Month 3 (Weeks 9–12)  | Eval pipeline, Langfuse observability, cloud deploy| 🔄 In progress |
| Month 4               | MCP server, LinkedIn posts                         | 🔜 Planned     |
| Month 5               | Portfolio site, resume, outreach                   | 🔜 Planned     |

---

## License

MIT