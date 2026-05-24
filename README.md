# ChefAgent 🍳

A multi-agent system where specialized AI agents collaborate to handle recipe search, dietary reasoning, and meal planning — built with production-grade orchestration, evaluation, and observability.

**100% open-source stack. No cloud subscriptions required.**

## Architecture

```
User → Orchestrator → ┬─ Recipe Agent  (RAG over 2M+ recipes)
                       ├─ Diet Agent    (dietary validation & substitutions)
                       └─ Planner Agent (weekly meal plans with memory)
```

| Agent | Responsibility | Tech |
|-------|---------------|------|
| **Recipe Agent** | Semantic search over a recipe corpus, returns ranked results | Semantic Kernel + Qdrant |
| **Diet Agent** | Validates recipes against user constraints (allergies, macros, preferences) | Tool-calling agent with structured output |
| **Planner Agent** | Builds weekly meal plans with memory of past selections | Stateful agent with Redis session persistence |
| **Orchestrator** | Routes intent, merges responses, handles fallbacks | Semantic Kernel Planner / LangGraph StateGraph |

## Tech Stack

- **Orchestration:** Semantic Kernel (C#), LangGraph (Python)
- **LLM:** Ollama (Llama 3.2, local) — provider-agnostic, swappable to Claude/GPT
- **Embeddings:** Ollama (nomic-embed-text, 768d)
- **Vector DB:** Qdrant (self-hosted, Docker)
- **Memory:** Redis (session state, conversation history)
- **Backend:** ASP.NET Core Minimal API
- **Eval:** RAGAS retrieval quality, LLM-as-judge end-to-end accuracy
- **Observability:** Langfuse (self-hosted tracing + dashboards)
- **Deployment:** Docker Compose (local), Azure Container Apps (production)

## Quick Start

```bash
# 1. Clone and start infrastructure
git clone https://github.com/YOUR_USERNAME/ChefAgent.git
cd ChefAgent
docker compose up -d

# 2. Pull LLM models (first time only — ~5GB total)
docker compose exec ollama ollama pull llama3.2
docker compose exec ollama ollama pull nomic-embed-text

# 3. Set up recipe data pipeline
pip install -r scripts/requirements.txt
python scripts/prepare_recipes.py --limit 10000    # Start with 10K recipes
python scripts/generate_embeddings.py               # ~20 min for 10K
python scripts/load_qdrant.py                        # Upload to Qdrant

# 4. Run the API
cd src/api
dotnet run

# 5. Test it
curl -X POST http://localhost:5100/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query": "quick chicken dinner", "maxResults": 3}'
```

## Project Structure

```
ChefAgent/
├── src/
│   ├── agents/
│   │   ├── RecipeAgent/        # RAG search over recipe corpus
│   │   ├── DietAgent/          # Dietary constraint validation
│   │   ├── PlannerAgent/       # Meal plan generation + memory
│   │   └── Orchestrator/       # Intent routing + agent coordination
│   ├── api/                    # ASP.NET Core Minimal API
│   ├── frontend/               # React chat UI
│   └── shared/                 # Common models, config, utilities
├── eval/
│   ├── datasets/               # Golden datasets for eval
│   └── harnesses/              # Eval runners (RAGAS, LLM-as-judge)
├── infra/
│   ├── docker/                 # Dockerfiles
│   └── ci/                     # CI/CD scripts
├── docs/
│   ├── adrs/                   # Architecture Decision Records
│   └── architecture/           # Diagrams, design docs
├── scripts/                    # Data pipeline (download, embed, index)
└── .github/workflows/          # GitHub Actions CI
```

## Architecture Decision Records

- [ADR-001: Orchestration Framework](docs/adrs/001-orchestration-framework.md)
- [ADR-002: Vector Database](docs/adrs/002-vector-database.md)
- [ADR-003: LLM Provider](docs/adrs/003-llm-provider.md)

## License

MIT
