# ChefAgent — Week 4 Progress Log

**Date:** May 2026  
**Goal:** Orchestrator — intent classification, agent coordination, /chat endpoint, basic React UI  
**Status:** 🔲 In Progress

---

## What We're Building

The intelligence layer that takes a raw user message and figures out which agent(s) to call, in what order, and how to merge the response.

```
Week 3:  Dedicated endpoints — /recipes/search, /recipes/search-validated
          User had to know which endpoint to call and what parameters to send

Week 4:  Single /chat endpoint — user sends natural language
          Orchestrator figures out the rest

          User message + optional profile
               │
               ├─ IntentRouter   → classify intent + extract entities
               │
               ├─ AgentOrchestrator → route to right agent(s)
               │       ├─ SearchRecipe → Recipe Agent
               │       ├─ SearchRecipe + profile → Recipe Agent + Diet Agent
               │       ├─ ValidateDiet → Diet Agent
               │       ├─ CreateMealPlan → placeholder (Month 2)
               │       └─ GeneralQuestion → Ollama direct
               │
               └─ OrchestratorResponse → recipes + diet + human-readable message
```

---

## Conceptual Foundation

### Why an Orchestrator at all?

Without an Orchestrator, the client has to know the system's internal structure — which endpoint to call, what parameters to send, how to merge results. That's fine for a REST API but wrong for a conversational agent. The Orchestrator is what turns a collection of specialized tools into a system that feels intelligent.

### Multi-intent — MVP decision

When a message contains multiple intents ("find me a nut-free pasta and plan my week"), the MVP:
1. Executes the **primary intent** (recipe search with dietary validation)
2. Defers the secondary intent with an honest message ("Meal planning is coming — focusing on pasta for now")

Full multi-intent queuing is a Month 2 feature. Noted for later.

### Profile handling — MVP decision

For MVP, the dietary profile is sent with every `/chat` request as part of the request DTO. No server-side storage yet. Redis-backed session memory with `sessionId` persistence is Month 2.

The interface is designed so adding `sessionId`-based history later doesn't require a rewrite — `sessionId` is already in the `ChatRequest` DTO, just unused for now.

### Future improvements noted (Month 2+)

- Multi-intent: execute primary, queue secondary, tell user what was deferred
- Profile modes: weight gain, lean, juice diet, smoothie — profile options beyond allergies/restrictions
- Self-learning: track what users request, learn patterns over time
- Profile-aware LLM routing: LLM enriches intent with profile context before routing
- Redis session memory: profile + history persists across conversations by `sessionId`

---

## Day 1 — Intent Classification (`IntentRouter.cs`)

### Signal word mapping — rules layer

Before touching the LLM, the rules layer checks for signal words:

| Intent | Signal words / phrases |
|--------|----------------------|
| `SearchRecipe` | "find", "search", "recipe for", "what can I make with", "show me", "suggest", "ideas for", "looking for", "need a", "want to make", "cook", "dinner", "lunch", "breakfast" |
| `ValidateDiet` | "is this safe", "can I eat", "allergic", "check this", "safe for", "does this have", "contains", "suitable for", "okay for me", "will this work" |
| `CreateMealPlan` | "plan my week", "meal plan", "what should I eat this week", "weekly plan", "plan for the week", "schedule meals" |
| `ModifyMealPlan` | "swap", "change", "replace", "update my plan", "different recipe for", "switch Tuesday" |
| `GeneralQuestion` | "what is", "how do I", "tell me about", "explain", "weather", "help me understand" |

Unknown → LLM fallback.

### Entity extraction — hybrid layer

Rules handle explicit dietary terms embedded in the query:

| Pattern | Extracted entity |
|---------|-----------------|
| `"gluten-free X"` | `restrictions: ["gluten-free"]` |
| `"dairy-free X"` | `restrictions: ["dairy-free"]` |  
| `"vegan X"` | `restrictions: ["vegan"]` |
| `"nut-free X"` | `allergies: ["nuts"]` |
| `"without X"` | negation — passed to query preprocessor |

LLM handles implicit dietary terms and complex entity extraction:
- `"I can't have dairy"` — not a keyword match, needs LLM
- `"something light and plant-based"` — semantic intent, needs LLM
- `"I'm on a weight loss diet"` — profile inference, needs LLM

### Structured output from LLM

When LLM is called for intent classification or entity extraction, it returns:

```json
{
  "intent": "SearchRecipe",
  "search_query": "chicken dinner",
  "dietary_profile": {
    "restrictions": ["gluten-free"],
    "allergies": []
  },
  "deferred_intents": ["CreateMealPlan"],
  "deferred_message": "Meal planning is coming — focusing on finding you a recipe first."
}
```

### Files created

```
src/agents/Orchestrator/
├── IntentRouter.cs    # Rules-based classifier + LLM fallback + entity extraction
```

---

## Day 2 — Orchestrator Core Logic (`AgentOrchestrator.cs`)

### Routing flows

```
SearchRecipe (no dietary profile):
    → RecipeSearchPlugin.SearchRecipesAsync()
    → return results

SearchRecipe (with dietary profile in request OR extracted from message):
    → RecipeSearchPlugin.SearchRecipesAsync()
    → for each result: DietValidationPlugin.ValidateRecipeAsync()
    → sort compatible first
    → return annotated results

ValidateDiet (user provides specific recipe context):
    → DietValidationPlugin.ValidateRecipeAsync()
    → return validation with substitutions

CreateMealPlan / ModifyMealPlan:
    → return placeholder: "Meal planning coming in Month 2"

GeneralQuestion:
    → Ollama direct — conversational response

Unknown:
    → ask user to clarify
```

### Error handling

| Failure | Response |
|---------|----------|
| Recipe Agent fails | Helpful error, no recipes |
| Diet Agent LLM timeout | Recipes returned without validation + "dietary check unavailable" warning |
| Intent router LLM timeout | Fall back to `Unknown`, ask user to clarify |
| All agents fail | Graceful error, never 500 |

### Response shape

The Orchestrator builds `OrchestratorResponse` from `Models.cs`:

```json
{
  "message": "Here are 3 pasta recipes that work with your dairy-free diet. Recipe #2 has a minor flag — soy sauce may contain wheat.",
  "intent": "SearchRecipe",
  "recipes": [...],
  "dietaryCheck": null,
  "metadata": {
    "dietaryValidationApplied": true,
    "deferredIntents": ["CreateMealPlan"],
    "deferredMessage": "Meal planning is coming — focusing on pasta for now."
  }
}
```

### Response message generation

For MVP: template strings — fast, deterministic, no LLM cost.

```
SearchRecipe (no diet):
    "Here are {N} recipes for {query}."

SearchRecipe (with diet, all compatible):
    "Here are {N} {restriction}-friendly recipes for {query}."

SearchRecipe (with diet, some incompatible):
    "Found {N} recipes for {query}. {compatibleCount} are {restriction}-compatible.
     The rest have notes — check the details for substitution suggestions."

ValidateDiet:
    "Checked {recipeName} against your {restriction} profile. {result}."

GeneralQuestion:
    [LLM-generated — conversational, no template needed]
```

LLM-generated summaries as opt-in — Month 2.

### Files created

```
src/agents/Orchestrator/
├── IntentRouter.cs        # Day 1
├── AgentOrchestrator.cs   # Day 2
```

---

## Day 3 — /chat Endpoint (`Program.cs`)

### Updated endpoint

```
POST /chat
Body: { "message": "find me a dairy-free pasta dinner", "dietaryProfile": {...}, "sessionId": null }

Flow:
    message + profile → IntentRouter → classified intent + merged profile
                      → AgentOrchestrator → agent calls
                      → OrchestratorResponse → returned to client
```

### DI registrations added

```csharp
builder.Services.AddSingleton<IntentRouter>(...);
builder.Services.AddSingleton<AgentOrchestrator>(...);
```

### Request DTO

```csharp
record ChatRequest(
    string Message,
    string? SessionId = null,          // unused in MVP, reserved for Month 2
    DietaryProfile? DietaryProfile = null  // optional — merged with extracted profile
);
```

### Files changed

```
src/api/Program.cs    # IntentRouter + AgentOrchestrator DI, /chat endpoint wired
```

---

## Day 4 — React Chat UI

### Components

| Component | Purpose |
|-----------|---------|
| `ChatInput` | Text box + send button |
| `ChatMessages` | Scrollable message list |
| `RecipeCard` | Recipe result with dietary badges |
| `ProfileSidebar` | Dietary restriction toggles |

### Dietary profile toggles

User can set restrictions via sidebar — sent with every `/chat` request:
- Vegetarian / Vegan / Pescatarian
- Dairy-free / Gluten-free / Nut-free
- Jain / Sattvic / Halal

### Tech

- Vite + React
- Tailwind (core utilities only)
- Fetch to `/chat` endpoint

### Files created

```
src/frontend/
├── src/
│   ├── components/
│   │   ├── ChatInput.jsx
│   │   ├── ChatMessages.jsx
│   │   ├── RecipeCard.jsx
│   │   └── ProfileSidebar.jsx
│   ├── App.jsx
│   └── main.jsx
```

---

## Day 5 — End-to-End Testing

### Test matrix

20 scenarios across 6 groups. Script: `scripts/test_orchestrator.py`.

| # | Message | Profile | Expected Intent | Expected Agents |
|---|---------|---------|----------------|----------------|
| 01 | "chicken stir fry recipes" | none | SearchRecipe | Recipe |
| 02 | "gluten-free pasta ideas" | none | SearchRecipe | Recipe + Diet |
| 03 | "find me pasta" | `{restrictions: ["vegan"]}` | SearchRecipe | Recipe + Diet |
| 04 | "can I eat pad thai if allergic to peanuts?" | none | ValidateDiet | Diet |
| 05 | "what should I cook?" | none | Unknown → clarify | none |
| 06 | "what's the weather today?" | none | GeneralQuestion | Ollama |
| 07 | "find a soup and tell me if it's vegan" | none | SearchRecipe (primary) | Recipe + Diet |
| 08 | "dairy-free chicken dinner" | none | SearchRecipe | Recipe + Diet |
| 09 | "I can't have dairy, find me dinner" | none | SearchRecipe | Recipe + Diet |
| 10 | "plan my meals for the week" | none | CreateMealPlan | placeholder |
| 11 | "easy vegetarian weeknight meal" | none | SearchRecipe | Recipe + Diet |
| 12 | "is soy sauce gluten-free?" | none | ValidateDiet | Diet |
| 13 | "find recipes" | `{allergies: ["nuts"]}` | SearchRecipe | Recipe + Diet |
| 14 | "something warm and comforting" | none | SearchRecipe | Recipe |
| 15 | "swap Tuesday's dinner" | none | ModifyMealPlan | placeholder |
| 16 | "nut-free pasta and plan my week" | none | SearchRecipe (primary), CreateMealPlan (deferred) | Recipe + Diet |
| 17 | "jain-friendly dinner ideas" | none | SearchRecipe | Recipe + Diet |
| 18 | "I'm on a weight loss diet, find dinner" | none | SearchRecipe | Recipe + Diet (LLM entity extract) |
| 19 | "simple breakfast ideas" | `{restrictions: ["sattvic"]}` | SearchRecipe | Recipe + Diet |
| 20 | "what is a roux?" | none | GeneralQuestion | Ollama |

---

## Day 6-7 — Month 1 Wrap-Up

- [ ] Write `docs/adrs/005-orchestrator-design.md`
- [ ] Update README — full architecture diagram, /chat endpoint docs
- [ ] Write `docs/month1-retrospective.md`
- [ ] Clean up TODO comments
- [ ] Commit, push, tag `v0.1.0`

---

## Files Created / Changed (Full Week)

```
src/agents/Orchestrator/
├── IntentRouter.cs          # Rules classifier + LLM fallback + entity extraction
└── AgentOrchestrator.cs     # Agent coordination + response building

src/api/
└── Program.cs               # IntentRouter + AgentOrchestrator DI, /chat wired

src/frontend/
├── src/components/
│   ├── ChatInput.jsx
│   ├── ChatMessages.jsx
│   ├── RecipeCard.jsx
│   └── ProfileSidebar.jsx
├── App.jsx
└── main.jsx

eval/datasets/
└── orchestrator_test_cases.md

scripts/
└── test_orchestrator.py

docs/
├── adrs/005-orchestrator-design.md
└── month1-retrospective.md
```

---

## Concepts to Learn This Week

| Concept | What It Means |
|---------|---------------|
| Intent classification | Mapping natural language to a discrete action category |
| Entity extraction | Pulling structured data (dietary constraints, query terms) from unstructured text |
| Agent orchestration | Deciding which agents to call, in what order, with what inputs |
| Response synthesis | Merging structured agent outputs into a human-readable conversational response |
| Stateless vs stateful chat | Each request independent vs session memory — tradeoff between simplicity and continuity |
| Graceful degradation | System returns best possible result even when agents fail partially |

---

*Same principle as every week: fast by default, smart on demand. The rules-based intent classifier handles obvious cases instantly. LLM handles the rest. The Orchestrator never blocks on a slow path when a fast one will do.*