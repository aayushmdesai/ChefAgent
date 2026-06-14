# Week 15 Progress — LinkedIn Posts + Dataset Expansion

**Month 4, Week 15 | Dates: June 2026**  
**Repos: ChefAgent + mcp-dotnet-diagnostics**

---

## Goals

- Post #1 outline, visual, and draft ✅
- LinkedIn profile updated (headline + projects) ✅
- Recipe dataset expanded from 10k → 52k ✅
- Docker Compose merge bug fixed ✅
- Post #1 published ✅
- Post #2 outline, visuals, and draft ✅
- Post #3 outline and draft (upcoming)

---

## The Theme: Shipped Code → Job Opportunities

Both projects are live. Week 15 shifts to a completely different skill: writing about what
you built. GitHub repos prove you can build. LinkedIn posts prove you can think and
communicate about what you build. The combination — post drives traffic to repo, repo
validates the post — is the compounding engine for the job search.

The discomfort is real. For 14 weeks, every day had a clear output: a file, a passing test,
a deployed service. Writing is different. The feedback is slower, the "done" criteria is
fuzzier, and it requires visibility in a way code doesn't.

---

## Day 1 — Post #1 Outline + Visual ✅

### What Was Decided

Post #1 is the ChefAgent story. Two core insights locked in:

- **The routing broke, not the agents** — 50-scenario e2e sweep, 6 failures, every single
  one from the IntentRouter. Not the Recipe Agent, not the Diet Agent, not the Planner.
- **You didn't know your RAG was broken until you measured it** — context relevance
  baseline 0.470. Nearly half of retrieved context was irrelevant. No errors, no warnings.
  Just quietly wrong.

These are the two things nobody warns you about when building multi-agent systems. That's
the story.

### Visual

Built a ByteByteGo-style architecture diagram: dark background (#13161f), bold colored
components (teal agents, red failure points, purple infrastructure, amber observability),
two red callouts (6/50 failures, 0.470 baseline). Exported to PNG for LinkedIn attachment.

The image headline reads "I built a multi-agent system. Here's what actually broke." — so
the post starts mid-story, assuming the image has already set the scene.

### Post Structure Decisions

- Image does the setup, post does the story
- No bullet points, no bold text, no headers — prose only
- Every claim backed by a specific number
- Technical jargon translated for non-engineers without dumbing it down
- Ends with repo link + job search signal
- Blog/deep technical writeup deferred to Month 5 portfolio site

---

## Day 2 — Post #1 Written and Locked ✅

### Final Post

```
Ran 50 tests before shipping. All 6 failures came from the same place — the router
deciding which agent handles your message. Not the agents. The layer connecting them.

Then I measured whether the system was actually finding the right information.
Score: 0.470 out of 1. Nearly half was irrelevant. No errors. No warnings. Just
quietly wrong the whole time.

Both got fixed. The router now uses pure signal-word classification — zero LLM calls.
The retrieval improved to 0.524 after adding spell correction and semantic negation
handling. Small changes, measurable difference.

Repo is public if you want to dig into the architecture at github.com/[username]/ChefAgent.
I'm looking for AI agent infrastructure roles — if you're working on orchestration,
routing, or eval pipelines, I'd genuinely like to compare notes. DM me.
```

### Published — Results (24hrs)

- Impressions: 429
- Member reach: 278
- Reactions: 14
- Saves: 2

Saves are the strongest signal — someone wanted to come back to it. No DMs or comments
on first post. Post #2 builds on this — consistency compounds.

### Visual

Architecture diagram saved as `docs/linkedin/chefagent_diagram.png` — ByteByteGo-style
dark background, teal agents, red failure callouts (6/50 failures, 0.470 baseline).

### Writing Principles Established

- Start mid-story (image already set the scene — don't repeat it)
- Short sentences, human voice, not AI-written
- "Just quietly wrong the whole time" — best line, don't touch it
- Separate social proof from job signal in the CTA
- "Let's talk" replaced with something specific about what you'd talk about

### LinkedIn Profile Updated

- Headline: "Software Engineer · AI Agent Infrastructure · Building multi-agent systems · Open to roles"
- ChefAgent added as LinkedIn project with architecture diagram PNG
- mcp-dotnet-diagnostics added as LinkedIn project with NuGet install command

---

## Bonus Work — Recipe Dataset Expansion ✅

Week 15 had a diversion into a meaningful ChefAgent improvement: expanding the recipe
dataset from 10k → 52k.

### What Changed

**Two datasets combined:**
- `corbt/all-recipes` — 536k total rows, pulled ~90k to get ~46k after dedup
- `Anupam007/indian-recipe-dataset` — 5,938 Indian recipes with clean structured fields

**New `cuisine` payload field** added to every Qdrant point: `"indian"` or `"western"`.
Available as a filter for future use — zero breaking change to existing agent code.

**Final count:** 52,155 unique recipes (5,900 Indian + ~46,255 Western)

### New Scripts

`scripts/pipeline/prepare_recipes_2.py` — unified loader:
- Two parsers: corbt uses regex on the single `input` field; Indian dataset uses clean
  structured fields (`TranslatedRecipeName`, `TranslatedIngredients`, `TranslatedInstructions`)
- Deduplication by MD5 title hash
- `--corbt-limit` and `--indian-limit` flags with sensible defaults

`scripts/pipeline/generate_embeddings_nomic.py` — Nomic Atlas API batch embedder:
- `search_document:` prefix (mirrors `NomicEmbeddingProvider.cs` exactly)
- `--resume` flag for interrupted runs (reads already-embedded IDs from output file)
- Retry logic on 429 and 5xx

`scripts/pipeline/load_qdrant.py` — bug fix:
- Replaced `urlparse` host/port extraction with `url=` direct parameter
- Old approach was sending requests to port 6334 (gRPC) instead of 6333 (REST),
  causing empty JSON responses

### Embedding Pipeline

Embeddings generated in Google Colab (T4 GPU) using `sentence-transformers` with
`nomic-ai/nomic-embed-text-v1.5` — same model as production, same `search_document:`
prefix, same 768 dimensions. Zero vector space mismatch.

Nomic Atlas API free tier (10M tokens) exhausted mid-run. The 52k dataset was embedded
using the local model in Colab instead. 

### Known Issue — Nomic Token Exhaustion

Production query-time embeddings are failing with HTTP 400 — Nomic Atlas API free tier
exhausted. System is currently down for recipe search.

**Fix path:** Migrate to Voyage AI (`voyage-4-lite`, 200M free tokens, $0.02/MTok after).
Requires:
1. New `VoyageEmbeddingProvider.cs` (~30 lines, identical structure to Nomic)
2. Re-embed 52k recipes in Colab with voyage model
3. Reload Qdrant
4. Swap `EmbeddingProvider=voyage` in Railway env vars

Deferred — will address in Week 16 before portfolio site work begins.

### Commit

```
feat(pipeline): expand recipe dataset to 52k with Indian recipes

- prepare_recipes_2.py: unified loader combining corbt/all-recipes
  (Western) and Anupam007/indian-recipe-dataset (Indian, 5938 recipes)
  with dedup by title hash and cuisine field added to payload
- generate_embeddings_nomic.py: Nomic Atlas API batch embedder with
  resume support for interrupted runs
- load_qdrant.py: fix client init to use url= directly instead of
  urlparse (was causing empty response on port 6333)
```

---

## Deferred

- Post #2 (RAG eval story) — in progress, hook locked
- Post #3 (MCP server) — Week 16
- Voyage AI migration — Week 16, before portfolio site
- `GeneralQuestion` statelessness (loses context across turns)
- Intent classifier gaps (ValidateDiet question-form, CreateMealPlan phrasing variants)
- `_ollamaUrl` → `_llmUrl` naming cleanup

---

## Key Learnings This Week

**Writing about your work is a skill separate from doing the work.** Most engineers
underinvest in it. A strong LinkedIn post reaches 50-100x more people than a GitHub repo
alone. The combination — post drives traffic to repo, repo validates the post — is the
compounding engine for a job search.

**The posts aren't about impressing people with complexity.** They're about showing clear
thinking, honest tradeoffs, and real data. That's what hiring managers look for.

**Translate, don't dumb down.** "Intent router" became "the component that reads a message
and decides which agent handles it." A PM gets it. A VP gets it. An engineer still
recognizes exactly what you're talking about.

**Dedup by title hash cuts deep.** corbt/all-recipes has significant title repetition
internally. Pulling 90k rows only yielded ~46k unique recipes after dedup — expected and
correct behavior, not a bug.

---

## Post #2 — RAG Eval Story ✅

### Final Post

```
One query broke my entire dietary filter after I thought I'd fixed it.

"Nut-free cookies."

Nut-free isn't just "no nuts." It's no almonds, no walnuts, no pecans, no peanut butter.
The system passed 6 out of 7 tests. I almost shipped without running the 7th.

A friend asked why I was even measuring this. "Isn't the LLM supposed to handle all that?"
Kind of. But nearly half of what my system was retrieving had nothing to do with what the
user asked. No errors. No warnings. The LLM had no idea.

The standard eval framework crashed before giving me a single number. Built my own instead
— three inputs, one score, no dependencies. Simpler, but it ran.

Measuring is harder than building. And more honest.

Code is on GitHub at https://lnkd.in/g-zGNwdV if you want to dig into the architecture.
I'm looking for AI agent infrastructure roles — if you're working on orchestration, routing,
or eval pipelines, I'd genuinely like to compare notes. DM me.
```

### Visuals

Two PNGs as LinkedIn carousel:

**Image 1 — Eval pipeline** (`docs/linkedin/post2_image1_pipeline.png`)
- Left column: RAGAS attempted → dependency conflicts → executor crashed → abandoned →
  custom scorer built instead
- Right column: pipeline that ran — fetch contexts locally → score with LLM judge (GPU)
  → track improvements
- Bottom: 100 queries · 12 categories · 3 metrics per query

**Image 2 — Experiment numbers** (`docs/linkedin/post2_image2_numbers.png`)
- Experiment table: baseline → spell check → negation fix
- Context relevance: 0.470 → 0.524 (+0.054) → 0.482 (tradeoff)
- Allergy search quality: 0.213 → 0.234 → 0.325 (+0.112)
- Nut-free cookies failure callout — 6/7 passed, this one didn't
- Fix path: push negation into Qdrant at query time, not after retrieval

### Writing Decisions

- Lead with the nut-free failure moment — most concrete, most interesting
- Friend dialogue as mid-post context, not opening setup
- RAGAS crash explained in one sentence with resolution
- "Measuring is harder than building. And more honest." — thesis line, stays last
- Line break between repo link and job signal in CTA
- "Just quietly wrong" removed — was reused from Post #1
- Images cover the technical depth — post stays human throughout

---

## Post #3 — MCP Server Story ✅

### Final Post

```
I recently asked Claude why my .NET app was slow. Without tools, it would have guessed.

With the right setup, it looked.

One prompt. Claude fired all seven diagnostic tools autonomously, read the actual numbers,
and flagged 52% LOH fragmentation. No hallucination. No guessing. Real diagnostics.

I built the MCP server behind this — seven .NET runtime tools, no code changes to your app.

Published on NuGet. Approved on Glama. Submitted to awesome-mcp-servers.

dotnet tool install -g mcp-dotnet-diagnostics

github.com/aayushmdesai14/mcp-dotnet-diagnostics

Looking for AI infrastructure and Software Development roles — DM me.
```

### Visual

Demo GIF from Claude Desktop session (`docs/assets/demo.gif`):
- Prompt: "I have a .NET app running. Can you do a full health check on PID 36226?"
- Claude autonomously fires all 7 diagnostic tools in parallel
- Dashboard renders: CPU 0.75%, Working set 45MB, LOH 21MB, GC fragmentation 52%
- "Needs attention" badge in red — the money shot

No second image needed. GIF tells the full story.

### Writing Decisions

- Hook: "Without tools, it would have guessed. With the right setup, it looked." — the
  core distinction between hallucination and real diagnostics
- Audience: software engineers specifically — Post 3 doesn't need to be for everyone
- No description of what the GIF shows — GIF handles it
- Three registry mentions (NuGet, Glama, awesome-mcp-servers) signal credibility without
  bragging
- Install command as its own line — developer CTA
- Job signal broadened: "AI infrastructure and Software Development roles"
- LinkedIn's AI rewrite rejected — voice restored to match Posts 1 and 2

### Publishing Schedule

- Post 1: published ✅
- Post 2: scheduled — in 2 days
- Post 3: scheduled — 4-5 days from now

---

## Goals — Final Status

- Post #1 outline, visual, and draft ✅
- Post #1 published ✅ (429 impressions, 278 reach, 14 reactions, 2 saves)
- Post #2 outline, visuals, and draft ✅ (ready to publish)
- Post #3 outline and draft ✅ (ready to publish)
- LinkedIn profile updated (headline + projects) ✅
- Recipe dataset expanded from 10k → 52k ✅
- Docker Compose merge bug fixed ✅