# ADR-010: Observability Architecture

**Status:** Accepted  
**Date:** June 2026 (Week 10, Month 3)  
**Decision Makers:** Aayush Desai

---

## Context

After Week 9, ChefAgent has three sources of runtime information:

- **Structured logs** — component-level events (`RecipeSearch completed in 2.3s`)
- **Audit log** (Week 7) — guardrail events: injections blocked, rate limits, confidence flags
- **Profiling data** (Week 8) — aggregate latency per component across many requests

These are sufficient for answering "what happened in aggregate." They are not sufficient for answering "why was this specific request slow?"

A meal plan request triggers 7 Recipe Agent calls and 7 Diet Agent calls — 14 sequential operations. With 14 log lines you know what happened and roughly when. You cannot answer:

- Which of the 7 recipe searches was the slow one?
- Was it slow at embedding, at Qdrant, or at diet validation?
- Which specific operation inside that slow search was the bottleneck?
- Did the circuit breaker state affect this specific request?

These questions require **distributed tracing** — a tree of timed spans that captures the full parent-child relationship of operations within a single request.

Three decisions are documented here:

1. Which observability tool to use (Langfuse vs alternatives)
2. Which deployment mode to use (Cloud vs self-hosted v3 vs self-hosted v2)
3. How to propagate trace context through the call chain (injection vs parameter passing)

---

## Decision 1 — Observability Tool: Langfuse

**Decision:** Use Langfuse for distributed tracing, LLM generation logging, and metrics.

### Alternatives considered

#### Option A — Langfuse ✅ Chosen

Open-source LLM observability platform. Provides traces, spans, LLM generation logging (prompt, completion, token count, latency), and a built-in UI. Self-hostable. Purpose-built for LLM applications.

#### Option B — OpenTelemetry + Jaeger (generic APM)

OpenTelemetry is the industry standard for distributed tracing in general-purpose applications. Jaeger is a common open-source backend.

**Why rejected:**
- Generic APM tools have no concept of an "LLM generation." You can trace that Ollama was called and how long it took, but you cannot see the input prompt, output, and token count without custom attribute schemas.
- Requires building and maintaining a custom semantic layer for LLM-specific concerns.
- Significantly more setup overhead for no gain in this context.
- LLM-specific dashboards (token usage, prompt quality) would need to be built from scratch.

Note: Under the hood, Langfuse's trace model maps directly to OpenTelemetry concepts — a Langfuse `Trace` is an OTel `Trace`, a `Span` is an OTel `Span`. Switching to OTel later is feasible.

#### Option C — LangSmith (LangChain's observability platform)

Purpose-built for LLM observability, strong UI, well-integrated with LangChain.

**Why rejected:**
- **Not open-source.** Self-hosting requires an Enterprise license.
- ChefAgent's entire stack is self-hosted open-source. LangSmith breaks that constraint.
- Tightly coupled to the LangChain ecosystem. ChefAgent uses Semantic Kernel (.NET), not LangChain.

#### Option D — Arize Phoenix

Open-source LLM observability, OpenTelemetry-native.

**Why rejected:**
- Smaller ecosystem and community than Langfuse at time of decision.
- Less mature .NET integration — Langfuse's REST API is language-agnostic and well-documented.
- Langfuse is more widely referenced in AI engineering job descriptions and interviews.

### Why Langfuse wins

- Open-source and self-hostable — consistent with the project's zero-paid-services constraint
- Purpose-built for LLM tracing — first-class support for prompt, completion, token count, model, latency per generation
- REST API is language-agnostic — works from C#/.NET without waiting for an official SDK
- Strong portfolio signal — Langfuse is frequently cited in AI engineering roles and recognizable to interviewers

---

## Decision 2 — Deployment Mode: Self-Hosted v2

**Decision:** Self-host Langfuse v2 via Docker Compose, integrated into the existing `docker-compose.yml`.

### Three deployment options evaluated

#### Option A — Langfuse Cloud (free tier)

Langfuse offers a managed cloud version with 50,000 observations/month free.

**What it gives you:**
- Zero infrastructure burden — no extra Docker containers
- No RAM overhead on development machine
- Sign up at `cloud.langfuse.com`, get API keys, done
- Identical observability code — only the endpoint URL differs from self-hosted

**Why not chosen for this project:**
- Data leaves the machine — traces contain LLM input/output, query text, user session metadata
- Breaks the "fully self-hosted" portfolio story: Qdrant, Redis, Ollama are all local; Langfuse Cloud would be the only external dependency
- 50K observations/month is plenty for development but requires account management

**When to choose cloud:** Any production system where infrastructure cost matters more than data sovereignty, or where the team is small and ops burden is a real constraint. For a real startup shipping fast, cloud free tier is the right call.

#### Option B — Langfuse v3 Self-Hosted

Langfuse v3 is the current actively-developed version. It introduces a significantly richer architecture:

| Component | Purpose |
|-----------|---------|
| `langfuse-web` | UI + REST API |
| `langfuse-worker` | Async event processor |
| `postgres` | Transactional metadata store |
| `clickhouse` | High-performance OLAP store for traces and observations |
| `minio` | Blob storage for multimodal traces |

**Why not chosen:**
- 5 additional Docker containers on a machine already running Qdrant + Redis
- Estimated additional RAM: 1.2–1.5GB
- Combined with Ollama native (~4–5GB) and macOS overhead (~1.5GB), total approaches 8GB ceiling
- ClickHouse and MinIO add operational complexity with no benefit at development scale
- v3 is the right choice for a production VM or cloud deployment — not an 8GB MacBook

**When to choose v3:** Cloud VM with 16GB+ RAM, or any production deployment. The ClickHouse backend enables much faster trace queries at scale. MinIO enables multimodal trace storage (images, audio). For development and portfolio purposes, this is overkill.

#### Option C — Langfuse v2 Self-Hosted ✅ Chosen

Langfuse v2 requires only two additional containers:

| Component | Purpose | Estimated RAM |
|-----------|---------|---------------|
| `postgres` | All state — traces, spans, generations, users | ~150–200MB |
| `langfuse-server` | UI + REST API | ~400–500MB |

**Total additional overhead: ~650MB** — feasible on 8GB with Ollama running natively (not in Docker).

**Trade-offs accepted:**
- v2 is in security-update-only mode as of Q1 2025 — no new features
- All observability features needed for this project (traces, spans, generations, dashboard) are fully present in v2
- Upgrading to v3 later is documented in Langfuse's official upgrade guide — not a dead end

**RAM budget with v2:**

| Service | Estimated RAM |
|---------|--------------|
| Ollama (native, llama3.2) | ~4,000–5,000MB |
| macOS + terminal overhead | ~1,500MB |
| Qdrant (Docker) | ~200MB |
| Redis (Docker) | ~50MB |
| Postgres (Docker) | ~175MB |
| Langfuse server (Docker) | ~450MB |
| **Total** | **~6,375–7,375MB** |

Feasible on 8GB. Tight if Ollama is actively inferencing — start Docker services first, let them settle, then load Ollama model.

---

## Decision 3 — Trace Context Propagation: Parameter Passing

**Decision:** Pass a lightweight `TraceContext` record through method parameters. Do not inject `ITracingService` into business logic classes.

### The constraint that drives this decision

**Tracing must never break the request.**

If Langfuse is unreachable, if the network call fails, if there is a serialization bug in a span payload — the user's recipe search must still succeed. Observability is infrastructure for operators, not a feature for users.

### Two approaches

#### Option A — Inject `ITracingService` everywhere

Every class that creates a span takes `ITracingService` in its constructor: `QdrantService`, `RulesEngine`, `OllamaService`, `QueryPreprocessor`.

**Problem:** Tracing is now a hard dependency of business logic. If `ITracingService.StartSpan()` throws — due to a Langfuse client bug, network issue, or serialization error — the exception propagates through the call stack and can kill the request. Recipe search fails because Langfuse is unreachable. This is backwards.

Secondary problem: every class now carries a tracing dependency that has nothing to do with its responsibility. `QdrantService` exists to query vectors — it should not know about observability infrastructure.

#### Option B — Pass `TraceContext` through parameters ✅ Chosen

`ChatController` creates a `TraceContext` (a plain record: `traceId`, `spanId`). It passes it to `AgentOrchestrator.HandleAsync(request, ctx)`. The Orchestrator passes it to `RecipeSearchPlugin.SearchAsync(query, ctx)`. Plugins pass it to sub-components.

The **only** class that knows Langfuse exists is `Tracing.cs`. Every outbound Langfuse call in `Tracing.cs` is wrapped in `try/catch` and executed as fire-and-forget via `Channel<T>`. A background worker flushes the channel every 5 seconds.

**Result:**
- Business logic classes are completely decoupled from the tracing backend
- A Langfuse outage produces silent no-ops, not request failures
- `TraceContext` is a plain record — no dependencies, trivially testable
- Switching from Langfuse to any other backend (OpenTelemetry, Datadog) requires changing only `Tracing.cs`

This pattern is the same model used by OpenTelemetry's `Activity` propagation — pass context through, keep the backend concern at the boundary.

### Fire-and-forget design

```
Request thread                    Background worker
─────────────────                 ─────────────────
StartSpan() called
  → write to Channel<T>           ← reads from channel
  → returns immediately           → POST to Langfuse REST API
                                  → retry on failure (max 3)
                                  → drop on persistent failure
```

Tracing overhead on the request thread: < 1ms (channel write). Langfuse network latency is invisible to the user.

---

## Consequences

**Positive:**
- Every `/chat` request produces a full trace tree in Langfuse — visible latency breakdown per component, per agent call, per LLM generation
- Plan generation traces show all 14+ agent calls organized by day/slot — the most interview-compelling trace in the system
- Correlation IDs link all log messages to their trace — log archaeology is replaced by trace lookup
- The `TraceContext` propagation pattern is directly transferable to OpenTelemetry in production
- Self-hosted keeps the zero-paid-services constraint intact

**Negative:**
- v2 is in maintenance mode — no new Langfuse features without upgrading to v3
- ~650MB additional Docker RAM — machine must be managed carefully when all services run simultaneously
- Fire-and-forget means trace data can be lost if the process crashes mid-flush — acceptable for development observability, not for production audit logging (the Week 7 audit log handles that case)

**Neutral:**
- Langfuse tracing is toggled via `appsettings.json: "Langfuse": { "Enabled": true }` — can be disabled without redeploying
- The endpoint URL is the only config change needed to switch from self-hosted v2 → v3 → cloud

---

## Related ADRs

- ADR-001: Orchestration Framework (Semantic Kernel)
- ADR-002: Vector Database (Qdrant)
- ADR-003: LLM Provider (Ollama)
- ADR-007: Guardrails Architecture (InputGuard, audit log)
- ADR-008: Circuit Breaker Pattern (Ollama + Redis resilience)
- ADR-009: Evaluation Pipeline (RAGAS, golden dataset)