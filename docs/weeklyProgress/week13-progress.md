# Week 13 Progress — Transition to MCP Server

**Month 4, Week 13 | Dates: June 2026**  
**Tag: v1.0.0 (no new tag this week)**

---

## Goals

- No new ChefAgent features this week ✅ (intentional)
- Begin `mcp-dotnet-diagnostics` — new standalone MCP server project ✅
- Document ChefAgent deferred items for future reference ✅

---

## Status

ChefAgent is at **v1.0.0** — fully deployed and publicly live:

- **Frontend:** `https://chefagent.vercel.app`
- **API:** `https://chefagent-production.up.railway.app`

No code changes this week. The focus shifted entirely to the MCP server project.

---

## The Portfolio Shift

ChefAgent demonstrated the ability to build AI applications — consuming LLMs,
orchestrating agents, building RAG pipelines, deploying to cloud.

Month 4 shifts to a different layer: **AI infrastructure**. The MCP server project
(`mcp-dotnet-diagnostics`) is a tool that extends what AI assistants can do — not
an application that uses AI.

The two projects together tell a complete story:

| Project | Role | What It Shows |
|---------|------|---------------|
| ChefAgent | AI application | Orchestration, RAG, memory, guardrails, eval, observability |
| mcp-dotnet-diagnostics | AI infrastructure | MCP protocol, tool design, .NET runtime internals |

---

## ChefAgent Deferred Items (Backlog)

These were explicitly deferred during Months 1–3. Not forgotten — candidates for
future improvement if time allows after Month 5.

| Item | Context | Deferred Since |
|------|---------|----------------|
| `GeneralQuestion` statelessness | Loses context across turns — "how to make it" loses reference | Week 12 |
| IntentRouter vocabulary gaps | `ValidateDiet` question-form, `CreateMealPlan` phrasing variants | Week 8 |
| Nut-free retrieval depth | Nut-free filter reduces candidate pool too aggressively | Week 5 |
| Static prompt building in `RecipeReranker` | Should use SK prompt templates | Week 6 |
| Upstash cold start pre-warm | ~3,000ms first Redis call per session | Week 12 |
| `ILlmProvider` wiring: `IntentRouter`, `QueryPreprocessor`, `RecipeReranker` | Currently skip provider abstraction | Week 12 |
| RecipeNLG dataset | Larger, richer dataset — gated on HuggingFace, deferred | Week 1 |

---

## Month 4 Plan

| Week | Focus |
|------|-------|
| Week 13 (this week) | MCP server scaffold, 7 tools, Claude Desktop integration, 34 tests |
| Week 14 | CI pipeline, README, open-source release |
| Remaining | LinkedIn posts documenting the full build journey |

---

## Key Links

- ChefAgent live: `https://chefagent.vercel.app`
- MCP server repo: `https://github.com/aayushmdesai/mcp-dotnet-diagnostics`
- Week 13 MCP progress: see `mcp-dotnet-diagnostics/week13-progress.md`