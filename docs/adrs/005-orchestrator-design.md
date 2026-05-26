# ADR 005 — Orchestrator Design: Intent Classification + Agent Coordination

**Date:** May 2026  
**Status:** Accepted  
**Authors:** Aayush Desai  
**Context:** ChefAgent — Month 1, Week 4

---

## Context

With two working agents (Recipe Agent, Diet Agent) and dedicated endpoints (`/recipes/search`, `/recipes/search-validated`), the system required a client to know its internal structure — which endpoint to call, what parameters to send, how to construct a `DietaryProfile`. That's fine for a REST API but wrong for a conversational agent.

The Orchestrator is the intelligence layer that turns a collection of specialized tools into a system that feels intelligent. It takes a raw natural language message and figures out: which agent(s) to call, in what order, with what inputs, and how to merge the response into a human-readable answer.

Three problems to solve:

1. **Intent classification** — what does the user want? (`SearchRecipe`, `ValidateDiet`, `CreateMealPlan`, `GeneralQuestion`)
2. **Entity extraction** — what constraints are embedded in the message? (`"gluten-free pasta"` → `restrictions: ["gluten-free"]`, `search_query: "pasta"`)
3. **Agent coordination** — which agents to call, in what order, and how to handle failures

---

## Decision

### Intent Classification — Rules-only for MVP

Two-layer design was considered (rules → LLM), but **rules-only with SearchRecipe as default** was chosen for MVP.

**Why not rules → LLM fallback:**
- LLM classification on 8GB CPU takes 8-30 seconds — unusable for interactive search
- Natural language is too varied for exhaustive rules, but SearchRecipe covers the gap
- `rules-default` in logs identifies every message that fell through — this becomes training data for a future LLM classifier

**Final design:**

```
Rules catch (unambiguous signal phrases):
  ValidateDiet    : "can i eat", "is this safe", "allergic to", ...
  CreateMealPlan  : "plan my week", "meal plan", "plan my meals for the week", ...
  ModifyMealPlan  : "swap", "replace tuesday", "update my plan", ...
  GeneralQuestion : "what is ", "how do i ", "explain ", ...

Default (everything else):
  SearchRecipe — most common intent, too varied for keyword rules
  Logged as "rules-default" for future dataset collection
```

**Test results:** 94% intent accuracy (15/16 runnable cases) with zero LLM calls. All classification completes in < 1ms.

### Entity Extraction — Rules-based

Rules extract dietary constraints embedded in the message:

```
"gluten-free pasta"     → restrictions: ["gluten-free"], query: "pasta"
"nut-free dessert"      → allergies: ["nuts"], query: "dessert"
"allergic to peanuts"   → allergies: ["peanuts"]
"vegan chicken dinner"  → restrictions: ["vegan"], query: "chicken"
"jain-friendly dinner"  → restrictions: ["jain"], query: "dinner"
```

Extracted profile is merged with any profile sent in the request DTO — union of restrictions and allergies, no duplicates. Profile from message + profile from DTO = full merged profile passed to agents.

**LLM entity extraction deferred to Month 2** — handles implicit constraints like "I can't have dairy" or "I'm on a weight loss diet" that rules can't catch.

### Agent Coordination

```
SearchRecipe (no profile)   → Recipe Agent only
SearchRecipe (with profile) → Recipe Agent → Diet Agent (per-recipe validation)
ValidateDiet                → Recipe Agent (find recipe) → Diet Agent (validate)
CreateMealPlan              → placeholder message (Month 2)
ModifyMealPlan              → placeholder message (Month 2)
GeneralQuestion             → Ollama direct (conversational, no recipe search)
Unknown                     → ask user to clarify
```

### Response Shape — ValidatedRecipe per recipe

Each recipe in the response carries its own dietary validation (`ValidatedRecipe`):

```json
{
  "recipes": [
    {
      "recipe": { "title": "...", "ingredients": [...] },
      "dietary": {
        "isCompatible": false,
        "violations": [...],
        "substitutions": [...]
      }
    }
  ]
}
```

This was chosen over a single top-level `dietaryCheck` field so the UI can show per-recipe badges without additional API calls.

### Response Messages — Templates, not LLM

```
SearchRecipe (no diet):   "Here are {N} recipes for '{query}'."
SearchRecipe (all match): "Here are {N} {profile}-friendly recipes for '{query}'."
SearchRecipe (partial):   "Found {N} recipes. {compatible} are {profile}-compatible. ..."
SearchRecipe (none):      "Found {N} recipes but none matched your {profile} profile. ..."
ValidateDiet (pass):      "'{title}' looks compatible with your profile. {explanation}"
ValidateDiet (fail):      "'{title}' has some issues. {explanation} Suggested swaps: ..."
```

LLM-generated summaries deferred to Month 2 as opt-in.

---

## Alternatives Considered

### Option A — LLM-first classification
Send every message to Ollama for classification and entity extraction.

**Rejected:** 8-30 second latency on CPU. Every request would be unusable. GPU or cloud deploy (Month 3) would make this viable.

### Option B — Rules + LLM fallback (original plan)
Rules catch obvious cases, LLM handles everything else.

**Rejected for MVP:** LLM fallback makes the "fast path" conditional. On 8GB CPU, any message that misses rules would be unusable. SearchRecipe as default gives the same result for the vast majority of messages at zero cost.

### Option C — Rules-only with SearchRecipe default (chosen)
Rules for unambiguous intents, SearchRecipe as the fallback.

**Accepted.** 94% accuracy at zero LLM cost. `rules-default` logs collect training data for a future classifier. Month 2 adds LLM classification as opt-in via `UseLlmClassification: true`.

### Option D — Single-intent only
Only handle one intent type, ignore others.

**Rejected:** ValidateDiet and GeneralQuestion are distinct enough from SearchRecipe to warrant separate routing — different agents, different response shapes.

---

## Key Design Decisions

### 1. SearchRecipe as default, not Unknown

Making `Unknown` the fallback and asking users to clarify would be frustrating — most ambiguous messages are recipe searches. `rules-default` as the fallback means the system is helpful even when classification is uncertain.

**Known limitation:** `"what's the weather today?"` returns recipe results. Acceptable for MVP — the alternative (Unknown) returns nothing, which is worse. Month 2: add contraction forms to `GeneralQuestionSignals`.

### 2. Profile merging

Message-extracted profile + DTO profile are merged at the `IntentRouter` level before routing. The Orchestrator receives a single `MergedProfile` and never needs to think about where the profile came from.

```
MergeProfiles(existing: {vegan}, extracted: {dairy-free}) 
→ {restrictions: ["vegan", "dairy-free"]}
```

### 3. Stateless by design

Each `/chat` request is independent. No session state, no conversation history. `SessionId` is in the DTO but unused — Redis-backed session memory is Month 2. Stateless design makes the system horizontally scalable and avoids the complexity of session management in Month 1.

### 4. Graceful degradation at every layer

| Failure | Response |
|---------|----------|
| Recipe Agent fails | Error message, no recipes |
| Diet Agent LLM timeout | Recipes without validation + warning |
| Intent LLM timeout (if enabled) | Unknown intent, ask to clarify |
| All agents fail | Graceful error message, never 500 |

No agent failure propagates to the user as an exception. The system always returns something useful.

### 5. API structure preserved

`/recipes/search` and `/recipes/search-validated` remain as direct endpoints. `/chat` is a conversational layer on top — not a replacement. This allows direct API testing and debugging without going through the Orchestrator.

---

## Test Results

20 test cases across 6 groups. Script: `scripts/eval/test_orchestrator.py`.

| Metric | Result |
|--------|--------|
| Total cases | 20 |
| Runnable (no timeout) | 16 |
| Pass | 15 (94%) |
| Timeout | 4 (GeneralQuestion + LLM Diet escalation on CPU) |
| LLM calls for classification | 0 |
| Per-recipe dietary shape | ✅ All correct |

**Intent classification breakdown:**
- `rules`: 5 cases — explicit signal words caught
- `rules-default`: 11 cases — SearchRecipe assumed
- `llm`: 0 cases — no LLM classification in MVP

---

## Consequences

**Positive:**
- Zero LLM calls for intent classification — all classification < 1ms
- 94% intent accuracy with rules alone
- `rules-default` logs provide training data for future classifier
- Graceful degradation at every layer — system never returns 500
- Stateless design — horizontally scalable, no session complexity

**Negative:**
- Contraction forms (`"what's"`, `"I'm"`) not caught by rules — fall to SearchRecipe default
- Implicit dietary constraints (`"I can't have dairy"`) not extracted — need LLM
- `GeneralQuestion` always calls Ollama — slow on CPU (8-30s)
- No conversation history — each message independent

**Neutral:**
- SearchRecipe as default means out-of-scope queries return recipes — acceptable tradeoff vs returning nothing

---

## Month 2 Roadmap

- LLM classification as opt-in (`UseLlmClassification: true` already in DTO)
- Redis session memory — profile + history persists by `sessionId`
- Contraction handling in rules (`"what's"` → `"what is"`)
- Implicit dietary constraint extraction via LLM
- Labeled dataset from `rules-default` logs → train proper classifier
- Multi-intent: execute primary, queue secondary, defer with message

---

## Related ADRs

- ADR 001 — Orchestration Framework
- ADR 002 — Vector Database (Qdrant)
- ADR 003 — LLM Provider (Ollama)
- ADR 004 — Diet Agent Architecture
- ADR 006 — Planner Agent (upcoming, Month 2)