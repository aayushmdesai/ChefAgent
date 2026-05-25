# ChefAgent 🍳

A multi-agent system where specialized AI agents collaborate to handle recipe search, dietary reasoning, and meal planning — built with production-grade orchestration, evaluation, and observability.

**100% open-source stack. No cloud subscriptions required.**

---

## Current Status

| Agent | Status | Notes |
|-------|--------|-------|
| **Recipe Agent** | ✅ Week 2 complete | Multi-stage retrieval pipeline |
| **Diet Agent** | 🔜 Month 1 Week 3 | Dietary validation + substitutions |
| **Planner Agent** | 🔜 Month 2 | Weekly meal plans with Redis memory |
| **Orchestrator** | 🔜 Month 2 | Intent routing + agent coordination |

---

## Architecture

```
User → Orchestrator → ┬─ Recipe Agent  (RAG over 10K+ recipes)
                       ├─ Diet Agent    (dietary validation & substitutions)
                       └─ Planner Agent (weekly meal plans with memory)
```

| Agent | Responsibility | Tech |
|-------|---------------|------|
| **Recipe Agent** | Multi-stage semantic search over recipe corpus | Semantic Kernel + Qdrant + Ollama |
| **Diet Agent** | Validates recipes against user constraints (allergies, macros, preferences) | Tool-calling agent with structured output |
| **Planner Agent** | Builds weekly meal plans with memory of past selections | Stateful agent with Redis session persistence |
| **Orchestrator** | Routes intent, merges responses, handles fallbacks | Semantic Kernel Planner / LangGraph StateGraph |

---

## Recipe Agent — Search Pipeline (Week 2)

The core retrieval pipeline has six stages. Fast stages always run; LLM stages are opt-in due to hardware constraints.

```
Query
  │
  ├─ [1] Negation parsing (regex, instant)
  │       "pasta without tomatoes" → query: "pasta", excluded: ["tomatoes"]
  │
  ├─ [2] Query expansion (Ollama llama3.2, opt-in)
  │       "something warm and comforting" → "soup, stew, chili, casserole"
  │
  ├─ [3] Embed cleaned query (Ollama nomic-embed-text, 768d)
  │
  ├─ [4] Qdrant vector search + payload filter
  │       cosine similarity over 10K recipes
  │       optional: maxIngredients, maxSteps
  │
  ├─ [5] Negation filter (post-retrieval, instant)
  │       removes results containing excluded ingredients
  │
  └─ [6] LLM re-rank (Ollama llama3.2, opt-in)
          title + ingredients → relevance scores → reordered results
```

### API request flags

| Flag | Default | Effect |
|------|---------|--------|
| `maxIngredients` | null | Filter results to ≤ N ingredients |
| `maxSteps` | null | Filter results to ≤ N steps |
| `rerank` | false | Enable LLM re-ranking (slow on CPU) |
| `expand` | false | Enable query expansion for abstract queries (slow on CPU) |

### Retrieval quality baseline (Week 2)

| Category | Score | Notes |
|----------|-------|-------|
| By Ingredients | 0.8282 | 🟢 Strongest — concrete nouns match well |
| Exact Match | 0.8053 | 🟢 Titles are concrete |
| Technique | 0.7552 | 🟢 "slow cooker", "grilled" embed well |
| Negation | 0.7314 ✅ | 🟢 4/4 clean — fixed in Week 2 |
| Filtering | 0.7360 ✅ | 🟢 All results within constraints |
| Dietary | 0.7172 | 🟡 Matches food type, misses constraints |
| Multi-Intent | 0.7079 | 🟡 Partial — complex queries need LLM |
| Negation (X-free) | 0.7022 | 🟡 Works for concrete terms, not categories |
| Cuisine | 0.6815 | 🟡 Matches mood ("spicy"), misses region |
| Misspelling | 0.6769 | 🟡 Degrades but recovers partially |
| Situation | 0.6373 | 🔴 Abstract queries need LLM expansion |
| Irrelevant | 0.5822 | ✅ Correctly low |

**Known limitation:** X-free filtering ("dairy-free") matches the literal word in ingredients, not the semantic category. "dairy-free soup" can still return recipes with milk or cheese. Fix: LLM-based semantic filtering — Month 3.

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| **Orchestration** | Semantic Kernel (C#), LangGraph (Python) |
| **LLM** | Ollama — Llama 3.2 (local, CPU) — provider-agnostic |
| **Embeddings** | Ollama — nomic-embed-text (768d) |
| **Vector DB** | Qdrant (self-hosted, Docker) |
| **Memory** | Redis (session state, conversation history) |
| **Backend** | ASP.NET Core Minimal API |
| **Eval** | RAGAS retrieval quality, custom test harness |
| **Observability** | Langfuse (self-hosted tracing) — coming Month 3 |
| **Deployment** | Docker Compose (local), Azure Container Apps — coming Month 3 |

---

## Quick Start

> **Note:** Ollama runs natively (not in Docker) for direct RAM access. On 8GB machines, running Ollama inside Docker caps available memory and stalls inference.

```bash
# 1. Clone and start infrastructure
git clone https://github.com/aayushmdesai/ChefAgent.git
cd ChefAgent
docker compose up -d        # starts Qdrant + Redis

# 2. Start Ollama natively and pull models (~2.3GB total)
brew install ollama          # macOS
ollama pull nomic-embed-text # embeddings (274MB, 768d)
ollama pull llama3.2         # chat + re-ranking (2GB)

# 3. Set up recipe data pipeline
python3 -m venv .venv && source .venv/bin/activate
pip install -r scripts/requirements.txt
python3 scripts/prepare_recipes.py       # downloads + parses 10K recipes
python3 scripts/load_qdrant.py           # uploads vectors to Qdrant

# Note: generate_embeddings.py requires GPU for reasonable speed.
# Use Google Colab with the included chefagent_embeddings.ipynb notebook.

# 4. Run the API
cd src/api && dotnet run     # starts on http://localhost:5000

# 5. Test it
curl -X POST http://localhost:5000/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query": "quick chicken dinner", "maxResults": 5}'

# With filtering
curl -X POST http://localhost:5000/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query": "simple chicken dinner", "maxResults": 5, "maxIngredients": 6}'

# Negation (always-on, no flag needed)
curl -X POST http://localhost:5000/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query": "pasta without tomatoes", "maxResults": 5}'
```

---

## Project Structure

```
ChefAgent/
├── src/
│   ├── agents/
│   │   ├── RecipeAgent/        # Multi-stage RAG search pipeline
│   │   │   ├── RecipeSearchPlugin.cs   # Semantic Kernel plugin
│   │   │   ├── RecipeReranker.cs       # LLM re-ranking via Ollama
│   │   │   └── QueryPreprocessor.cs    # Negation + query expansion
│   │   ├── DietAgent/          # Coming Month 1 Week 3
│   │   ├── PlannerAgent/       # Coming Month 2
│   │   └── Orchestrator/       # Coming Month 2
│   ├── api/                    # ASP.NET Core Minimal API
│   └── shared/                 # Common models (RecipeDocument, etc.)
├── eval/
│   ├── datasets/
│   │   ├── retrieval_baseline.md       # 24-query evaluation set
│   │   └── week2_retrieval_results.md  # Week 2 quality results
│   └── harnesses/              # RAGAS eval — coming Month 3
├── scripts/
│   ├── prepare_recipes.py      # Download + parse corbt/all-recipes
│   ├── generate_embeddings.py  # Embed via Ollama (use Colab for speed)
│   ├── load_qdrant.py          # Upload vectors to Qdrant
│   └── test_search_quality.py  # Search quality test harness
├── docker-compose.yml          # Qdrant + Redis
└── chefagent_embeddings.ipynb  # Colab notebook for GPU embedding
```

---

## Architecture Decision Records

- [ADR-001: Orchestration Framework](docs/adrs/001-orchestration-framework.md)
- [ADR-002: Vector Database](docs/adrs/002-vector-database.md)
- [ADR-003: LLM Provider](docs/adrs/003-llm-provider.md)

---

## License

MIT