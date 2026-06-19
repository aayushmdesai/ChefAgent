# Week 17 Progress — Resume + Portfolio Site

**Month 5, Week 1. Packaging week.**

## Summary

Rebuilt the resume to lead with AI agent work while keeping the .NET/healthcare
background as credibility, not the headline — then built and deployed a portfolio
site from scratch, starting as a 4-page React Router SPA and evolving into a
single scrollable page with anchor navigation, an Experience section with honest
narrative framing, a redesigned Hero, hamburger mobile nav, Open Graph meta tags,
custom favicon, and copy-to-clipboard on code blocks. All shipped live at
aayushmdesai.vercel.app.

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

**Status: Live at `aayushmdesai.vercel.app`, fully restructured as a single-page scrollable site.**

Stack: React + Vite + Tailwind v4, no React Router (anchor-based navigation), deployed on Vercel from GitHub.

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
  - **Request lifecycle**: animated, auto-playing vertical timeline (dots +
    connector line) placed side-by-side with the architecture diagram.
    Stepping through highlights the matching agent node(s) in real time;
    hovering a node pauses the animation.
  - **Product screenshot**: real screenshot of the ChefAgent chat UI, captioned
    to tie back to the "rules for the common case" key decision.
  - **Guardrails & observability section**: 5-layer guardrail grid + Langfuse
    tracing explanation.
  - **Eval results table**: shows the real progression including the semantic
    negation tradeoff and Voyage AI migration regression — both disclosed
    honestly, not hidden.

### Day 5 — MCP Server page + Connect section
- **MCP Server page**: hero, "what MCP is" explainer, demo GIF, full 7-tool
  table, "how it works" section, 3 key decisions, install instructions,
  copy-to-clipboard on code blocks, directory badges (mcpservers.org, Glama,
  awesome-mcp-servers, CodeGuilds).
- **About page**: initially built as separate, later merged into the
  single-page layout as a "Connect" closing section.

### Days 6–7 — Single-page restructure + polish

**Major architectural decision:** converted from 4-page React Router SPA to a
single scrollable page with anchor-based navigation and `IntersectionObserver`
active-section highlighting — inspired by reviewing Parinith Reddy's and Dipin
Yadav's portfolios side-by-side. React Router removed entirely.

**New section order:** Home → Experience → ChefAgent → MCP Server → Connect

**File structure:** `src/sections/` (Hero, ChefAgentSection, McpSection,
ExperienceSection, ConnectSection) + `src/components/` (Nav, Layout, Footer)

**New: ExperienceSection** — built from scratch via targeted Q&A to get accurate
ownership claims. 3 expanded narrative items (IQ-Prompt Integration, EHR
scheduling sync, Patient Intake Portal) with honest problem → approach → outcome
framing. Remaining 6 Carenet items behind "Show all experience" toggle.

**Hero redesign:**
- Photo left, bio right (Dipin-style layout)
- "Open to opportunities" green pulsing badge
- "Dallas, TX · Open to relocation" location line
- 3 scrollable cards with section labels: Experience first, then Personal Projects
- Tech stack expanded to 5 categories, full-width stacked bordered cards
- Skills updated: Data & Persistence and Backend & Observability added as
  separate categories; LINQ, SQL Server, MySQL, Serilog, Hangfire, xUnit,
  Upstash, Qdrant Cloud added

**Nav:**
- Sticky with `IntersectionObserver` active highlighting
  (`rootMargin: '-10% 0px -85% 0px'` — tuned for long sections like MCP)
- Smooth scroll with runtime nav-height offset
- "AD" logo replacing full name
- Hamburger menu on mobile (animated ≡ → × transition, dropdown with all links)

**Other polish:**
- `index.html`: tab title, meta description, Open Graph tags, Twitter card,
  `og:image` for LinkedIn/Slack link previews
- Favicon: custom dark circle SVG with "AD" initials
- Copy-to-clipboard on MCP install command and JSON config
- All external links open in new tab
- `overflow-x-hidden` on Layout for mobile
- README with homepage screenshot added to repo

---

## Engagement (as of Week 17)

| Post | Date | Impressions | Members Reached | Reactions | Comments | Saves | Link Clicks |
|------|------|-------------|------------------|-----------|----------|-------|-------------|
| #1 (ChefAgent architecture) | 6/10 | 636 | 402 | 16 | 0 | 2 | 2 |
| #2 (RAG eval deep dive) | 6/12 | 455 | 255 | 10 | 1 | 0 | 3 |
| #3 (MCP server diagnostics) | 6/16 | 534 | 314 | 12 | 0 | 1 | 0 |
| #4 (Portfolio site launch) | 6/18 | scheduled | — | — | — | — | — |

Post #4 is a portfolio announcement — genuine, grounded tone ("nothing fancy,
just the real work documented honestly"). Attached a screen recording of the
site in motion rather than a static screenshot. Numbers to be pulled next week.

---

## Tech debt / open items

- **Full proofread pass**: not yet done — do before outreach phase
- **Dark mode toggle**: deliberately deferred, lowest priority
- **MCP tools table mobile**: 3-column table is hard to read on narrow screens;
  left as-is by choice

## Distribution — completed

- ✅ Portfolio URL added to resume header (Dallas, TX · aayushmdesai.vercel.app on line 1)
- ✅ LinkedIn Featured section — portfolio link with photo thumbnail and description
- ✅ LinkedIn About section — updated with ChefAgent/MCP specifics, portfolio URL, honest framing
- ✅ LinkedIn Contact info — website field updated
- ✅ Resume PDF uploaded to portfolio site (serves as the "View résumé" download)
- ✅ Post #4 scheduled — portfolio announcement

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

**Single-page vs multi-page is a real tradeoff, not a style preference.**
Started with 4 separate routes, switched to single-page after seeing how
inspection sites like Dipin's felt more fluid and less like clicking through
documentation. The tradeoff: deep-link URLs like `/chefagent` are gone, but
the scrollable arc (identity → work → projects → connect) tells a better story
than isolated pages with no flow between them.

**`IntersectionObserver` rootMargin needs tuning per content length.** The
default aggressive margin (`-20% 0px -55% 0px`) worked for short sections but
caused the active nav link to jump ahead on long sections like MCP. Widening
to `-10% 0px -85% 0px` kept long sections active while you read them.

**Ownership framing in experience narratives matters.** The first draft of the
ExperienceSection overclaimed on IQ-Prompt (said "designed" when it was
"implemented against an architecture"). Corrected via Q&A before writing —
a reminder that honest framing is more credible than inflated claims, and a
hiring manager who digs in will catch the difference.