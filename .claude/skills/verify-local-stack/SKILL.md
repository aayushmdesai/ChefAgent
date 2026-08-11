---
name: verify-local-stack
description: Bring up ChefAgent's local dependencies and confirm every service is actually healthy, not just started. Use when asked to "start the app locally", "run ChefAgent", or "is the local stack working".
---

# Verify the local stack is healthy

Starting containers isn't the same as confirming the app works — this repo has multiple services that can be "up" but not actually functional (empty Qdrant collection, cold Ollama, unwarmed Redis). Bring-up plus checks, not just bring-up:

1. **Start**: `make up` (API + Ollama only, `docker-compose.local.yml`) or `make up-full` (full 6-service stack including Qdrant/Redis/Langfuse) — pick based on what you're actually testing; `up` is faster if Qdrant/Redis/Langfuse are already running from a previous session.
2. **API health**: `make health` — curls `http://localhost:5100/health`. A 200 here only proves the process started, not that its dependencies are reachable (missing API keys for cloud providers fail lazily on first use, per [src/CLAUDE.md](../../../src/CLAUDE.md) — this check alone won't catch that).
3. **Vector store**: `make check-vectors` — expect `{"status": "green", "points": N}`. `points: 0` means the collection exists but is empty (a common state after `docker compose down -v` or a fresh clone) — that's a silent failure mode for `/recipes/search` and `/chat`, not an error, so it's easy to miss without explicitly checking.
4. **Redis**: not a `make` target — check the API startup log for `[Startup] Redis pre-warm ping succeeded`. A `LogWarning` here (`"...ping failed — continuing"`) means the app will run but session/conversation history will silently degrade — the app won't crash, it'll just forget context.
5. **Ollama models** (only relevant if `LlmProvider=ollama`/`EmbeddingProvider=ollama` — check which provider is actually configured first, see [src/CLAUDE.md](../../../src/CLAUDE.md)): `make pull-models` if a fresh volume, confirm with a direct call rather than assuming the pull succeeded.

If vectors are missing: `make reload-vectors`, or `make fresh` for a full wipe-and-rebuild (drops volumes — confirm that's actually wanted first, per this repo's own safety norms). In a Codespace specifically, see [scripts/CLAUDE.md](../../../scripts/CLAUDE.md) — the vector file has to be manually uploaded there, `make reload-vectors` alone won't fix an empty collection.

## Verification (this skill's own point)

All four checks above pass — health 200, points > 0 matching the expected corpus size, Redis pre-warm succeeded in logs, and (if relevant) Ollama responds. Don't report "the stack is up" from step 1 alone.
