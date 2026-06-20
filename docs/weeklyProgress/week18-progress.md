# Week 18 Progress — ChefAgent Improvements + Outreach Prep

**Month 5, Week 2.**

---

## Goals

- Complete ILlmProvider wiring through all remaining call sites ✅
- Re-ranker on Groq + eval measurement ✅ (finding: expansion matters more)
- GeneralQuestion conversation context ✅
- Upstash cold-start pre-warm ✅
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

### Key learnings

The `TODO(Month4-cleanup)` comment in `RecipeReranker.cs` was the exact right pattern
for deferred debt — it survived two months without getting lost. When the time came,
the refactor was mechanical: swap constructor params, replace HTTP call, delete private
records, update ServiceRegistration. Total time: ~45 minutes across three files.

The `RecipeSearchPlugin._httpClient` dead field was only visible because the grep
verification cast a wider net than just `_ollamaUrl`. Worth running broad verification
greps rather than narrow ones — they catch dead code the targeted search misses.

---

## Day 2 — Re-ranker investigation + query expansion fix ✅

### What we investigated

The plan for Day 2 was to test the re-ranker through Groq and decide whether to
make it default. That investigation revealed two things: the re-ranker was never
actually firing, and the more impactful fix was elsewhere.

**Re-ranker was never wired through the chat endpoint.**
`ChatRequest` has no `Rerank` field. `RecipeSearchPlugin.SearchRecipesAsync` has
`rerank: false` as a default. The orchestrator never passes it. So every curl with
`"rerank": true` in the body was silently ignored by the deserializer — the flag
existed only on `RecipeSearchRequest` (the `/recipes/search` endpoint), not on
`/chat`.

Hardcoded `rerank: true` temporarily at the orchestrator call site, pushed to
Railway, and tested. Results: inconsistent latency (1.2s vs 9.8s across two
queries), ordering barely changed from vector similarity. The temporary commit
was reverted.

**The real finding: `expand: false` was the default, and it was never overridden.**
`QueryPreprocessor.ExpandQueryAsync` had been built in Month 2 but never used in
production — the `expand` flag defaulted to `false` in `RecipeSearchPlugin` and
the orchestrator never set it. Abstract queries were hitting the vector index
literally, returning keyword matches instead of semantically appropriate results.

### The fix

One line change in `RecipeSearchPlugin.cs`:

```csharp
// Before:
bool expand = false,

// After:
bool expand = true,
```

`IsAbstract()` is rules-based and cheap — it checks for signal words (`something`,
`impressive`, `cozy`, `quick`, `fancy`, etc.) before deciding whether to call the
LLM. Concrete queries skip expansion entirely with zero latency cost.

### Before vs after

**"something impressive for a dinner party"**

| | Results |
|---|---|
| Before | Party Punch, Champagne Punch, Pink Party Wedding Punch, Cathy's Champagne Punch, Pretty Party Punch |
| After | Beef Tenderloin Stuffed with Lobster, Meat Soufflé, Steak with Lobster Tail, Beef Wellington, Paella |

**"something cozy for a cold night"**

After: Beef Pot Pie, Gourmet Stew, Shepherd's Pie, Shepard's Pie, Meal-In-A-Dish

### Latency profile

| Query type | Example | Latency |
|---|---|---|
| Concrete (no expansion) | "chicken tikka masala" | 0.7s |
| Abstract, Railway warm | "something cozy for a cold night" | ~4s |
| Abstract, Railway cold | "something impressive for a dinner party" | ~9s |

The 4-9s range on abstract queries is Groq expansion (~2-3s) + Railway/Upstash
overhead. Cold start adds ~5s on top of warm latency — same Upstash issue as
always, addressed in Day 4.

### Re-ranker decision

Deferred — not default-on yet. The expansion fix addresses retrieval quality
directly; re-ordering bad candidates doesn't help. Once retrieval is consistently
good, re-ranking becomes the next lever. The wiring work (Day 1) means it would
now go through Groq if enabled — the infrastructure is ready, the decision is
just not yet justified by the data.

### Key learnings

**Retrieval quality > reordering quality.** The re-ranker can only reorder what
retrieval found. If vector similarity returns 5 punch recipes for "dinner party",
the re-ranker reorders 5 punch recipes. Getting the right candidates matters more
than the order of wrong ones.

**Flag defaults are silent bugs.** `expand: false` in `SearchRecipesAsync` was
correct under Month 2 CPU constraints (LLM expansion on CPU took 30+ seconds).
When Groq replaced Ollama in Month 3, the constraint disappeared but the default
didn't change. Worth auditing other opt-in flags when the hardware constraint
that motivated them no longer applies.

---

## Days 3–7 — In progress

| Day | Focus | Status |
|-----|-------|--------|
| Day 3 | GeneralQuestion conversation context | ⏳ |
| Day 4 | Upstash cold-start pre-warm | ⏳ |
| Day 5 | Portfolio site proofread + update | ⏳ |
| Day 6-7 | Company research + outreach prep | ⏳ |

## Day 3 — GeneralQuestion conversation context ✅

### What changed

`GeneralQuestion` was stateless — every follow-up question lost all context from
the previous turn. "What is blanching?" followed by "how long does it take?" would
return 5 recipe results instead of answering about blanching duration.

Three components changed:

**`AgentOrchestrator.cs` — `HandleGeneralQuestionAsync`**
Now loads the last 6 history entries from Redis before calling the LLM:
```csharp
history = await _sessionStore.GetHistoryAsync(classified.SessionId, limit: 6);
var answer = await AskLlmAsync(classified.SearchQuery, history);
```

**`AgentOrchestrator.cs` — `AskLlmAsync`**
Accepts an optional history parameter and builds a multi-turn prompt:
```csharp
foreach (var entry in history.TakeLast(6))
    messages.Add(new ChatMessage(entry.Role, entry.Content));
messages.Add(new ChatMessage("user", question));
```

**`IntentRouter.cs` — context continuation check**
Short follow-up messages that default to `SearchRecipe` are now reclassified as
`GeneralQuestion` when the last assistant turn was also `GeneralQuestion`:
```csharp
if (intent == UserIntent.SearchRecipe
    && history?.LastOrDefault(e => e.Role == "assistant")?.Intent == UserIntent.GeneralQuestion
    && lower.Split(' ').Length <= 8)
{
    intent = UserIntent.GeneralQuestion;
}
```

**`SessionStore.cs` — `JsonStringEnumConverter`**
The root cause of the initial failure: `UserIntent` enum was serializing as an
integer to Redis and deserializing back as `null`. The context continuation check
was reading `null` instead of `GeneralQuestion` and short-circuiting. Fix:
```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
};
```

### Verification

```
Turn 1: "what is blanching?"
→ "Blanching is a cooking technique where food is briefly submerged in boiling
   water or steam, then immediately cooled in an ice bath..."

Turn 2: "how long does it take?"
→ "Blanching time varies depending on the food, but typically 30 seconds to
   5 minutes, with most vegetables taking 1-3 minutes."

Turn 3: "can I do it with broccoli?"
→ "Yes, broccoli can be blanched, typically for 2-3 minutes, until tender
   but still crisp."
```

All three turns correctly use context from the previous exchange.

### Key learnings

**Enum serialization is a silent Redis bug.** Enums serialize as integers by
default in `System.Text.Json`. `UserIntent.GeneralQuestion` stored as `4` in
Redis, deserialized as `null` (nullable enum default). The fix is one line but
the symptom — context continuation silently not firing — took multiple debug
cycles to trace back to serialization. Any nullable enum stored in Redis needs
`JsonStringEnumConverter`.

**Infrastructure failures mask feature bugs.** The context continuation code was
correct from the first commit. It appeared broken because the Redis circuit
breaker was tripping on cold start, causing history loads to return empty. Day 4's
pre-warm was required before Day 3's feature could be verified.

---

## Day 4 — Redis pre-warm + connection fix ✅

### What changed

Two problems were causing the Redis circuit breaker to trip on every cold start:

**Problem 1: Wrong connection string format.**
The Railway env var was set to the Upstash REST URL (`rediss://...`) instead of
the StackExchange.Redis native format. `ConfigurationOptions.Parse()` was
duplicating the port, producing `hostname:6379:6379` which never connected.

**Fix:** Updated Railway env var to StackExchange.Redis format:
```
set-sailfish-143031.upstash.io:6379,password=...,ssl=True,abortConnect=False
```

**Problem 2: No startup pre-warm.**
Even with the correct connection string, the first Redis operation after a cold
Railway start took ~3,000ms — long enough to trip the circuit breaker (3
consecutive timeouts) and leave it open for 30 seconds. All history loads during
that window returned empty, breaking conversation context.

**Fix:** Added a Redis ping in `Program.cs` immediately after `app.Build()`:
```csharp
var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
await redis.GetDatabase().PingAsync();
app.Logger.LogInformation("[Startup] Redis pre-warm ping succeeded");
```

This moves the cold-start penalty to deployment time. The circuit breaker never
trips because by the time the first user request arrives, the connection is warm.

**Bonus fix:** `/stack` endpoint was displaying `"Ollama"` for embedding provider
even when Voyage was configured. Added the missing `"voyage"` case:
```csharp
"voyage" => "Voyage",
```

### Verification

Railway startup logs now show:
```
[Startup] Redis pre-warm ping succeeded
```

Multi-turn conversation context works correctly from the first request after
deployment — no 30-second window where history loads silently fail.

### Key learnings

**Circuit breaker cooldown periods can mask feature correctness.** The 30-second
Redis circuit breaker cooldown meant features dependent on session history appeared
broken even after the code was correct. Pre-warming moves infrastructure
reliability to deployment time, not request time.

**Connection string format matters.** StackExchange.Redis and REST clients use
different connection string formats. Upstash provides both — always use the native
Redis format for StackExchange.Redis, not the `rediss://` URL format.

---

## Days 5–7 — In progress

| Day | Focus | Status |
|-----|-------|--------|
| Day 5 | Portfolio site proofread + update | ⏳ |
| Day 6-7 | Company research + outreach prep | ⏳ |