# ChefAgent 🍳

A multi-agent system where specialized AI agents collaborate to handle recipe search, dietary reasoning, and meal planning — built with production-grade orchestration, evaluation, and observability.

**100% open-source stack. No cloud subscriptions required.**

---

## Current Status

| Agent | Status | Notes |
|-------|--------|-------|
| **Recipe Agent** | ✅ Week 2 complete | Multi-stage retrieval pipeline — negation, filtering, reranking |
| **Diet Agent** | ✅ Week 3 complete | Two-layer validation + substitutions (94% rules coverage) |
| **Orchestrator** | ✅ Week 4 complete | Intent routing, /chat endpoint, React UI |
| **Planner Agent** | ✅ Week 5 complete | Stateful meal plans, Redis session memory, multi-slot, swap UI |

**Latest tag:** `v0.2.0` — Planner Agent complete

---

## Architecture

```
User /chat message
    │
    ├─ IntentRouter          rules <1ms · LLM fallback Month 2
    │
    ├─ AgentOrchestrator     routes by intent · builds response
    │
    ├──────────────────────────────────────────────┐
    │                    │                         │
    ▼                    ▼                         ▼
Recipe Agent         Diet Agent            Planner Agent
search · filter      rules 94%             generate · modify
rerank · expand      LLM fallback          variety enforcement
    │                    │                         │
    ├── Qdrant            ├── Rules engine          ├── calls Recipe ×7
    │   10K vectors       │   420+ phrases          ├── calls Diet ×7
    │                     │   12 categories         │
    └── Ollama            └── Ollama                └── Redis SessionStore
        nomic-embed-text      llama3.2 fallback         session:{id}:plan
                                                         TTL 7 days
    └─ OrchestratorResponse
           recipes · dietary validation · message · meal plan
```

---

## Agents

### Recipe Agent (Week 1–2)

Multi-stage semantic search pipeline over 10K recipes. Fast stages always run; LLM stages are opt-in due to hardware constraints.

```
Query
  ├─ [1] Negation parsing       regex, instant — "pasta without tomatoes" → query: "pasta", excluded: ["tomatoes"]
  ├─ [2] Query expansion        Ollama llama3.2, opt-in — "cozy" → "soup, stew, chili"
  ├─ [3] Embed cleaned query    Ollama nomic-embed-text, 768d
  ├─ [4] Qdrant vector search   cosine similarity + optional maxIngredients / maxSteps filter
  ├─ [5] Negation filter        post-retrieval, removes excluded ingredients
  └─ [6] LLM re-rank            Ollama llama3.2, opt-in — title + ingredients → relevance scores
```

| Flag | Default | Effect |
|------|---------|--------|
| `maxIngredients` | null | Filter results to ≤ N ingredients |
| `maxSteps` | null | Filter results to ≤ N steps |
| `rerank` | false | LLM re-ranking (slow on CPU) |
| `expand` | false | Query expansion for abstract queries (slow on CPU) |

**Retrieval baseline (Week 2):** Score gap (relevant vs irrelevant) = 0.1482. Strongest on ingredient queries (0.82), weakest on abstract/mood queries (0.64).

---

### Diet Agent (Week 3)

Two-layer validation: fast rules engine for common cases, LLM fallback for edge cases only.

```
Recipe + DietaryProfile
  ├─ [1] Rules Engine      420+ phrase-level rules, 12 categories, <5ms, zero LLM
  │       Violations found → return with substitutions
  ├─ [2] Ambiguous tier
  │       Allergy + ambiguous ingredient  → LLM (safety-critical)
  │       Restriction + ambiguous         → skip recipe (conservative)
  └─ [3] LLM fallback      unknown restrictions or allergy + ambiguity only
```

| Layer | Cases | Cost |
|-------|-------|------|
| Rules engine | 94% | Zero LLM, <5ms |
| Skip tier | ~5% | No cost |
| LLM | ~1% | Ollama llama3.2 |

**12 restriction categories:** dairy, gluten, nuts, eggs, soy, sesame, seafood, meat, jain, sattvic, paleo, halal/kosher

**`DairyFalsePositives` safe-list:** `"peanut butter"` excluded from dairy check before rule scan. Same pattern reused in `CuisineFalsePositives` in Week 5.

---

### Orchestrator (Week 4)

Rules-based intent classifier routes natural language to the right agent(s).

| Intent | Signal | Agents called |
|--------|--------|--------------|
| `SearchRecipe` | default (most common) | Recipe Agent + Diet Agent (if profile) |
| `ValidateDiet` | "can I eat", "safe for", "allergic to" | Recipe Agent + Diet Agent |
| `CreateMealPlan` | "plan my week", "meal plan", "plan my dinners" | Planner Agent |
| `ModifyMealPlan` | "swap", "replace Tuesday" | Planner Agent |
| `GeneralQuestion` | "what is", "how do I", "explain" | Ollama direct |

**94% intent accuracy** (rules-only, zero LLM calls). `rules-default` logs collected for future LLM classifier training.

---

### Planner Agent (Week 5)

First stateful agent. Generates 7-day meal plans, persists to Redis, supports iterative slot-level modification.

```
CreateMealPlan → GeneratePlanAsync
    ├─ foreach day × foreach slot
    │       SearchRecipesAsync (rotated queries per day)
    │       SelectWithVariety (no consecutive protein/cuisine repeats)
    │       ValidateRecipeAsync (if profile present)
    └─ SavePlanAsync → Redis

ModifyMealPlan → ModifyPlanAsync
    ├─ GetPlanAsync ← Redis (instant)
    ├─ SearchRecipesAsync (offset query, excludeTitle)
    ├─ SelectWithVarietyForModify (checks both neighbors + excludes current)
    └─ SavePlanAsync → Redis
```

**Multi-slot support:** "plan my meals" → breakfast + lunch + dinner. "plan my dinners" → dinner only. Extracted from natural language, no code change required for new combinations.

**Variety enforcement:** Keyword-based protein/cuisine inference — no LLM. `CuisineFalsePositives` safe-list prevents false tags (e.g., `"soy sauce"` in an American recipe triggering the asian tag).

**Performance (8GB RAM, CPU-only):**

| Operation | Latency |
|-----------|---------|
| Plan generation (7 dinners) | ~14–20s |
| Plan generation (7 × 3 meals) | ~45s |
| Plan read from Redis | ~1ms |
| Single slot modify | ~2s |

---

## Endpoints

| Endpoint | Description |
|----------|-------------|
| `GET /health` | Health check |
| `POST /recipes/search` | Recipe Agent only — vector search + optional filters |
| `POST /recipes/search-validated` | Recipe Agent + Diet Agent — search with dietary validation |
| `POST /chat` | Orchestrator — natural language → right agent(s) |

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Orchestration** | Semantic Kernel (C#) |
| **LLM** | Ollama — llama3.2 (local, CPU) |
| **Embeddings** | Ollama — nomic-embed-text (768d) |
| **Vector DB** | Qdrant (self-hosted, Docker) |
| **Session state** | Redis (Docker, TTL 7 days) |
| **Backend** | ASP.NET Core Minimal API |
| **Frontend** | React + Tailwind (Vite) |
| **Observability** | Langfuse — coming Month 3 |
| **Deployment** | Docker Compose (local), Azure — coming Month 3 |

---

## Quick Start

> **Note:** Ollama runs natively (not in Docker) for direct RAM access. On 8GB machines, running Ollama inside Docker caps available memory and stalls inference.

```bash
# 1. Clone and start infrastructure
git clone https://github.com/aayushmdesai/ChefAgent.git
cd ChefAgent
docker compose up -d        # starts Qdrant + Redis

# 2. Start Ollama natively and pull models (~2.3GB total)
brew install ollama
ollama pull nomic-embed-text
ollama pull llama3.2

# 3. Set up recipe data pipeline
python3 -m venv .venv && source .venv/bin/activate
pip install -r scripts/requirements.txt
python3 scripts/prepare_recipes.py       # downloads + parses 10K recipes
python3 scripts/load_qdrant.py           # uploads vectors to Qdrant
# Note: generate_embeddings.py requires GPU — use chefagent_embeddings.ipynb on Colab

# 4. Run the API
cd src/api && dotnet run     # http://localhost:5000

# 5. Run the frontend
cd src/frontend && npm install && npm run dev   # http://localhost:5173

# 6. Test
curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "find me a quick chicken dinner", "sessionId": "my-session"}'

curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "plan my dinners for the week", "sessionId": "my-session"}'

curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "swap Tuesday to something with pasta", "sessionId": "my-session"}'
```

---

## Project Structure

```
ChefAgent/
├── src/
│   ├── agents/
│   │   ├── RecipeAgent/              # Multi-stage RAG search pipeline
│   │   │   ├── RecipeSearchPlugin.cs
│   │   │   ├── RecipeReranker.cs
│   │   │   └── QueryPreprocessor.cs
│   │   ├── DietAgent/                # Two-layer dietary validation
│   │   │   ├── DietaryRules.cs       # 420+ phrase rules, 12 categories
│   │   │   └── DietValidationPlugin.cs
│   │   ├── PlannerAgent/             # Stateful meal plan generation
│   │   │   └── MealPlannerPlugin.cs  # Generate + Modify + variety enforcement
│   │   └── Orchestrator/             # Intent routing + agent coordination
│   │       ├── IntentRouter.cs       # Rules classifier + entity extraction
│   │       └── AgentOrchestrator.cs  # Agent dispatch + response building
│   ├── api/
│   │   ├── Program.cs
│   │   ├── Endpoints.cs
│   │   └── ServiceRegistration.cs
│   ├── frontend/                     # React + Tailwind chat UI
│   │   └── src/components/
│   │       ├── ChatInput.jsx
│   │       ├── ChatMessages.jsx
│   │       ├── RecipeCard.jsx
│   │       ├── ProfileSidebar.jsx
│   │       └── MealPlanView.jsx      # 7-day grid, per-slot swap buttons
│   └── shared/
│       ├── Models.cs                 # RecipeDocument, DietaryProfile, MealPlan, etc.
│       └── SessionStore.cs           # Redis-backed plan + profile persistence
├── eval/
│   └── datasets/
│       ├── retrieval_baseline.md
│       ├── week2_retrieval_results.md
│       ├── diet_agent_test_cases.md
│       ├── diet_agent_test_results.md
│       └── planner_test_results.md   # 14/15 passed, 0 system bugs
├── scripts/
│   ├── prepare_recipes.py
│   ├── generate_embeddings.py
│   ├── load_qdrant.py
│   ├── test_search_quality.py
│   ├── test_diet_agent.py
│   └── eval/
│       └── test_planner.py           # 15-case planner test matrix
├── docs/
│   └── adrs/
│       ├── 001-orchestration-framework.md
│       ├── 002-vector-database.md
│       ├── 003-llm-provider.md
│       ├── 004-diet-agent-architecture.md
│       ├── 005-orchestrator-design.md
│       └── 006-planner-agent-architecture.md
├── docker-compose.yml                # Qdrant + Redis
└── chefagent_embeddings.ipynb        # Colab notebook for GPU embedding
```

---

## Architecture Decision Records

- [ADR-001: Orchestration Framework](docs/adrs/001-orchestration-framework.md)
- [ADR-002: Vector Database](docs/adrs/002-vector-database.md)
- [ADR-003: LLM Provider](docs/adrs/003-llm-provider.md)
- [ADR-004: Diet Agent Architecture](docs/adrs/004-diet-agent-architecture.md)
- [ADR-005: Orchestrator Design](docs/adrs/005-orchestrator-design.md)
- [ADR-006: Planner Agent Architecture](docs/adrs/006-planner-agent-architecture.md)

---

## Roadmap

| Month | Focus | Status |
|-------|-------|--------|
| Month 1 (Weeks 1–4) | Recipe Agent, Diet Agent, Orchestrator, React UI | ✅ Complete |
| Month 2 (Week 5) | Planner Agent, Redis session memory, multi-slot planning | ✅ Complete |
| Month 2 (Weeks 6–8) | Guardrails, LLM intent classifier, conversation history | 🔜 Next |
| Month 3 | Eval (RAGAS), Langfuse observability, cloud deploy | 🔜 Planned |
| Month 4 | MCP server, LinkedIn posts | 🔜 Planned |
| Month 5 | Portfolio site, resume, outreach | 🔜 Planned |

---

## License

MIT