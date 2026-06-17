# Week 16 Progress — Production Fix + Eval + IntentRouter

**Month 4, Week 16 | Dates: June 2026**  
**Repos: ChefAgent + mcp-dotnet-diagnostics**

---

## Goals

- Fix production (Nomic exhausted → Voyage migration) ✅
- Re-run eval pipeline on 52k dataset + new embeddings ✅
- Fix IntentRouter vocabulary gaps ✅
- Publish Post #2 ✅
- Publish Post #3 + community seeding
- Month 5 prep

---

## Day 1 — Voyage AI Migration ✅

### Problem

Production was down. Nomic Atlas API free tier (10M tokens) exhausted
mid-run during the Week 15 dataset expansion. Query-time embedding calls
were returning HTTP 400, breaking recipe search entirely.

### Fix

Migrated query-time embeddings from Nomic Atlas API to Voyage AI.

**New file: `src/shared/VoyageEmbeddingProvider.cs`**
- Implements `IEmbeddingProvider` — same interface as Nomic
- Endpoint: `POST https://api.voyageai.com/v1/embeddings`
- Auth via `Authorization: Bearer {key}` header
- Parses `data[0].embedding` from response
- ~30 lines, structurally identical to `NomicEmbeddingProvider.cs`
- Default model: `voyage-4-lite`

**Updated: `src/api/ServiceRegistration.cs`**
- Added `voyage` registration block alongside existing `nomic`, `huggingface`, `ollama` blocks
- Config: `EmbeddingProvider=voyage`, `Voyage__ApiKey=...`, `Voyage__Model=voyage-4-lite`
- Zero agent code changes — provider-agnostic architecture validated again

### Re-embedding 52k Recipes

The stored vectors in Qdrant were 768d Nomic vectors. Voyage outputs
different dimensions, requiring a full re-embed and Qdrant reload.

**Model selected: `voyage-4-lite`**
- 200M free tokens per account (52k recipes × ~300 tokens = ~15.6M tokens — well within free tier)
- 1024d output vectors
- Designed for retrieval

**Embedding approach: Voyage Batch API**

Standard Voyage API has 3 RPM rate limit on free tier — 52k recipes at
batch size 50 would take ~5.8 hours with constant 429 errors. The Batch
API has no RPM limit: bundle all requests into a JSONL file, submit once,
poll until complete.

Process:
1. Prepared `batch_input.jsonl` — 1,044 requests × 100 recipes each
2. Uploaded to Voyage Files API
3. Submitted batch job with `voyage-4-lite`, 12h completion window
4. Polled every 60s — completed in ~12 minutes (1,044/1,044 requests)
5. Downloaded results, converted to `recipe_vectors_voyage.jsonl`
6. Ran `load_qdrant.py` — deleted 768d collection, created 1024d, uploaded 52,155 points

**Qdrant Cloud final state:**
- Points: 52,155
- Vectors: dim=1024, distance=Cosine

**Local verification:**
```bash
curl -X POST http://localhost:5100/recipes/search \
  -H "Content-Type: application/json" \
  -d '{"query":"chicken stir fry","maxResults":3}'
# → "Easy Chicken Stir-Fry" (0.745), "Stir-Fry Chicken" (0.731),
#   "Stir-Fry Minced Chicken With Vegetables" (0.727) ✅
```

### Architecture Validation (Again)

Nomic → Voyage swap required:
- 1 new file (`VoyageEmbeddingProvider.cs`)
- 1 registration block in `ServiceRegistration.cs`)
- 0 agent code changes

Time from start to working local test: ~1 day (mostly waiting for
Qdrant upload and debugging env var issues).

This is the second provider swap validated in production
(first was Ollama → Groq in Week 12). The `IEmbeddingProvider`
abstraction held.

### Debugging Notes

- `.env.local` vars not picked up by `dotnet run` directly — need
  `export $(grep -v '^#' .env.local | xargs)` or inline env vars
- Docker Compose `--env-file` injects vars for `${VAR}` substitution
  in the compose file, but `env_file:` in the service block is what
  actually injects them into the container — both needed
- Voyage Batch API output line order is not guaranteed — use `custom_id`
  to map results back to input docs, not line position
- `voyage-4-lite` outputs 1024d (not 512d as older `voyage-3-lite` docs
  suggested) — dimension auto-detected by `load_qdrant.py` from first doc
- Colab notebook loaded only 26,100 of 52,155 docs on first pass (likely
  encoding issue mid-file) — fixed by reloading with `errors='ignore'`
  and iterating over `results` dict instead of `docs` list in step 7

### Commit

```
feat(embeddings): migrate from Nomic to Voyage AI

- VoyageEmbeddingProvider.cs: new IEmbeddingProvider backed by
  voyage-4-lite (1024d, 200M free tokens)
- ServiceRegistration.cs: add voyage provider registration block
- generate_embeddings_voyage.py: batch embedding script with resume
  support and rate limit handling
- Qdrant Cloud reloaded: 52,155 recipes at 1024d cosine
- Zero agent code changes — provider-agnostic architecture validated

EmbeddingProvider=voyage in Railway env vars to activate
```

---

## Day 2 — Eval Pipeline Re-run (voyage-4-lite, 52k corpus) ✅

### Setup

- Added `time.sleep(25)` between questions in `retrieve.py` to respect
  Voyage's 3 RPM free tier limit at query time
- Added retry with backoff on 429 in `VoyageEmbeddingProvider.cs` —
  respects `Retry-After` header, falls back to `20s × attempt`
- `score_simple.py` unchanged — Ollama running on Colab (llama3.2)

### Results

**Experiment:** `eval/experiments/2026-06-14_voyage_52k.json`  
**Baseline:** `eval/experiments/2026-06-07_spell_check.json`

| Metric | Baseline | Experiment | Delta |
|---|---|---|---|
| context_relevance | 0.524 | 0.578 | ↑ +0.054 |
| faithfulness | 0.444 | 0.477 | ↑ +0.033 |
| answer_relevancy | 0.234 | 0.279 | ↑ +0.045 |

**Per-category context relevance:**

| Category | Baseline | Experiment | Delta |
|---|---|---|---|
| technique | 0.550 | 0.800 | ↑ +0.250 |
| dietary | 0.458 | 0.675 | ↑ +0.217 |
| misspelling | 0.617 | 0.800 | ↑ +0.183 |
| multi_intent | 0.463 | 0.600 | ↑ +0.137 |
| situation | 0.425 | 0.562 | ↑ +0.137 |
| cuisine | 0.613 | 0.725 | ↑ +0.112 |
| exact_match | 0.613 | 0.725 | ↑ +0.112 |
| filtering | 0.438 | 0.488 | ↑ +0.050 |
| by_ingredients | 0.610 | 0.630 | ↑ +0.020 |
| x_free | 0.588 | 0.450 | ↓ -0.138 |
| negation | 0.503 | 0.280 | ↓ -0.223 |
| edge_case | 0.433 | 0.167 | ↓ -0.266 |

### Analysis

**Wins driven by corpus size (10k → 52k):** technique, dietary, cuisine,
multi_intent, situation all improved significantly. More candidates in
Qdrant means better top-k matches.

**Indian recipes paying off:** dietary +0.217 is the clearest signal —
the Indian recipe dataset added in Week 15 directly improved retrieval
for dietary-specific queries.

**SymSpell holding up:** misspelling +0.183 across an embedding model
change validates that spell correction is doing real work upstream of
the vector search.

**Regressions — negation (-0.223), x_free (-0.138), edge_case (-0.266):**
Likely `voyage-4-lite` encodes "without X" and "X-free" queries
differently than Nomic did — pulling toward recipes containing X rather
than excluding it. This is not a RAG problem, it's an IntentRouter /
DietAgent filtering gap. The edge_case regression is mostly noise (tiny
sample, inherently low-signal queries like "food", "r", "xkqzpw blarfnog").

**Root cause of negation/x_free regressions:** these query types need
post-retrieval filtering in DietAgent, not better embedding. The embedding
model retrieves semantically similar recipes; exclusion logic must happen
at the agent layer. Flagged as tech debt.

### Commits

```
eval: voyage-4-lite 52k experiment results

Overall: context_relevance +0.054, faithfulness +0.033, answer_relevancy +0.045
Wins: technique +0.250, dietary +0.217, misspelling +0.183
Regressions: negation -0.223, edge_case -0.266, x_free -0.138
```

```
fix(voyage): add retry with backoff on 429
```

---

## Deferred

- Colab notebook (`chefagent_embeddings_voyage_batch.ipynb`) — commit
  to `scripts/pipeline/` once cleaned up
- IntentRouter vocabulary gaps (Day 3)
- Post #2 publish (Day 4)
- Post #3 publish + community seeding (Day 5)
- Month 5 prep (Days 6-7)
- Negation/x_free regression investigation (tech debt)

---

## Day 3 — IntentRouter Fixes + E2E Sweep ✅

### Fixes Applied

**I-1, I-2, I-3 — MealPlan vocabulary gaps:**
- `CreateMealPlan`: added "make me a new plan", "plan dinners for the week",
  "plan breakfast lunch and dinner", "create a meal plan", "help me plan my week" and variants
- `ModifyMealPlan`: added "change/switch/update/move {day}" for all 7 days
- Fixes TC23, TC24, TC26 from Week 8 e2e sweep

**I-8 — ValidateDiet question forms:**
- Added `DietQuestionRegex`: `\bis\s+\w[\w\s]{0,30}(vegan|vegetarian|halal|kosher|gluten.free|dairy.free|nut.free|safe)\b`
- Added `CanEatRegex`: `\bcan\s+(vegans?|vegetarians?|a\s+\w+\s+allergy\s+person)\s+eat\b`
- Removed bare `"can i eat"` string — too broad, matched search queries
- Added back `"can i eat this"` and `"can i eat it"` — specific enough to be unambiguous
- Fixes e2e-025, 028, 030, 055, 056, 057

**I-9 — CreateMealPlan without "my":**
- Added "plan dinners for the week", "plan meals for the week" etc.
- Fixes e2e-031 and cascading setup failures for e2e-033, 034, 037, 040

**GetMealPlan variants:**
- Added "what did you plan", "what have you planned", "what's on {day}" for all days
- Added "remind me what i am having" (normalized from "remind me what i'm having")
- Fixes e2e-042, e2e-044

**e2e-046 — "I can't have gluten, what pasta can I eat?" → ValidateDiet fix:**
- Removed broad `"can i eat"` signal
- `CanEatRegex` now only matches third-person forms ("can vegans eat X")

**Shared folder refactor:**
- Reorganized `src/shared/` into `Providers/Embeddings/`, `Providers/Llm/`,
  `Guardrails/`, `Observability/` subfolders
- Updated all namespaces and using statements

### E2E Sweep Results

**Dataset:** `eval/datasets/e2e_golden_dataset.json` (60 cases)
**Experiment:** `eval/experiments/2026-06-14_e2e_intentrouter_fixes.json`

| Category | Passed | Failed | Timeouts |
|---|---|---|---|
| create_meal_plan | 2/3 | 0 | 1 |
| general_question | 2/2 | 0 | 0 |
| get_meal_plan | 6/7 | 1 | 0 |
| guardrail | 3/4 | 1 | 0 |
| implicit_dietary | 4/6 | 0 | 2 |
| modify_meal_plan | 0/4 | 4 | 0 |
| search_negation | 5/6 | 0 | 1 |
| search_simple | 6/8 | 0 | 2 |
| search_with_diet | 7/11 | 1 | 3 |
| validate_diet | 6/9 | 0 | 3 |
| **Overall** | **41/60** | **6** | **13** |

**True logic pass rate: 41/47 evaluated = 87%**
(13 timeouts are Voyage 3 RPM infrastructure, not logic failures)

### Remaining Failures Analysis

| ID | Root cause | Status |
|---|---|---|
| e2e-016 | "paleo" not in DietAgent rules — 0 recipes returned | Tech debt, deferred |
| e2e-033/034/037/040 | Setup plan timed out → no plan in Redis for modify | Infrastructure cascade |
| e2e-053 | Repeated query guardrail needs session state — fresh session each run | Known gap |

### Timeout Root Cause

Voyage free tier: 3 RPM. Meal plan generation fires 7+ embedding calls per
request (one per recipe per slot). During eval, concurrent requests cascade
into 429s. The retry logic handles single 429s but can't recover from
sustained rate limiting across concurrent requests.

Fix path: embedding cache (warm on second request), or higher-tier Voyage
account. Deferred.

### Commits

```
refactor(shared): organize providers and guardrails into subfolders
fix(tests): update using statements after shared folder refactor
fix(intent): add MealPlan and ModifyMealPlan vocabulary gaps (I-1, I-2, I-3)
fix(intent): ValidateDiet question forms, GetMealPlan variants, CreateMealPlan without 'my' (I-8, I-9)
fix(intent): remove 'can i eat' broad signal, rely on CanEatRegex instead
fix(intent): normalize contraction in GetMealPlan signal (e2e-042)
eval: e2e sweep Day 3 after IntentRouter fixes
docs(eval): update README and add Week 16 experiment results
```

---

## Deferred

- Colab notebook (`chefagent_embeddings_voyage_batch.ipynb`) — commit to `scripts/pipeline/`
- Post #2 publish (Day 4)
- Post #3 publish + community seeding (Day 5)
- Month 5 prep (Days 6-7)
- Negation/x_free regression investigation (tech debt)
- Voyage rate limit fix for meal plan generation (embedding cache or paid tier)
- e2e-053 repeated query guardrail (needs session state in eval harness)
- e2e-016 paleo dietary rules

---

## Day 4 — Fix 2 (negation/x_free) + Fix 3 (paleo)

### Meal Plan 429 Cascade — Root Cause Documentation

**Why it happens:**

The `MealPlannerPlugin` generates a 7-day plan by calling `SearchRecipesAsync`
once per meal slot per day. A full breakfast/lunch/dinner plan = 21 embedding
calls. Each call hits the Voyage API for a unique query ("yogurt fruit breakfast",
"wrap lunch", "pork tenderloin dinner" etc.) — no cache hits since queries are
all different.

Voyage free tier: 3 RPM = 1 request per 20 seconds. 21 requests back-to-back
saturates the rate limit window immediately. The retry logic in
`VoyageEmbeddingProvider.cs` handles single 429s but can't recover from
sustained saturation across 21 concurrent/sequential calls.

**Why caching doesn't help:**

The in-memory `ConcurrentDictionary` cache in `RecipeSearchPlugin` only helps
for repeated identical queries. Meal plan generation queries are all unique
(different slot + day combinations), so there are no cache hits.

A Redis-backed cache across deploys would help for user-initiated searches
(same query asked twice across sessions) but not for meal plan generation.

**Fix paths considered:**

| Option | Effect | Cost |
|---|---|---|
| A — Throttle planner (Task.Delay 20s between slots) | Works, plan takes 7+ minutes | Bad UX |
| B — Redis embedding cache | Helps repeat searches, not meal plans | Engineering work, partial fix |
| C — Accept + retry logic (current) | Plan succeeds eventually, latency variable | Free |
| D — Voyage paid tier | No rate limit, ~$0.02/1M tokens | ~$0 at current usage |

**Decision: Option C for now.** Retry logic already in place. Plans succeed
when the embedding cache has warm entries from prior searches. Flagged as
tech debt — revisit if meal plan UX becomes a portfolio demo concern.


### Fix 2 — Negation/x_free Regression: Code Audit

Reviewed `QueryPreprocessor.cs` negation pipeline:
- `ParseNegation` extracts excluded terms from patterns like "without X", "no Y", "X-free"
- `FreePattern` expands "dairy-free" → full `DairyIngredients` set via `DietaryRules.GetCategoryIngredients`
- `FilterNegations` removes recipes whose ingredients match any excluded term post-search

**Finding: the code is correct.** The negation/x_free regression in the RAGAS
eval (-0.223 negation, -0.138 x_free) is not a logic bug — it's an embedding
model difference.

`voyage-4-lite` encodes "pasta without tomatoes" semantically closer to
"pasta with tomatoes" than `nomic-embed-text` did. The top-k candidates
retrieved from Qdrant are lower quality before filtering runs. Post-retrieval
filtering is working correctly but can't rescue bad initial candidates.

**Root cause:** embedding models differ in how they represent negation in
vector space. Some models treat "without X" and "with X" as near-identical
(negation is syntactic, not semantic). Others encode the negation signal.
`voyage-4-lite` appears to be in the former category for food queries.

**Fix path (deferred):** Use a higher-quality embedding model (voyage-4 or
voyage-4-large) that may better encode negation. Or add a negation-aware
re-ranking step post-retrieval. Both require either paid tier or GPU.

**Decision: No code change.** Post-retrieval filtering already in place.
Document as embedding model limitation.

### Fix 3 — Paleo: Already Implemented

Audited `DietaryRules.cs`:
- `PaleoExclusions` set: legumes, grains, dairy, refined sugar, processed oils ✅
- `RestrictionCheckers["paleo"]` mapped ✅
- `ExtractProfile` now extracts "paleo" from queries ✅

e2e-016 failure ("find me a paleo dinner" → 0 recipes) is a **corpus gap**,
not a code gap. The 52k recipe corpus has no explicitly paleo-tagged recipes.
DietAgent correctly flags most returned recipes as paleo violations, leaving
0 compatible results. Fix requires either a paleo recipe dataset or loosening
the validation threshold.

**Decision: No code change.** Document as corpus limitation.

### Day 4 Summary

No new code changes — audit confirmed existing implementations are correct.
Three issues diagnosed and documented:

| Issue | Root cause | Fix path |
|---|---|---|
| Meal plan 429 cascade | Voyage 3 RPM × 21 embedding calls | Paid tier or throttling |
| negation/x_free RAGAS regression | voyage-4-lite doesn't encode negation well | Better model or reranker |
| e2e-016 paleo 0 results | Corpus gap — no paleo recipes in dataset | Paleo recipe dataset |


---

## Days 6-7 — Month 5 Prep

### Post Engagement (final, pulled end of Week 17)

| Post | Date | Impressions | Members Reached | Reactions | Comments | Saves | Link Clicks |
|------|------|-------------|------------------|-----------|----------|-------|-------------|
| #1 (Multi-agent architecture) | 6/10 | 636 | 402 | 16 | 0 | 2 | 2 |
| #2 (RAG eval deep dive) | 6/12 | 455 | 255 | 10 | 1 | 0 | 3 |
| #3 (MCP server diagnostics) | 6/16 | 534 | 314 | 12 | 0 | 1 | 0 |

**Notes:**
- Post #1's original same-day placeholder numbers (429 impressions / 14 reactions)
  were early-read figures before the post finished circulating — final pulled
  numbers are meaningfully higher across the board (636 impressions, 16 reactions).
  Worth remembering that day-of LinkedIn numbers are not final; wait 3-5 days
  before recording them as the official figure.
- Post #1 has the strongest reach of the three, likely the novelty of the
  multi-agent architecture topic plus it being the first post in the series.
- Post #2 had the lowest impressions/reach but is the only post with a comment —
  small sample, but worth noting that technical deep-dive content (RAG eval
  specifics) may engage fewer people more deeply rather than more people lightly.
- Post #3 had zero link engagement (no GitHub link in that post, unlike #1 and #2
  which linked to `github.com/aayushmdesai/ChefAgent`) — explains the 0 in that
  column; not a performance signal, a content difference.
- All three posts skew toward Software Engineer job titles (29-35%) and
  Entry/Senior seniority — aligns with the intended technical audience.

### Month 5 Plan — Portfolio + Resume + Outreach

**Goal:** Land SDE2 or Senior SDE interviews at big tech companies where AI agent
work is required. Story: "Senior .NET engineer who built production AI agent
infrastructure from scratch — multi-agent orchestration, RAG pipelines, MCP
servers."

---

#### Resume — 5 Steps (Priority Order)

**Step 1 — Add Projects section (highest leverage)**
Two new entries: ChefAgent and mcp-dotnet-diagnostics.
These are the primary differentiators for AI roles and are currently missing entirely.

ChefAgent bullets to write:
- Multi-agent architecture (Orchestrator, RecipeAgent, DietAgent, PlannerAgent)
- RAG pipeline: 52k recipes, Voyage-4-lite embeddings, Qdrant Cloud, eval results
- Provider-agnostic design: Ollama → Groq swap (21x latency), Nomic → Voyage (zero agent changes)
- 5-layer guardrails, Langfuse observability (14 span types), RAGAS eval pipeline
- Cloud deploy: Railway + Vercel, Upstash Redis, Groq, Qdrant Cloud

mcp-dotnet-diagnostics bullets to write:
- Open-source .NET global tool MCP server, published to NuGet
- 7 runtime diagnostics tools exposed to AI assistants (Claude Desktop)
- CI/CD with auto-publish on version tags, submitted to MCP directories

**Step 2 — Rewrite summary**
Lead with AI agent infrastructure, not healthcare context.
Current: "Backend Software Engineer with 5 years in healthcare technology..."
Target: "Senior Software Engineer specializing in AI agent infrastructure and
distributed backend systems..."

**Step 3 — Reorder and expand skills**
Move AI & Agent Systems to top or second position.
Add: Python, Qdrant, Semantic Kernel, Langfuse, Voyage AI, Ollama, Groq,
     RAG pipelines, RAGAS evaluation, MCP (Model Context Protocol)

**Step 4 — Reframe 2-3 Carenet bullets**
Epic/Athena/Meditech are unknown to big tech recruiters.
Reframe: "EHR integration" → "third-party API integration at scale"
Keep the impact numbers, remove the healthcare product names.

**Step 5 — Title and positioning**
Resume already says "Software Engineer" — correct, keep it.
Add "Senior" framing in summary and project scope language.

---

#### Portfolio Site — Structure

**Goal:** Show depth that doesn't fit on a resume. Primary audience: recruiters
and hiring managers who want to understand what you built and how you think.

**Pages:**
1. **Home** — headline, 2-line story, links to projects + GitHub + LinkedIn
2. **ChefAgent** — full technical writeup:
   - Architecture diagram (the ByteByteGo-style dark diagram from Post #1)
   - Agent breakdown (what each agent does, why)
   - Key decisions (provider-agnostic design, rules vs LLM, eval-driven development)
   - Eval results (RAGAS numbers, e2e pass rate)
   - Live demo link + GitHub
3. **mcp-dotnet-diagnostics** — writeup:
   - What MCP is and why it matters
   - The 7 tools and what they expose
   - Install command, NuGet link, demo GIF
4. **About** — 3-4 sentences, not a life story. Link to resume PDF.

**Tech stack:** React + Tailwind, deploy to Vercel, custom domain if available.
**Timeline:** Month 5 Week 17-18

---

#### Outreach — Target List (to build)

- 15-20 companies with active AI agent / AI infrastructure roles
- Mix: big tech (Microsoft, Google, Amazon, Meta, Apple) + AI-first (Anthropic,
  OpenAI, Cohere, Mistral) + enterprise AI (Salesforce, ServiceNow, Workday)
- Strategy: LinkedIn DM to engineering managers, not just apply through portal
- Reference: ChefAgent live demo + mcp-dotnet-diagnostics NuGet as conversation starters

**Timeline:** Month 5 Week 19-20

---

#### Week 17 Preview (next week)

- Day 1-2: Write ChefAgent and mcp-dotnet-diagnostics resume bullets
- Day 3: Rewrite summary + reorder skills + reframe Carenet bullets
- Day 4-5: Start portfolio site scaffold (React + Tailwind, deploy to Vercel)
- Days 6-7: Portfolio site Home + ChefAgent page content