# Week 17 Progress — Resume + Portfolio Site

**Month 5, Week 1. Packaging week.**

## Summary

Rebuilt the resume to lead with AI agent work while keeping the .NET/healthcare
background as credibility, not the headline — then built and deployed a full
4-page portfolio site from scratch. Both shipped live this week, ahead of the
original Days 6–7 polish schedule; most of that polish landed during Days 4–5
instead.

---

## Resume

**Status: Done, 1 page, deployed as `Resume_Aayush_Desai.pdf`.**

- Rewrote summary to lead with backend/.NET (5 years, healthcare) and mention
  AI agent work as a recent addition — not the other way around. First draft
  over-indexed on AI-engineer framing; corrected after review to keep the
  actual career weight accurate.
- Condensed Skills from 7 verbose lines to 4, AI & Agent Systems added as its
  own category. Added Python, Voyage AI, Groq, Ollama, Qdrant, RAGAS, Langfuse.
- Added a **Projects** section (new) between Skills and Experience — 3 bullets
  for ChefAgent, 1 for mcp-dotnet-diagnostics. Numbers included: 52k recipes,
  context relevance 0.470 → 0.578, 14,000ms → 651ms latency, 87% eval pass rate.
- Carenet section: kept all 9 original bullets unchanged at the person's
  request — Epic/Athena/Meditech by name, every impact number intact. Cut
  Softvan internship (2017–2018, no AI/agent signal).
- Bold-text audit: removed bold from third-party product/tool names (Langfuse,
  Epic/Athena/Meditech, AdvanceMD) that don't represent *my* achievement; added
  bold to actual outcomes (3 production provider swaps, 87%).
- AI-language audit: reworded several phrases that read as LLM-generated
  ("targeting AI infrastructure roles," "zero agent code changes," "autonomously
  diagnose," "60-case golden dataset") into how an engineer actually talks about
  the work.
- Fixed informal arrow (`Ruby→.NET`) to `Ruby-to-.NET` per feedback from an
  early reviewer (a friend, non-technical) who also flagged density — addressed
  by improving bold-text scannability rather than cutting content.

**Key learning:** the first AI-assisted draft swung too far toward "AI engineer"
framing and needed a deliberate correction back toward "backend engineer who's
also built this." Worth re-checking framing balance any time resume content
gets touched again.

---

## Portfolio Site

**Status: Live at `aayushmdesai.vercel.app`, all 4 pages built and deployed.**

Stack: React + Vite + Tailwind v4, React Router, deployed on Vercel from GitHub.

### Day 3 — Scaffold
- Vite + Tailwind v4 (plugin-based config, no separate `tailwind.config.js`)
- Deployed to Vercel immediately with placeholder content to prove the pipeline
- Hit a git auth issue (`Permission denied (publickey)` — remote was added via
  SSH URL without a registered key). Resolved by switching remote to HTTPS with
  a Personal Access Token.
- React Router set up with 4-page skeleton (Home, ChefAgent, MCP Server, About)
  plus shared `Layout` + `Nav` components

### Day 4 — ChefAgent page
- Built as a fully interactive page, not a static writeup:
  - **Architecture diagram**: custom SVG, 4 agent nodes, hover-to-reveal
    descriptions. Fixed an early aspect-ratio bug where the viewBox didn't
    match the actual node geometry, causing visible distortion/off-center
    rendering.
  - **Request lifecycle**: animated, auto-playing step-through with a progress
    rail; later upgraded to **link directly to the architecture diagram** —
    stepping through the request flow highlights the matching agent node(s)
    above it in real time.
  - **Product screenshot**: real screenshot of the ChefAgent chat UI, captioned
    to tie back to the "rules for the common case" key decision (the screenshot
    shows `SearchRecipe` / `rules-default` tags confirming no LLM call was needed).
  - **Guardrails & observability section**: 5-layer guardrail grid + Langfuse
    tracing explanation (added after a review pass identified the original
    page was missing any mention of guardrails despite it being a stated
    achievement).
  - **Eval results table**: shows the real progression including the semantic
    negation tradeoff (context relevance dipped, answer relevancy rose +0.112)
    and the Voyage AI migration's negation regression — both disclosed
    honestly as a deliberate tradeoff and documented tech debt, not hidden.

### Day 5 — MCP Server page + About page
- **MCP Server page**: hero, "what MCP is" explainer, demo GIF (Week 14 health
  check recording), full 7-tool table with "reach for it when" column, "how it
  works" section explaining the tool-description chaining technique, 3 key
  decisions (C# over TypeScript, target-by-PID, the .NET 10 EventPipe payload
  discovery), install instructions, and a directory-badge row linking to
  mcpservers.org, Glama, awesome-mcp-servers, and CodeGuilds.
- **About page**: short bio, current role focus, resume link, GitHub/LinkedIn.
  Iterated twice on layout — first pass was too cramped (small photo, narrow
  text column, lots of dead space) after comparing against two inspiration
  sites; rebuilt with a bigger heading, bigger circular photo (224px), and
  full-width use of the existing layout container.

### Bonus: Home page polish (pulled forward from Days 6–7)
Reviewed two portfolio sites for inspiration (Parinith Reddy's, Dipin Yadav's)
and adopted three elements, explicitly rejecting a fourth:
- **Icon-based social links** (GitHub/LinkedIn/Mail) replacing plain text —
  required installing `react-icons` after discovering `lucide-react` doesn't
  ship brand/logo icons (a real gap in that library, not a version issue)
- **Categorized tech stack grid** — 4 categories mirroring the resume's skill
  groupings exactly (AI & Agent Systems / Languages & Frameworks / Cloud &
  Infrastructure / Backend Engineering)
- **"Let's Connect" closing section** — heading, positioning line, 3 labeled
  contact buttons
- **Rejected**: light/dark mode toggle — assessed as low-signal frontend
  polish disconnected from the backend/AI-infra story being told. Explicitly
  deferred, not forgotten — revisit only if time remains after everything else.

---

## Engagement (final numbers, all 3 posts)

| Post | Date | Impressions | Members Reached | Reactions | Comments | Saves | Link Clicks |
|------|------|-------------|------------------|-----------|----------|-------|-------------|
| #1 (ChefAgent architecture) | 6/10 | 636 | 402 | 16 | 0 | 2 | 2 |
| #2 (RAG eval deep dive) | 6/12 | 455 | 255 | 10 | 1 | 0 | 3 |
| #3 (MCP server diagnostics) | 6/16 | 534 | 314 | 12 | 0 | 1 | 0 |

Post #1's previously recorded same-day numbers (429 impressions / 14 reactions,
captured in the Week 16 doc) were early reads before the post finished
circulating — final numbers came in higher across the board. Post #1 has the
strongest overall reach of the three. Post #3 shows 0 link clicks because that
post didn't include a GitHub link (unlike #1 and #2), not because of weaker
engagement — its impressions and reactions are comparable to #1.

---

## Tech debt / open items

- **Profile photo**: placeholder swapped for real photo mid-week; final crop
  confirmed circular, 224px, good framing
- **Mobile testing**: not yet done — the ChefAgent architecture diagram and
  the new Home page tech-stack grid are the highest-risk components at
  narrow widths and should be checked first
- **Distribution**: portfolio URL not yet added to resume header, LinkedIn
  Featured/About sections, or a GitHub profile README
- **Full proofread pass**: not yet done across all 4 pages
- **Dark mode toggle**: deliberately deferred, lowest priority

---

## Key learnings

**AI-assisted resume drafts need a framing check, not just a content check.**
The first full resume rewrite was technically accurate but over-corrected
toward "AI engineer" positioning that didn't match 5 years of actual backend
experience. Caught on review, fixed by deliberately re-weighting the summary
and trimming project-section bullet counts rather than expanding them.

**Honest disclosure of tradeoffs reads as more senior than a clean upward
graph.** The ChefAgent eval table's semantic-negation dip and the Voyage AI
negation regression were kept in, explained, and framed as deliberate
tradeoffs and documented tech debt — this was a deliberate choice over
showing only the metrics that trend favorably.

**Library assumptions need verification, not just installation.** Assumed
`lucide-react` included brand/logo icons (a common but incorrect assumption
echoed in several tutorials); it doesn't, by design. Caught only when the
build threw an import error — worth checking a library's actual export list
before writing code against it next time, especially for icon/logo needs.

**Small UI comparisons are worth doing even late in the process.** The About
page redesign only happened because two inspiration sites were reviewed
side-by-side against the current build — the gap (small photo, narrow text,
wasted space) was invisible until placed next to a reference that used the
same width intentionally.