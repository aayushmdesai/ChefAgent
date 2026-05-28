# Running ChefAgent in GitHub Codespaces

## First time setup

When you open this repo in Codespaces, the devcontainer runs `.devcontainer/setup.sh` automatically. This takes 5–10 minutes (model downloads).

If setup failed or you need to run it manually:

```bash
bash .devcontainer/setup.sh
```

---

## Upload recipe vectors (required once per Codespace)

The vector file is gitignored. Upload it manually:

1. In VS Code file explorer → right-click `data/embeddings/` → **Upload**
2. Select `recipe_vectors.jsonl` from your local machine
3. Load into Qdrant:

```bash
make reload-vectors
make check-vectors
# → { "status": "green", "points": 10000 }
```

---

## Daily workflow

```bash
make up              # start everything
make health          # verify API is up
make check-vectors   # verify Qdrant has 10K vectors
make build           # rebuild API after code changes
make logs            # tail API logs
make frontend        # start React dev server → http://localhost:5173
```

---

## Pull Ollama models (once per Codespace)

Models live in the `ollama-data` Docker volume. If the volume was wiped:

```bash
make pull-models
# pulls nomic-embed-text (274MB) + llama3.2 (2GB) — takes 3–5 min
```

---

## Full fresh rebuild

Wipes all volumes — re-pull models and reload vectors after:

```bash
make fresh
# then re-upload recipe_vectors.jsonl
make reload-vectors
```

---

## Test the API

```bash
# Health
make health

# Recipe search
curl -X POST http://localhost:5100/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "find me a quick chicken dinner", "sessionId": "test"}'

# General question — invokes LLM
curl -X POST http://localhost:5100/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "what is a roux?", "sessionId": "test"}'

# Meal plan
curl -X POST http://localhost:5100/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "plan my dinners for the week", "sessionId": "test"}'
```

---

## Port reference

| Port | Service |
|------|---------|
| 5100 | ChefAgent API |
| 5173 | React UI |
| 6333 | Qdrant REST / dashboard |
| 6379 | Redis |
| 11434 | Ollama |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| API can't reach Qdrant | `docker compose down && docker compose up -d` |
| Vectors missing after restart | `make reload-vectors` |
| Models missing after volume wipe | `make pull-models` |
| API running old code | `make build` |
| Nuclear reset | `docker compose down -v && make fresh` then re-upload vectors |