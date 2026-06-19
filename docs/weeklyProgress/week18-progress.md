# Week 18 Progress — ChefAgent Improvements + Outreach Prep

**Month 5, Week 2.**

---

## Goals

- Complete ILlmProvider wiring through all remaining call sites ✅
- Re-ranker on Groq + eval measurement ⏳
- GeneralQuestion conversation context ⏳
- Upstash cold-start pre-warm ⏳
- Portfolio site proofread + update ⏳
- Company research + outreach message templates ⏳

---

## Day 1 — Wire ILlmProvider through remaining call sites ✅

### What changed

Three agents were still calling Ollama directly via raw `HttpClient` — bypassing
the `ILlmProvider` abstraction that every other component had used since Month 2.
This was explicitly deferred from Week 12 with a `TODO(Month4-cleanup)` comment.
Day 1 closed that debt.

**Files changed:**

| File | Change |
|------|--------|
| `IntentRouter.cs` | Replaced `HttpClient + ollamaUrl + chatModel` fields with `ILlmProvider`. `TryExtractProfileWithLlmAsync` now calls `_llmProvider.ChatAsync()` instead of building a manual Ollama JSON payload. 90s `CancellationTokenSource` retained — wraps the `ChatAsync` call. |
| `QueryPreprocessor.cs` | Same swap. `ExpandQueryAsync` single call site replaced with `ILlmProvider.ChatAsync()`. Private `OllamaChatResponse` / `OllamaChatMessage` records deleted. |
| `RecipeReranker.cs` | `CallOllamaAsync` renamed to `CallLlmAsync`, body replaced with `ILlmProvider.ChatAsync()`. Private records deleted. |
| `RecipeSearchPlugin.cs` | Removed dead `_httpClient` field and constructor param — had been unused since `IEmbeddingProvider` was wired in Month 2. |
| `ServiceRegistration.cs` | Updated `AddRecipeAgent` and `AddOrchestrator` to pass `ILlmProvider` instead of `(httpClient, ollamaUrl, chatModel)`. Removed dead locals. Updated dependency graph comment and IntentRouter registration comment. |

### Verification

```bash
grep -rn "_ollamaUrl\|/api/chat\|_httpClient" src/agents/ src/api/ --include="*.cs" \
  | grep -v "OllamaLlmProvider\|OllamaEmbeddingProvider\|//"
# → empty
```

Zero results. Every LLM call in the system now goes through `ILlmProvider`.

### What this means

The provider-agnostic claim is now unconditionally true. Before today, three
components had a silent asterisk: if you swapped `LlmProvider=groq` in env vars,
`IntentRouter`, `QueryPreprocessor`, and `RecipeReranker` would still call local
Ollama. Now `ServiceRegistration.cs` is the single source of truth for provider
selection — one env var change affects every LLM call in the system.

The re-ranker in particular should benefit immediately: it was designed in Month 2
but left on CPU Ollama where it was too slow for interactive use (30+ seconds).
Now it goes through Groq. Day 2 will measure the actual latency.

### Key learnings

The `TODO(Month4-cleanup)` comment in `RecipeReranker.cs` was the exact right pattern
for deferred debt — it survived two months without getting lost. When the time came,
the refactor was mechanical: swap constructor params, replace HTTP call, delete private
records, update ServiceRegistration. Total time: ~45 minutes across three files.

The `RecipeSearchPlugin._httpClient` dead field was only visible because the grep
verification cast a wider net than just `_ollamaUrl`. Worth running broad verification
greps rather than narrow ones — they catch dead code the targeted search misses.

---

## Days 2–7 — In progress

| Day | Focus | Status |
|-----|-------|--------|
| Day 2 | Re-ranker on Groq + eval | ⏳ |
| Day 3 | GeneralQuestion conversation context | ⏳ |
| Day 4 | Upstash cold-start pre-warm | ⏳ |
| Day 5 | Portfolio site proofread + update | ⏳ |
| Day 6-7 | Company research + outreach prep | ⏳ |