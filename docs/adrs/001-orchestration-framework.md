# ADR-001: Orchestration Framework Selection

**Status:** Accepted
**Date:** 2026-05-23
**Decision Makers:** [Your Name]

## Context

ChefAgent requires a multi-agent orchestration framework to coordinate Recipe, Diet, and Planner agents. The framework must support:

- Agent-to-agent communication and function chaining
- Stateful workflows (meal planning requires multi-turn context)
- Tool calling with structured input/output
- Provider-agnostic LLM integration (Claude, Azure OpenAI)

Leading candidates: **Semantic Kernel (C#)** and **LangGraph (Python)**.

## Decision

Use **Semantic Kernel (C#) as the primary orchestration framework** for all agents and the main API. Implement **one agent (Diet Agent) additionally in LangGraph (Python)** as a secondary implementation to demonstrate bilingual fluency and framework comparison.

## Rationale

- **Semantic Kernel** aligns with existing .NET and Azure expertise, reducing ramp-up time.
- **C# primary** positions for enterprise AI roles where .NET is prevalent.
- **LangGraph secondary** demonstrates Python proficiency and awareness of the broader AI ecosystem.
- Dual implementation enables a concrete A/B comparison in the eval pipeline (Month 3), which is a strong portfolio talking point.

## Consequences

- Primary API and deployment pipeline target .NET 8 / ASP.NET Core.
- Python LangGraph agent runs as a sidecar service, called via HTTP from the C# orchestrator.
- Slightly more infra complexity (two runtimes), mitigated by Docker containerization.
- Evaluation harness must handle both implementations for fair comparison.

## Alternatives Considered

| Option | Pros | Cons |
|--------|------|------|
| LangGraph only (Python) | Larger community, more tutorials | Doesn't leverage .NET background |
| AutoGen | Multi-agent native | Less mature, API instability |
| CrewAI | Simple agent definition | Limited control over orchestration logic |
