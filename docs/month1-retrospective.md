# ChefAgent — Month 1 Retrospective

**Date:** May 2026  
**Period:** Weeks 1–4  
**Target role:** AI Orchestrator / AI Infrastructure Engineer

---

## What We Built

A fully functional multi-agent recipe system in 4 weeks:

```
User message
    │
    ├─ IntentRouter (rules, < 1ms)
    │       classify intent + extract dietary entities
    │
    ├─ AgentOrchestrator
    │       SearchRecipe  → Recipe Agent → Diet Agent (optional)
    │       ValidateDiet  → Recipe Agent → Diet Agent
    │       GeneralQ      → Ollama direct
    │       MealPlan      → placeholder (Month 2)
    │
    └─ OrchestratorResponse
            per-recipe dietary validation + substitutions + human message
```

**Endpoints shipped:**
- `POST /recipes/search` — vector search, no diet
- `POST /recipes/search-validated` — vector search + diet validation
- `POST /chat` — natural language → Orchestrator → agents
- `GET /health`

**Stack:** Semantic Kernel (C#), Qdrant, Ollama, Redis (ready), React + Tailwind

---

## Week by Week

### Week 1 — RAG Pipeline

Built the entire data pipeline from scratch: download 10K recipes, parse, embed (Colab GPU), load into Qdrant, build .NET API with vector search.

**Key learning:** Embedding on 8GB CPU would take 67 hours. Switched to Google Colab for GPU embedding — same model (`nomic-embed-text`), 15 minutes. Always match hardware to workload.

**Baseline score gap (relevant vs irrelevant):** 0.1482 — the Month 1 measurement.

### Week 2 — Multi-Stage Retrieval

Added four pipeline stages: negation parsing (regex), query expansion (LLM opt-in), payload filtering (Qdrant), LLM re-ranking (opt-in).

**Key learning:** "Fast by default, smart on demand." The re-ranker is architecturally correct but CPU-unusable at 8GB. Same pattern applied to every LLM feature from here forward.

**Negation fix:** "pasta without tomatoes" was returning "Pasta With Tomatoes" as result #2. Regex negation parsing + post-retrieval filtering fixed it. Vector embeddings treat "with" and "without" nearly identically — negation must be handled outside the embedding.

### Week 3 — Diet Agent

Built a two-layer dietary validation system: 420+ phrase-level rules across 12 categories (dairy, gluten, nuts, eggs, soy, sesame, seafood, meat, jain, sattvic, paleo, halal), then LLM fallback for edge cases.

**Key learning:** Agent design is about knowing what doesn't need an LLM. The rules engine handles 94% of validations instantly and for free. LLM is called for ~5% of cases — only when safety requires it.

**Bug found in testing:** Vegetarian profiles were missing seafood violations — worcestershire sauce contains anchovies. Fixed by adding `SeafoodIngredients` check to the vegetarian restriction checker. Found because we wrote a 20-case test matrix, not because we got lucky.

**False positive fixed:** `"peanut butter"` was being flagged as a dairy violation (contains "butter"). Fixed with a `DairyFalsePositives` safe-list checked before the main rule scan.

### Week 4 — Orchestrator + UI

Built `IntentRouter` (rules-based, zero LLM), `AgentOrchestrator` (routes to right agents), wired `/chat` endpoint, React UI with dietary profile sidebar and per-recipe validation badges.

**Key learning:** SearchRecipe as the default intent (not Unknown) makes the system feel helpful rather than brittle. 94% accuracy with rules alone — LLM classification deferred to when a labeled dataset exists.

**Key decision:** `ValidatedRecipe` per-recipe shape (Option A) — every recipe in the response carries its own dietary validation. Required updating `OrchestratorResponse` and `AgentOrchestrator` but made the UI straightforward to build.

---

## What Worked

**The "fast by default, smart on demand" principle** — applied consistently across all four weeks:
- Week 2: negation parsing (regex, instant) before LLM expansion (opt-in)
- Week 3: rules engine (94% coverage) before LLM escalation (~5%)
- Week 4: rules classification (zero LLM) before LLM classifier (Month 2)

Every time we introduced an LLM feature, we made it opt-in and preserved a fast path. This is what kept the system usable on 8GB RAM throughout.

**Test matrices as debugging tools** — the 20-case diet agent test matrix found the vegetarian/seafood bug on Day 5. The 20-case orchestrator matrix caught the TC13 signal phrase gap immediately. Running structured tests before shipping is how real bugs get caught.

**Phrase-level matching in the rules engine** — the decision to match full known phrases (`"peanut butter"` = nut, not dairy) rather than word-boundary regex prevented an entire class of false positives. The matching strategy is the most important design decision in the Diet Agent.

**Documenting decisions as they're made** — progress logs, ADRs, and test matrices written during the week rather than retroactively. This is interview material that would be impossible to reconstruct from memory.

---

## What Surprised Me

**How much the hardware constraint shaped the architecture.** Every design decision from Week 2 onward was influenced by "will this work on 8GB CPU?" The opt-in LLM pattern, the rules-first approach, the SearchRecipe default — all of these were correct software design choices that also happened to be forced by hardware. The hardware constraint made the architecture better.

**How often "known limitations" turned out to be correct behavior.** The `AmbiguousSignals` skip tier — initially felt like a hack, but it's actually the right call. Better to skip a recipe than serve an incompatible one without warning. The false positive on coconut — flagged correctly per FDA labeling even if most people tolerate it. The rules engine's conservatism is a feature.

**How important test design is.** TC12 and TC16 in the diet test matrix failed because the vector search returned unexpected recipes — not because the system was wrong. Distinguishing "the system is wrong" from "the test is wrong" is a skill that only develops by writing many tests.

**How much the search query cleaning mattered.** `"find me pasta dinner"` being sent to Qdrant as-is would return worse results than cleaning it to `"pasta"`. `ExtractSearchQuery` is 30 lines of string replacement but meaningfully improves search quality for the most common message pattern.

---

## What I'd Do Differently

**Start with the evaluation dataset.** We built the system first and wrote tests after. Starting with 20 labeled test cases for each agent would have caught the vegetarian/seafood bug before the first API call, not on Day 5.

**Use `chicken broth` as a safe-list entry from day one.** `"vegetable broth"` triggering the ambiguous skip is a known limitation that should have been in `KnownSafePhrases` from the start. The pattern was clear — any named broth is a known ingredient.

**Split `Program.cs` earlier.** We let it grow to 200+ lines before splitting into `ServiceRegistration.cs`, `Endpoints.cs`, `RequestDtos.cs`. The split should happen at the first new agent registration.

---

## Month 1 Metrics

| Metric | Value |
|--------|-------|
| Recipes indexed | 10,000 |
| Embedding dimensions | 768 |
| Diet rules | 420+ phrases, 12 categories |
| Diet Agent rules coverage | 94% |
| Orchestrator intent accuracy | 94% (15/16 runnable cases) |
| LLM calls for intent classification | 0 |
| API endpoints | 4 |
| ADRs written | 5 |
| Test cases | 40 (20 diet + 20 orchestrator) |
| Real bugs found in testing | 3 (vegetarian seafood, peanut butter FP, TC13 signal phrase) |

---

## Month 2 Plan

| Feature | Why |
|---------|-----|
| Planner Agent | Weekly meal plans with Redis session memory |
| Redis session memory | Profile + history persists across conversations |
| LLM intent classification (opt-in) | Handle implicit constraints (`"I can't have dairy"`) |
| Contraction handling in rules | `"what's"` → `"what is"` |
| Multi-intent queuing | Execute primary, defer secondary with message |
| Langfuse observability | Trace agent calls, measure latency, find slow paths |
| RAGAS evaluation | Measure retrieval quality with ground truth labels |

---

## Interview Talking Points

**"Why rules before LLM for intent classification?"**
LLM on 8GB CPU takes 8-30 seconds. Rules take < 1ms. 94% accuracy with rules alone. The `rules-default` logs collect training data for a future classifier — we're not ignoring LLM, we're building toward it correctly.

**"How does the Diet Agent decide when to call the LLM?"**
Three-tier escalation: rules engine (94% of cases, instant) → ambiguous tier (skip if restriction-only, LLM if allergy + ambiguity) → LLM for unknown restriction types. The key insight: false negatives on allergies can be life-threatening, so we escalate to LLM. False negatives on restrictions are less critical, so we skip instead.

**"What would you do differently at scale?"**
Replace the rules-based intent classifier with a fine-tuned model trained on `rules-default` logs. Replace Ollama with a managed LLM API. Add Langfuse for observability — we need to know which agent is slow before we can optimize it. The architecture is the same; the components swap out.

**"What was the hardest bug?"**
The vegetarian/seafood violation miss. Worcestershire sauce contains anchovies, but the vegetarian restriction checker only looked at `MeatIngredients`, not `SeafoodIngredients`. Found it in the Day 5 test matrix when `"beef stew with worcestershire sauce"` passed vegetarian validation with no violations. One-line fix, but it would have been a real safety issue in production.

---

*Month 1 complete. The system works. The architecture is documented. The tests prove it. Month 2 is about memory, observability, and the Planner Agent.*