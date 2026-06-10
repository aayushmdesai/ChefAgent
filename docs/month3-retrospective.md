# ChefAgent — Month 3 Retrospective

**Date:** June 2026  
**Period:** Weeks 9–12  
**Target role:** AI Orchestrator / AI Infrastructure Engineer

---

## What We Built

A production-ready, cloud-deployed AI agent system with evaluation infrastructure, full observability, and a live public demo:

```
chefagent.vercel.app  (React UI)
        │
        ▼
chefagent-production.up.railway.app  (.NET API)
        │
        ├─ Groq (Llama 3.3 70B)      — LLM inference, 651ms GeneralQuestion
        ├─ Nomic Atlas API            — nomic-embed-text-v1, 163-615ms embed
        ├─ Qdrant Cloud (1GB free)    — 10K recipe vectors, cosine similarity
        ├─ Upstash Redis              — session memory, profile, plan storage
        └─ Langfuse Cloud             — full trace tree per request
```

**New this month:**

- RAGAS-style evaluation pipeline (local + Colab GPU runner)
- 100-query golden dataset across 12 categories
- Spell correction (SymSpell + food domain dictionary)
- Semantic negation fix — X-free expands to full ingredient sets
- Embedding cache — 1813ms → 12ms on repeated queries
- Self-hosted Langfuse v2 observability → migrated to Langfuse Cloud
- E2E eval harness + 60-case golden dataset
- LLM-as-judge scoring
- Cloud deployment — 6 local services → 5 free-tier cloud services
- `ILlmProvider` + `IEmbeddingProvider` provider abstraction
- Railway API deployment + Vercel frontend deployment
- Load test: 10/10 success at 10 concurrent requests

---

## Week by Week

### Week 9 — Evaluation Pipeline + Spell Check

Built the evaluation foundation: a two-stage pipeline where `retrieve.py` runs locally and `score_simple.py` runs on Colab GPU. Established baseline context_relevance of 0.470 on a 100-query golden dataset.

SymSpell + food domain dictionary replaced Hunspell as the spell corrector. The Hunspell problem was editorial: edit-distance alone picked "sup" over "soup" without frequency weighting. SymSpell's frequency-weighted approach fixes this — common words win over obscure ones.

**Key learning:** Frequency data matters in spell correction. The right correction isn't always the shortest edit-distance — it's the most likely word in the domain.

**Key decision:** RAGAS was operationally fragile (numpy conflicts, parallel executor timeouts, API quota issues). Replaced with a custom sequential Ollama-based scorer. Losing RAGAS's specific metrics was acceptable — the evaluation signal we needed was directional, not exact.

### Week 10 — Langfuse Observability

Built `Tracing.cs` — a fire-and-forget Langfuse client with a bounded `Channel<T>` background worker. Every `/chat` request produces a full trace tree: intent classification → orchestrator → recipe agent → embed → Qdrant → diet validation → response. Overhead: < 1ms on the request thread.

14 span types across all agents. `embed.cache_hit` vs `embed.provider` spans make cache effectiveness visible in Langfuse without reading logs. p50/p95/p99 latency per intent from `MetricsCollector`.

**Key learning:** Observability reveals what logs hide. The embedding cache delivering 12ms cache hits only became visually obvious once `embed.cache_hit` spans appeared in Langfuse. Same data was in the logs — but buried.

**Key decision:** `TraceContext` as an explicit parameter rather than ambient state. No static globals, no thread-local storage. Testable, composable, honest about its dependencies.

### Week 11 — E2E Eval + Semantic Negation Fix

Fixed the most visible retrieval gap: X-free queries. "dairy-free" was extracting `excludedTerms = ["dairy"]` and filtering on the literal string. Real recipes contain "milk", "cream", "butter" — not "dairy". The fix was one method call: `DietaryRules.GetCategoryIngredients(category)` instead of adding the raw prefix. Zero latency cost.

This required moving `DietaryRules.cs` from `ChefAgent.Agents.Diet` to `ChefAgent.Shared` — the right architectural home. `QueryPreprocessor` (RecipeAgent) needed dietary ingredient sets without depending on DietAgent. Shared is the correct dependency direction.

E2E eval harness (60 cases, 10 intent categories) + LLM-as-judge scoring revealed the real system gap: **the intent classifier is the weakest link**. Every e2e failure except one traced to intent misclassification. The retrieval, dietary validation, and session state layers all worked correctly once intent was routed right.

**Key learning:** Cascading failures are informative. Cases 33 and 34 failed because case 31 failed — not because modify or get were broken. Root vs cascading analysis saves time debugging the wrong layer.

**Key learning:** The judge layer catches what binary pass/fail misses. Implicit_dietary cases passed the harness (intent classified correctly) but scored H:2.33. The system extracted constraints and returned recipes — but recipes didn't respect the constraint. Pass/fail can't see that.

### Week 12 — Cloud Deployment

Migrated all 6 local services to free-tier cloud equivalents without touching a single line of agent code. The provider abstraction built in Days 1-2 was the mechanism: `ILlmProvider` and `IEmbeddingProvider` in `ServiceRegistration.cs`, config-driven, one file.

**HuggingFace DNS blocker.** `api-inference.huggingface.co` is blocked at DNS level in both Codespaces and Railway. Discovered mid-deployment. Decision: Nomic Atlas API — same `nomic-embed-text-v1` model, same vector space, no re-embedding. Reachable everywhere.

**Groq performance.** Llama 3.3 70B on Groq LPU: 287-371ms per GeneralQuestion. Local Ollama CPU with 3B model: ~14,000ms. 21x improvement, zero agent code changes.

**Upstash cold start.** `session.load_profile` and `session.append_user_message` both show ~3,000ms on first request per session — Upstash connection cold-start. Subsequent calls: 0-1ms. Documented as free-tier behavior, not a code issue.

**Load test: 10/10 success.** p50 4,314ms, p95 5,287ms at 10 concurrent requests. High latency is dominated by Upstash cold connection per new session — actual processing (embed + search + LLM) is 300-700ms. Warm sessions would show dramatically lower numbers.

---

## The Eval Story

Three experiments, one direction of travel:

| Experiment | Context Relevance | Answer Relevancy (x_free) | Notes |
|-----------|------------------|--------------------------|-------|
| Baseline | 0.470 | 0.213 | No preprocessing |
| + Spell check | 0.524 (+0.054) | 0.234 (+0.021) | SymSpell + food dict |
| + Semantic negation | 0.482 (-0.042) | 0.325 (+0.112) | X-free expansion |

The semantic negation fix trades context relevance for answer relevance on x_free queries — a deliberate, good tradeoff. Filtering out non-dairy recipes from "dairy-free pasta" reduces the retrieval pool (context relevance drops slightly) but dramatically improves the quality of what remains (answer relevance +0.112).

This is the correct behavior: a user asking for dairy-free pasta would rather see 2 compatible recipes than 5 recipes where 3 contain cheese.

**E2E results (60 cases via /chat):**

| Category | Pass Rate |
|----------|-----------|
| search_simple | 8/8 (100%) |
| search_negation | 6/6 (100%) |
| general_question | 2/2 (100%) |
| search_with_diet | 9/10 (90%) |
| implicit_dietary | 5/6 (83%) |
| create_meal_plan | 2/3 (67%) |
| get_meal_plan | 6/8 (75%) |
| modify_meal_plan | 3/6 (50%) |
| validate_diet | 3/8 (38%) |
| guardrail | 3/4 (75%) |

**Overall: 47/60 (78%), 49/57 (86%) adjusted** (excluding cascading failures from case 31).

All failures trace to intent classification — the correct next area to improve.

---

## The Observability Story

Month 3 moved from "grep the logs" to structured traces in Langfuse Cloud.

**What became visible:**

- `embed.cache_hit` vs `embed.provider` — cache effectiveness per query
- `diet.llm_validation` — when DietAgent escalates from rules to LLM and why
- `session.load_profile` — 3,000ms Upstash cold start vs 0ms warm
- Full span tree per request — bottleneck identification without log archaeology

**Production numbers from Langfuse Cloud traces (Railway):**

| Operation | Latency |
|-----------|---------|
| GeneralQuestion (Groq) | 287-371ms |
| Recipe search + embed (warm cache) | 65ms |
| Recipe search + embed (cold) | 443-499ms |
| Full pipeline with diet LLM validation | 739ms |
| MealPlan generation (7 Qdrant searches) | 1,118ms |
| Nomic embed cold | 163-666ms |
| Nomic embed warm | 107-191ms |

---

## The Deployment Story

6 local Docker services → 5 free cloud services. Zero agent code changed.

| Local | Cloud | Mechanism |
|-------|-------|-----------|
| Qdrant (Docker) | Qdrant Cloud | `Qdrant__Endpoint` env var |
| Ollama LLM | Groq | `LlmProvider=groq` env var |
| Ollama Embeddings | Nomic Atlas API | `EmbeddingProvider=nomic` env var |
| Redis (Docker) | Upstash | `Redis__ConnectionString` env var |
| Langfuse + Postgres | Langfuse Cloud | `Langfuse__BaseUrl` env var |
| .NET API (Docker) | Railway | Dockerfile already worked |

The entire swap happened in `ServiceRegistration.cs` (one file) and environment variables. No agent touched.

**What "provider-agnostic" actually means in practice:** when a provider fails (HuggingFace DNS blocked), you add a new `IEmbeddingProvider` implementation in ~30 lines and change one env var. The rest of the system doesn't know or care.

---

## What Worked

**"Rules for the common case, LLM for the edge case" is the right architecture for constrained hardware.**

Established in Week 2 under CPU-only constraints, this principle was validated across every new component in Month 3: spell correction (SymSpell + food dict before SymSpell before nothing), semantic negation (rules lookup, not LLM), evaluation scoring (sequential Ollama scorer after RAGAS proved fragile). Each component found the same natural boundary between rules and LLM.

**The interface/config boundary is the correct abstraction layer.**

`ILlmProvider` and `IEmbeddingProvider` with config-driven registration in one file (`ServiceRegistration.cs`) proved to be the right design. Groq swap: 30 minutes. Nomic swap: 15 minutes. Zero agent code changes. The abstraction wasn't over-engineering — it was the exact right level.

**Evaluation in layers catches different failure modes.**

RAGAS retrieval: are we finding the right documents? E2E harness: does the full pipeline route and respond correctly? LLM judge: is the response actually helpful? Each layer caught failures the others missed. x_free improved in e2e while context_relevance dropped in RAGAS — both true, both important, neither alone sufficient.

**Langfuse's fire-and-forget channel design was the right call.**

Zero blocking on the request thread. Traces are a side-effect, not a requirement. When Langfuse is unreachable, the recipe search succeeds. When the channel fills, old events are dropped — never unbounded memory growth.

---

## What Surprised Me

**HuggingFace free inference API is blocked at DNS level in both Codespaces and Railway.**

Not a code issue, not an API key issue — the domain simply doesn't resolve. A provider that looked like the obvious choice for cloud embeddings turned out to be the hardest to reach. The fix (Nomic Atlas API) was better: same model, canonical hosting, same vector space. But the discovery happened mid-deployment, not in planning.

**Upstash cold connection dominates load test latency.**

Single-request latency: 300-700ms. Load test p50: 4,314ms. The difference is Upstash's cold TCP connection per new session (~3,000ms). Processing time didn't change — it was the connection overhead on 10 simultaneous new sessions. Free tier behavior, documented, not a code issue.

**The intent classifier failure rate under e2e eval.**

78% pass rate sounds good until you see that every failure traces to intent misclassification. The retrieval pipeline, dietary validation, session memory, and guardrails all worked correctly. The weakest link isn't the complex LLM reasoning — it's the vocabulary coverage of a rules-based classifier. This is a useful finding: a few dozen new signal phrases would fix 80% of failures.

**21x latency improvement from a config change.**

Switching `LlmProvider=groq` and adding a 30-line `GroqProvider.cs` reduced GeneralQuestion from ~14,000ms to 651ms. The improvement isn't just "cloud is faster" — it's that Groq's LPU hardware runs Llama 3.3 70B (a much larger, better model) faster than local CPU runs Llama 3.2 3B. Better model, faster inference, from an environment variable change.

---

## What I'd Do Differently

**Test embedding provider reachability before committing to it.**

One `curl` to `api-inference.huggingface.co` from Codespaces would have revealed the DNS block before implementing `HuggingFaceEmbeddingProvider.cs`. Instead we implemented, deployed, debugged, then switched. The lesson: validate network reachability as the first step when adding any external API dependency.

**Wire `ILlmProvider` into all callers in one pass, not incrementally.**

`IntentRouter`, `QueryPreprocessor`, and `RecipeReranker` still call Ollama directly. Deferring them was the right scope decision for Week 12, but it means the provider abstraction is incomplete. The three remaining callers are all opt-in LLM paths that rarely fire — but they'll need to be addressed in Month 4 before any provider-specific behavior (like Groq rate limiting) can be guaranteed to work correctly end-to-end.

**Separate Upstash connection pooling from the session TTL design.**

The 3,000ms cold connection cost on every new session is structural — Upstash free tier doesn't maintain persistent connections. A connection warm-up on API startup (dummy Redis ping) would prime the pool and eliminate the cold start for the first real user request. Same pattern as the planned HuggingFace pre-warm.

**Write the eval golden dataset alongside the feature, not after.**

The semantic negation fix was completed in Week 11 but the negation test cases weren't in the golden dataset until the same week. The evaluation revealed the fix worked — but only because we happened to include x_free cases in Week 11. The intent classifier gaps (validate_diet question-form, CreateMealPlan phrasing variants) are now in the tech debt backlog as I-7, I-8, I-9 — they should have had test cases when the classifier was first built in Week 4.

---

## Month 3 Metrics

| Metric | Value |
|--------|-------|
| Retrieval baseline context_relevance | 0.470 |
| Retrieval post spell-check | 0.524 (+0.054) |
| x_free answer_relevancy improvement | +0.112 (0.213 → 0.325) |
| E2E pass rate | 47/60 (78%), 49/57 (86%) adjusted |
| LLM judge cases scored | 56 |
| Langfuse spans implemented | 14 types |
| Embedding cache reduction | 1813ms → 12ms (99.3%) |
| Cloud services migrated | 6 local → 5 cloud |
| Agent code changed for cloud | 0 lines |
| Groq latency improvement | 14,000ms → 651ms (21x) |
| Load test success rate | 10/10 (100%) |
| Load test p50 / p95 | 4,314ms / 5,287ms |
| Public URLs | 2 (Railway + Vercel) |
| ADRs written | 3 (ADR-010, ADR-011, ADR-012) |
| Provider implementations | 5 (Ollama LLM, Groq, Ollama Embed, HuggingFace, Nomic) |
| New interfaces | 2 (ILlmProvider, IEmbeddingProvider) |

---

## Month 4 Plan

| Feature | Why | Priority |
|---------|-----|----------|
| `ILlmProvider` wiring for IntentRouter, QueryPreprocessor, RecipeReranker | Complete the abstraction | High |
| Intent router: question-form ValidateDiet | Fix "is X vegan?" misclassification (I-8) | High |
| Intent router: CreateMealPlan phrasing variants | Fix "plan dinners... I'm dairy-free" (I-9) | High |
| `GeneralQuestion` conversation history | "how to make it" loses context (I-10) | Medium |
| Upstash connection pre-warm | Eliminate 3s cold start on first session | Medium |
| MCP server project | Month 4 primary deliverable | High |
| LinkedIn posts | Portfolio visibility | Medium |

---

## Interview Talking Points

**"How did Month 3 change the architecture from Month 2?"**
Month 2 added state — profile, history, plan. Month 3 added observability, evaluation, and portability. The system can now tell you what's happening inside every request (Langfuse), how well it's performing across categories (RAGAS + e2e), and run anywhere without modification (ILlmProvider/IEmbeddingProvider). Those three things — visible, measurable, portable — are what separate a working prototype from a production system.

**"Your load test shows p50 of 4 seconds. Isn't that slow?"**
The p50 of 4,314ms is dominated by Upstash's cold TCP connection per new session — about 3,000ms each. Actual processing time (embed + Qdrant search + optional LLM) is 300-700ms. The load test hit 10 new sessions simultaneously, so every request paid the cold start. Warm sessions in Langfuse Cloud show 65-500ms. The fix is a connection pre-warm on API startup — one Redis ping that primes the pool before any user request arrives.

**"You said zero agent code changed for the cloud migration. What actually changed?"**
`ServiceRegistration.cs` — one file, about 40 lines of provider selection logic. And `docker-compose.local.yml` — environment variables. `ILlmProvider` and `IEmbeddingProvider` were the key design decision: agents depend on interfaces, not HTTP clients. The interface is what makes the config swap possible. Without it, you'd be updating HTTP client URLs across 5 files in 3 different agents.

**"What's your evaluation strategy and what does it actually measure?"**
Three layers. RAGAS measures retrieval quality — does vector search find the right documents? E2E harness measures pipeline correctness — does the full system route intent correctly and produce a useful response? LLM judge measures subjective quality — is the response actually helpful? Each catches different failures. E2E pass/fail confirmed search and negation work; the judge caught that implicit_dietary responses retrieved correct results but didn't respect the extracted constraint. You need all three.

**"What's the weakest part of the system?"**
The intent classifier. Every e2e failure except one traced to intent misclassification — question-form ValidateDiet ("is X vegan?"), informal GetMealPlan phrasing, CreateMealPlan with embedded dietary context. The retrieval pipeline, dietary validation, session memory, and guardrails all performed correctly once intent was routed right. Fixing the intent classifier has the highest leverage on overall system quality. The good news: it's a rules vocabulary gap, not a fundamental design flaw.

---

_Month 3 complete. The system is evaluated, observable, and deployed. Architecture claims from 11 ADRs were validated in production. Month 4 is the MCP server and portfolio visibility._