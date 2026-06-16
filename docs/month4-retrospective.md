# ChefAgent — Month 4 Retrospective

**Date:** June 2026  
**Period:** Weeks 13–16  
**Target role:** AI Orchestrator / AI Infrastructure Engineer / Senior SDE

---

## What We Built

Month 4 had two parallel tracks: a new open-source project and public portfolio visibility.

```
Track 1: mcp-dotnet-diagnostics (Weeks 13-14)
  github.com/aayushmdesai14/mcp-dotnet-diagnostics
  NuGet: dotnet tool install -g mcp-dotnet-diagnostics
  Listed: Glama, mcpservers.org, awesome-mcp-servers (pending)

Track 2: LinkedIn + ChefAgent production fixes (Weeks 15-16)
  3 posts written, 2 published
  Production migrated: Nomic → Voyage AI
  Dataset expanded: 10k → 52k recipes
  IntentRouter: 44/50 (Week 8) → 87% on 60-case dataset (Week 16)
```

**New this month:**

- `mcp-dotnet-diagnostics` — 7 MCP tools exposing .NET runtime diagnostics to AI assistants
- Claude Desktop integration — autonomous multi-tool health check demo
- CI pipeline with NuGet auto-publish on version tags
- 34 unit + integration tests across all 7 tools
- 3 ADRs documenting C#-over-TypeScript, target-process design, .NET 10 payload extraction fix
- LinkedIn posts: Post #1 (429 impressions, 14 reactions), Post #2 (published), Post #3 (written)
- Voyage AI migration — production restored, 52k recipes re-embedded
- IntentRouter: I-1 through I-3, I-7 through I-9, I-10 all fixed
- E2E sweep: 60-case dataset, 87% pass rate on evaluated cases
- ADR-013 documenting Voyage migration decision trail
- `src/shared/` folder reorganization (Providers, Guardrails, Observability)

---

## Week by Week

### Week 13 — mcp-dotnet-diagnostics Scaffold

Built the MCP server from scratch: 7 diagnostic tools, Claude Desktop integration,
34 tests. The core challenge was the .NET 10 EventPipe payload structure change —
counter data is wrapped one level deeper under a `"Payload"` key of type `StructValue`
in .NET 10, undocumented, discovered through runtime inspection.

The most important architectural insight: **tool descriptions are infrastructure**.
The LLM reads descriptions to decide when and whether to call each tool. A vague
description means the wrong tool gets called. Each description includes what it returns,
what symptoms trigger it, and what to call first — teaching Claude the correct
investigation sequence implicitly, not through hardcoded orchestration.

**Key decision:** C# over TypeScript. The MCP ecosystem leans TypeScript, but
`Microsoft.Diagnostics.NETCore.Client` is .NET-native. A TypeScript wrapper would
require shelling out to CLI tools rather than using the SDK directly. C# also
differentiates the project.

**Key learning:** `Environment.ProcessId` is a valid integration test target. The
xUnit runner is a live .NET process with a diagnostic socket — no separate test app
needed for most integration tests.

### Week 14 — CI, NuGet, Open-Source Release

The gap between "works on my machine" and "open-source project" is larger than it looks.
This week covered CI (GitHub Actions, ubuntu-latest, auto-publish on `v*` tags), NuGet
packaging as a global tool (`PackAsTool=true`), README polish, demo GIF, and three MCP
registry submissions.

**Key finding:** Glama's Dockerfile for .NET has three non-obvious gotchas — PATH
doesn't persist across Docker RUN layers, `debian:trixie-slim` is missing `libicu`,
and the published apphost looks for the runtime in `/usr/share/dotnet` not `/root/.dotnet`.
None documented anywhere. Discovered through iteration.

**Key decision:** `--skip-duplicate` on `dotnet nuget push` makes CI pipelines
idempotent. Without it, re-running on an existing version fails the job.

**Key learning:** The demo GIF is worth more than 1000 words of documentation. The
52% LOH fragmentation badge in red, "Needs attention" callout, Claude firing tools
autonomously — communicates the value proposition in one frame.

### Week 15 — LinkedIn Posts + Dataset Expansion

Three posts written:
- Post #1: Multi-agent architecture — "6/50 failures, all from the intent router, not the agents"
- Post #2: RAG eval deep dive — "measuring is harder than building"
- Post #3: MCP server demo — "without tools, it would have guessed. With the right setup, it looked."

Writing about your work is a distinct skill from doing the work. The discomfort is
real — for 14 weeks every day had a clear output: a file, a passing test, a deployed
service. Posts have fuzzier "done" criteria and require visibility in a way code doesn't.

Also expanded the recipe dataset from 10k → 52k using `corbt/all-recipes` (Western)
and `Anupam007/indian-recipe-dataset` (5,938 Indian recipes with `cuisine` field).
This work also revealed the Nomic Atlas API token exhaustion that took production down.

**Key finding:** Nomic Atlas API free tier (10M tokens) is insufficient for a 52k
recipe corpus (~15.6M tokens needed). Any future corpus expansion would hit the same
limit. This triggered the Voyage AI migration in Week 16.

### Week 16 — Production Fix + Eval + IntentRouter

Three distinct work streams:

**Production fix (Day 1):** Migrated from Nomic → Voyage AI. `VoyageEmbeddingProvider.cs`
is ~30 lines. Qdrant reloaded with 52k recipes at 1024d (up from 768d). Zero agent changes —
third provider swap validated. Used the Voyage Batch API to embed 52k recipes in ~12
minutes with no rate limits, rather than ~5.8 hours at 3 RPM on the standard API.

**Eval re-run (Day 2):** RAGAS results on 52k Voyage corpus vs 10k Nomic baseline:
context_relevance +0.054, faithfulness +0.033, answer_relevancy +0.045. Corpus size
drove the improvement more than embedding model quality. Negation/x_free regressed —
voyage-4-lite encodes exclusion queries differently than Nomic. Post-retrieval filtering
correct but can't rescue weaker initial candidates.

**IntentRouter fixes (Day 3):** Fixed I-1 through I-3, I-7 through I-10. E2E sweep
went from 35/60 → 52/60 (warm cache run) → 41/60 (cold cache run). True logic pass
rate: 41/47 evaluated = 87%. 13 timeouts are Voyage 3 RPM infrastructure, not logic.

---

## The Two Projects Tell a Complete Story

| Project | Layer | What It Demonstrates |
|---|---|---|
| ChefAgent | AI application | Orchestration, RAG, memory, guardrails, eval, observability, cloud deploy |
| mcp-dotnet-diagnostics | AI infrastructure | MCP protocol, tool design, .NET runtime internals, open-source release |

The positioning target for Month 5 job search: Senior SDE / AI Infrastructure roles
where the ability to build both the application layer and the platform layer is valued.

---

## The Eval Story (Month 4 Additions)

Month 3 baseline experiments used a 10k Nomic corpus. Month 4 adds:

| Experiment | Context Relevance | Delta vs baseline |
|---|---|---|
| Month 3 baseline (nomic, 10k) | 0.470 | — |
| + Spell check (nomic, 10k) | 0.524 | +0.054 |
| + Semantic negation (nomic, 10k) | 0.482 | -0.042 vs spell_check |
| Voyage 52k (voyage-4-lite, 52k) | 0.578 | +0.054 vs spell_check |

The 52k Voyage result is the strongest overall — more candidates in the corpus means
better top-k matches across almost every category except negation/x_free.

**E2E results (60-case golden dataset):**

| Category | Week 16 (cold) | Week 16 (warm) | Note |
|---|---|---|---|
| validate_diet | 6/9 | 9/9 | I-8 fixed |
| create_meal_plan | 2/3 | 3/3 | I-9 fixed |
| get_meal_plan | 6/7 | 7/7 | I-7, I-10 fixed |
| modify_meal_plan | 0/4 | 4/4 | Cascade from timeout — setup plan creation timed out |
| search_simple | 6/8 | 8/8 | Voyage 429 timeouts |
| guardrail | 3/4 | 3/4 | e2e-053 known gap |

---

## What Worked

**The provider abstraction proved its value three times.**

HuggingFace → Nomic (Month 2), Ollama → Groq (Month 3), Nomic → Voyage (Month 4).
Three provider swaps, zero agent code changes. `IEmbeddingProvider` and `ILlmProvider`
with config-driven registration in `ServiceRegistration.cs` was the correct design.
The abstraction wasn't over-engineering — it was exactly the right level of indirection.

**Tool descriptions as infrastructure (mcp-dotnet-diagnostics).**

The most important lesson from building MCP tools: the description is the contract
between you and the LLM. Well-written descriptions — including what the tool returns,
what symptoms trigger it, and what to call first — produced autonomous multi-tool
chaining without any hardcoded orchestration. Claude discovered the correct investigation
sequence from the descriptions alone.

**Rules-for-the-common-case, data for the edge case.**

IntentRouter fixes I-1 through I-10 were all signal vocabulary additions. No LLM
classification needed for any of them. The 87% pass rate on 60 cases came from
carefully chosen phrase-level rules, not ML. This validates the core architectural
principle established in Month 2.

**The Voyage Batch API eliminated the rate limit problem for bulk embedding.**

3 RPM on the standard API would have taken ~5.8 hours with constant 429 errors.
The Batch API took 12 minutes with no rate limiting — submit once, poll, download.
This is the correct tool for bulk one-time workloads.

**Open-source project lifecycle end-to-end in two weeks.**

Week 13: working code. Week 14: CI, NuGet, README, demo GIF, registries.
The gap between "works locally" and "installable by a stranger" — packaging, docs,
discoverability — was the entire deliverable of Week 14.

---

## What Didn't Work

**Nomic Atlas API free tier is too small for a 52k corpus.**

This was discoverable in advance: 52k recipes × ~300 tokens = ~15.6M tokens,
well above the 10M free tier limit. A one-line token estimate before running
the embedding would have caught this. Instead it was discovered mid-run when
production went down.

**Voyage 3 RPM saturates on meal plan generation.**

A 7-day breakfast/lunch/dinner plan fires 21 embedding calls. At 3 RPM that
saturates the rate limit immediately. The retry logic handles individual 429s
but can't recover from sustained saturation across 21 calls. This is a structural
constraint of the free tier, not a code problem — but it should have been modeled
before choosing Voyage as the query-time provider.

**E2E eval timing is inconsistent across runs.**

The 60-case sweep gave 52/60 on a warm-cache run and 41/60 on a cold-cache run.
The delta is 100% explained by Voyage 429 timeouts on cold requests. An eval
harness that depends on cache state for consistency is misleading — the reported
number depends on when you ran it. Fix: pre-warm the embedding cache before the
sweep, or exclude timeout cases from the pass rate denominator.

---

## What Surprised Me

**The .NET 10 EventPipe payload structure changed without documentation.**

`PayloadValue(0)` in .NET 10 wraps counter data under a `"Payload"` key of type
`StructValue` — one level deeper than in earlier versions. All EventCounter values
returned zero until this was discovered through runtime inspection with a debug tool.
Nothing in the official documentation mentioned this change. The pattern of writing
a diagnostic tool to debug the diagnostic library is now a standard approach.

**The Batch API was the right answer and it was obvious in retrospect.**

The standard Voyage API has 3 RPM. A 52k recipe embedding job at that rate would
take hours with constant errors. The Batch API — submit all requests as a JSONL file,
poll until complete — finished in 12 minutes. This is the obvious tool for bulk
one-time workloads. It should have been the first thing checked, not discovered after
hitting rate limits.

**Post #1 got 2 saves from 429 impressions.**

Saves are the strongest signal on LinkedIn — someone wanted to come back to it.
The post had no comments, no DMs, no viral spread. But 2 saves from 429 impressions
(0.5% save rate) on a first technical post is a meaningful signal. Technical content
compounds slowly — Post #2 and #3 build on the same audience.

**87% pass rate came from vocabulary additions, not ML.**

Every IntentRouter fix was a handful of new strings in a `HashSet<string>`. No model
training, no embeddings, no LLM calls. The system went from 35/60 to 52/60 on the
hardest run by adding phrases like "make me a new plan" and "change friday" to signal
sets. The ML layer is valuable for the edge cases — the common cases are just vocabulary.

---

## What I'd Do Differently

**Model API token budgets before committing to a provider.**

Before choosing Nomic for the 52k corpus, one calculation:
52,155 recipes × ~300 tokens = ~15.6M tokens > 10M free tier.
That calculation takes 30 seconds and would have prevented a production outage.
Validate token budgets as the first step when adding any embedding provider.

**Use the Batch API for all bulk embedding jobs.**

The standard API is designed for interactive use. The Batch API is designed for bulk
one-time workloads. These are different tools. Future re-embedding (corpus expansion,
model migration) should default to the Batch API regardless of rate limits — it's
faster, more reliable, and designed for the use case.

**Pre-warm the embedding cache before running the e2e sweep.**

The inconsistency between 52/60 (warm) and 41/60 (cold) makes the eval results
hard to interpret. A pre-warm step — run 20-30 common queries before the sweep —
would make results consistent across runs and remove the timeout confounding factor.

**Start LinkedIn posts earlier.**

Posts compound. Post #1 at Week 15 should have been at Week 10 or 11. The portfolio
story was ready by Week 12 (deployed system, eval results, clear narrative). Six more
weeks of consistent posting would have built a larger audience before the resume
outreach campaign.

---

## Month 4 Metrics

| Metric | Value |
|---|---|
| New projects shipped | 1 (mcp-dotnet-diagnostics) |
| MCP tools implemented | 7 |
| Tests (mcp-dotnet-diagnostics) | 34 (100% pass) |
| NuGet downloads | live |
| MCP registry submissions | 3 (Glama ✅, mcpservers.org ✅, awesome-mcp-servers pending) |
| LinkedIn posts written | 3 |
| LinkedIn posts published | 2 |
| Post #1 impressions | 429 |
| Post #1 reactions | 14 |
| Post #1 saves | 2 |
| Recipe corpus | 10k → 52k (+420%) |
| Embedding provider | Nomic → Voyage AI (voyage-4-lite, 1024d) |
| Re-embedding time (Batch API) | ~12 minutes for 52k recipes |
| RAGAS context_relevance (52k) | 0.578 (+0.054 vs spell_check baseline) |
| E2E pass rate (warm cache) | 52/60 (87%) |
| E2E pass rate (cold cache) | 41/60 (68%), 41/47 evaluated = 87% |
| IntentRouter items fixed | I-1, I-2, I-3, I-7, I-8, I-9, I-10 (7 items) |
| Tech debt items resolved (Month 4) | 8 |
| ADRs written | 1 (ADR-013) |
| Provider implementations | +1 (VoyageEmbeddingProvider) |
| Agent code changed for Voyage swap | 0 lines |
| Shared folder reorganized | Providers/, Guardrails/, Observability/ |

---

## Month 5 Plan

Month 5 is the job search activation month.

| Deliverable | Why | Week |
|---|---|---|
| Resume: ChefAgent + MCP projects section | Highest leverage addition — not on resume yet | 17 |
| Resume: summary rewrite | Lead with AI agent infrastructure, not healthcare | 17 |
| Resume: skills reorder + expand | AI & Agent Systems should be first, not last | 17 |
| Portfolio site scaffold | React + Tailwind, deploy to Vercel | 17-18 |
| Portfolio site: ChefAgent page | Technical depth, architecture diagram, eval results | 18 |
| Portfolio site: MCP page | Demo GIF, install command, tool descriptions | 18 |
| Post #3 publish + community seeding | MCP server on Anthropic Discord, r/dotnet | 16-17 |
| Target company list | 15-20 companies with AI agent / infrastructure roles | 19 |
| Outreach campaign | LinkedIn DMs to engineering managers | 19-20 |

**The story to tell:**
"Senior .NET engineer who spent 4 months building production AI agent infrastructure
from scratch — multi-agent orchestration, RAG pipelines, MCP servers, eval-driven
development, cloud deployment. Zero to deployed in 16 weeks."

---

## Interview Talking Points

**"What is mcp-dotnet-diagnostics and why did you build it?"**
It's a .NET global tool that exposes live runtime diagnostics to AI assistants through
the Model Context Protocol. Instead of Claude guessing at performance problems from
general knowledge, it calls real diagnostic tools — memory stats, GC events, thread
pool health — and gives grounded, data-driven analysis. I built it because ChefAgent
showed me the application layer of AI agents. The MCP server shows the infrastructure
layer: extending what AI assistants can do for other developers. The demo shows Claude
autonomously diagnosing 52% LOH fragmentation in a live .NET process without being
told which tools to call.

**"Three provider swaps, zero agent code changes — how?"**
`IEmbeddingProvider` and `ILlmProvider` are interfaces in `ChefAgent.Shared`. Every
agent depends on the interface, not the HTTP client. `ServiceRegistration.cs` reads
`EmbeddingProvider=voyage` from environment variables and constructs the right
implementation. The agents never know what they're talking to. HuggingFace → Nomic,
Ollama → Groq, Nomic → Voyage — three swaps, one file changed each time, zero agent
changes. That's what provider-agnostic actually means in practice.

**"Your eval results show 87% pass rate. What are the remaining 13%?"**
Two categories. First: 13 timeouts from Voyage's 3 RPM free tier — these aren't logic
failures, they're infrastructure. On a paid tier or with a warm embedding cache, they'd
mostly pass. Second: 6 genuine logic failures — one corpus gap (paleo queries return 0
results because the dataset has no paleo-tagged recipes), one known guardrail limitation
(repeated query detection needs session state), and four modify-meal-plan cascades from
setup timeouts. The logic the system does handle correctly is handling correctly.

**"What was the hardest technical problem in Month 4?"**
The .NET 10 EventPipe payload structure change in `mcp-dotnet-diagnostics`. All
EventCounter values returned zero despite a successful connection. The fix turned out
to be a one-level-deeper nested key — `PayloadValue(0)["Payload"]["Mean"]` instead
of `PayloadValue(0)["Mean"]` — but discovering that required building a debug tool
to inspect the raw payload at runtime. The change was undocumented. The lesson was
that when a library's output doesn't match its documentation, the fastest path to
understanding is often building a tool that dumps the raw output.

---

_Month 4 complete. Two projects live, two published posts, production fixed and
improved. Month 5 is the job search activation: resume, portfolio site, outreach._