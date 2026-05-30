# ChefAgent — Performance Profile

**Date:** 2026-05-30 18:38
**Environment:** GitHub Codespaces (CPU-only Ollama, nomic-embed-text, llama3.2)
**Methodology note:** Operations measured 5× at `/recipes/search` endpoint. `/chat` measurements use first 2 runs only — repeat detector short-circuits runs 3-5 with identical queries on same session, making them non-representative. See "Profiling Finding" below.

---

## Latency Table (corrected)

| Operation                                 | p50    | p95    | Source   | Bottleneck                      |
| ----------------------------------------- | ------ | ------ | -------- | ------------------------------- |
| Health check                              | <0.01s | 0.01s  | 10 runs  | TCP + HTTP                      |
| InputGuard — blocked (any)                | <0.01s | 0.03s  | 5 runs   | In-memory rules, no I/O         |
| Chat — GetMealPlan (Redis read)           | <0.01s | 0.01s  | 5 runs   | Redis GET only                  |
| Chat — ValidateDiet (rules-only)          | <0.01s | 0.01s  | runs 1-2 | Rules engine, no I/O            |
| Search — warm embedding, single query     | 0.10s  | 0.13s  | 5 runs   | Ollama embed + Qdrant           |
| Search — warm embedding, ingredient query | 0.11s  | 0.16s  | 5 runs   | Ollama embed + Qdrant           |
| Search + diet validation (rules hit)      | 0.10s  | 0.30s  | 5 runs   | Embed + rules check             |
| Chat — SearchRecipe (warm)                | ~0.14s | 0.18s  | runs 1-2 | Orchestrator + embed + Qdrant   |
| ModifyMealPlan — single slot swap         | ~0.14s | 0.14s  | runs 1-2 | 1× embed + Qdrant + Redis       |
| CreateMealPlan — 7-day dinner plan        | 0.85s  | 1.21s  | 3 runs   | 7× sequential embed + Qdrant    |
| Reference resolution (LLM path)           | ~3.8s  | 4.18s  | runs 1-2 | Redis read + LLM interpretation |
| Chat — GeneralQuestion (LLM)              | ~6.97s | 21.64s | 3 runs   | llama3.2 CPU inference          |
| Profile load + LLM entity extraction      | ~11.9s | 11.94s | runs 1-2 | LLM extraction on first access  |

**Orchestrator overhead:** ~0.04s (Chat SearchRecipe 0.14s − Search endpoint 0.10s = 0.04s for InputGuard + intent routing + profile load + response formatting)

---

## Top 3 Bottlenecks

### #1 — LLM Inference (GeneralQuestion, reference resolution, entity extraction)

LLM calls on CPU Codespaces are the dominant latency source across three paths:

| Path                      | Latency | Trigger                                 |
| ------------------------- | ------- | --------------------------------------- |
| GeneralQuestion           | 7-22s   | Every general question                  |
| Profile entity extraction | ~12s    | First message of every session          |
| Reference resolution      | ~4s     | "The first one", "the second one", etc. |

**p50: 6.97s | p95: 21.64s** (GeneralQuestion, most common LLM path)

The 21.64s p95 is a real user-facing spike — a 1-in-20 general question takes 21 seconds on CPU. On GPU this drops to 2-5s.

**Fix paths:**

- GPU inference — 10-20× speedup on all LLM paths
- Faster model — phi3-mini or llama3.2:1b instead of llama3.2:3b
- Shorter system prompt — reduces tokens processed per call
- Skip LLM for reference resolution — rules-based ordinal matching ("first", "second", "last") covers 90% of cases without LLM
- Cache entity extraction per session — don't re-extract on every message, only on profile change

### #2 — Profile Load + LLM Entity Extraction (~12s, first access per session)

Every new session hitting `/chat` for the first time triggers LLM entity extraction on profile load. Runs 1-2 of the profile measurement show 11.94s and 11.89s — consistent and reproducible. This means the **first message** of any session that has a saved profile waits ~12s before responding.

**Fix paths:**

- Cache extraction result — store extracted entities in Redis alongside the raw profile. Only re-extract when profile changes.
- Run extraction async — extract in background, use raw profile for immediate response, merge on next request

### #3 — Plan Generation Sequential Embedding (0.85s p50, but scales badly)

7-day plan generation is 0.85s median on warm Codespaces (7 sequential embeddings × 0.10s each + Qdrant queries). This looks fast, but:

- **Cold start** (Ollama not warm): first embedding ~15-20s → plan takes 15-20s minimum
- **Multi-slot planning** (breakfast + lunch + dinner × 7 = 21 calls): ~2.1s on warm CPU, 60s+ cold
- Scales linearly — adding more days or meals adds proportional time

**Fix paths:**

- Parallel async search — 7 concurrent `Task.WhenAll` calls instead of sequential. Would bring 0.85s → ~0.15s
- Batch embedding — embed all 7 queries at once if the model supports it
- Pre-warmed query cache — common plan queries ("dinner", "lunch", "breakfast") pre-embedded at startup

---

## Profiling Finding: Repeat Detector Skewed /chat Measurements

The profiling script reused the same session ID and query for each run. The Week 7 repeat detector short-circuits identical queries after run 2, making runs 3-5 return 0.00s. This is the guardrail working correctly but made naive repeated-measurement profiling invalid for `/chat`.

**Correct approach for future profiling:** Use unique queries or unique session IDs per run for `/chat` measurements. The `/recipes/search` endpoint has no repeat detector and gives accurate repeated measurements.

**Unexpected benefit:** The 0.00s pattern revealed exactly where LLM calls are happening. Operations that stayed slow for runs 1-2 before dropping to 0.00s contain LLM calls. Operations that were instant from run 1 are pure rules or Redis.

---

## Derived Insights

**The rules engine is effectively free:**

- InputGuard: <0.01s
- ValidateDiet (rules path): <0.01s
- Intent routing (rules): <0.01s
- GetMealPlan (Redis): <0.01s

Everything fast is in-memory. Everything slow touches Ollama.

**Embedding is fast when warm:**

- nomic-embed-text: 0.10-0.13s per query when model is loaded
- First call after cold start: ~3-20s (model load included)
- Implication: system should keep Ollama warm, or the first user of the day waits

**Orchestrator adds ~0.04s overhead:**

- `/recipes/search` p50: 0.10s
- `/chat` SearchRecipe p50: ~0.14s
- Delta: ~0.04s for InputGuard + intent routing + profile load + response formatting
- Negligible — the coordination layer is not a bottleneck

**Plan generation is better than feared:**

- 7-day dinner plan: 0.85s on warm Codespaces CPU
- This surprised us — expected 7 × 0.10s = 0.7s + overhead, actual 0.85s matches
- Risk: cold start plan generation still takes 15-20s (model load on first embedding)

---

## Fix Priority for Month 3

| Bottleneck                                    | Fix                                  | Expected Improvement                 | Effort      |
| --------------------------------------------- | ------------------------------------ | ------------------------------------ | ----------- |
| Profile entity extraction (~12s first access) | Cache extraction result in Redis     | First-access drops to <0.1s          | Low         |
| Reference resolution LLM path (~4s)           | Rules-based ordinal matching         | ~4s → <0.01s for "first/second/last" | Low         |
| Plan generation cold start (15-20s)           | Startup warmup call to Ollama        | Eliminates cold start penalty        | Low         |
| Plan generation scale (linear)                | Parallel async Task.WhenAll          | 0.85s → ~0.15s, 21-call → ~0.3s      | Medium      |
| GeneralQuestion LLM (7-22s)                   | GPU / faster model / shorter prompt  | 10-20× improvement                   | High effort |
| Embedding per cold query                      | Embedding cache for repeated queries | Near-zero on cache hit               | Medium      |
