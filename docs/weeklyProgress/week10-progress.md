# Week 10 Progress — Langfuse Observability

**Month 3, Week 10 | Dates: June 2026**  
**Tag: v0.7.0**

---

## Goals

- Self-host Langfuse v2 for distributed tracing
- Instrument every agent call with traces and spans
- Track LLM generations (prompt, completion, token estimate, latency)
- Add correlation IDs across all log lines
- Build `/admin/metrics` aggregate endpoint
- Document observability architecture in ADR-010

---

## Education: Logs vs Traces

**The core question:** with 14 log lines from a meal plan request (7 recipe searches + 7 diet validations), what can you NOT answer?

- Which of the 7 recipe searches was slow?
- Was it slow at embedding, Qdrant, or diet validation?
- Which specific operation inside that slow search was the bottleneck?

A log line is flat and disconnected. A span has two things a log doesn't:

1. **Parent-child relationship** — it knows who called it and what it called. The tree structure is baked in, not reconstructed manually.
2. **Duration, not just timestamp** — a span has start AND end. You don't calculate latency from two separate log lines and hope they matched.

With a trace tree you look at Monday's recipe search span, expand it, and immediately see: embedding 1800ms, Qdrant 40ms. The bottleneck is Ollama — and only for Monday, meaning it was the first call after a circuit breaker half-open state, not a general slowdown. That's a 10-second answer vs 20 minutes of log archaeology.

---

## Architecture Decisions

### Observability tool: Langfuse

Three alternatives evaluated:

**LangSmith** — rejected. Not open-source; requires Enterprise license for self-hosting. Tightly coupled to LangChain ecosystem. ChefAgent uses Semantic Kernel.

**OpenTelemetry + Jaeger** — rejected. Generic APM has no concept of an LLM generation. Can't see prompt, completion, or token count without building a custom semantic layer. Significant setup overhead for no gain here. (Note: Langfuse's trace model maps directly to OTel concepts — switching later is feasible.)

**Arize Phoenix** — rejected. Smaller ecosystem, less mature .NET integration at time of decision.

**Langfuse** — chosen. Open-source, self-hostable, purpose-built for LLM tracing. First-class support for prompt/completion/token/latency per generation. REST API is language-agnostic — works from C#/.NET without an official SDK.

### Deployment: self-hosted v2

Three deployment modes evaluated:

**Langfuse Cloud (free tier)** — 50K observations/month, zero infrastructure burden. Rejected because traces contain LLM input/output and user query text — self-hosting keeps all data on the machine, consistent with the zero-paid-services constraint. Code is identical; only `BaseUrl` changes to switch.

**Langfuse v3 self-hosted** — current active version. Adds ClickHouse (OLAP) + MinIO (blob storage) + separate worker container = 5 containers, ~1.2-1.5GB additional RAM. On 8GB with Qdrant + Redis + Ollama already running, exceeds available headroom. Right choice for a production VM (16GB+).

**Langfuse v2 self-hosted** — chosen. Only needs Postgres + langfuse-server = ~650MB. All traces, spans, and LLM generation logging needed here are fully supported in v2. Upgrade path to v3 is documented.

RAM budget with v2:

| Service | Estimated RAM |
|---------|--------------|
| Ollama (Docker, llama3.2:1b) | ~2,000MB |
| Qdrant | ~200MB |
| Redis | ~50MB |
| Postgres (Langfuse) | ~175MB |
| Langfuse server | ~450MB |
| Docker + OS overhead | ~1,500MB |
| **Total** | **~4,375MB** |

Feasible in Codespaces (8GB).

### Trace context propagation: parameter passing

Two approaches considered:

**Inject ITracingService into every class** — rejected. If Langfuse is unreachable and StartSpan() throws, the exception propagates through QdrantService, RulesEngine, OllamaService — killing the user's request. Recipe search fails because Langfuse is unreachable. Backwards.

**Pass TraceContext through method parameters** — chosen. ChatController creates a TraceContext (plain record: traceId, spanId). Passes it to AgentOrchestrator.RouteAsync(classified, ctx), which passes it to each agent. The only class that knows Langfuse exists is Tracing.cs. All outbound calls are wrapped in try/catch and fire-and-forget via Channel<T>. A background worker flushes every 2 seconds (dev) / 5 seconds (prod).

**Result:** Langfuse outage -> silent no-ops, not request failures. This is the same model as OpenTelemetry's Activity propagation.

**Fire-and-forget design:**
```
Request thread                    Background worker
-----------------                 -----------------
StartSpan() called
  -> write to Channel<T>          <- reads from channel
  -> returns immediately          -> POST to Langfuse REST API
                                  -> retry on failure (max batch)
                                  -> drop on persistent failure
```

Tracing overhead on the request thread: < 1ms (channel write).

---

## Day 1 — Langfuse setup + Tracing.cs

### Docker Compose

Added two services to `docker-compose.yml`:

**`langfuse-db`** (postgres:16-alpine) — Langfuse's only storage layer in v2. Healthcheck on `pg_isready` so `langfuse-server` waits until DB is ready.

**`langfuse-server`** (langfuse/langfuse:2) — UI + REST API on port 3100.

Key env vars:
- `SALT` — required by v2 for API key encryption (missing on first attempt -> container crashed)
- `LANGFUSE_INIT_*` — auto-creates org, project, and API keys on first boot. No manual UI setup needed.
- `TELEMETRY_ENABLED: false` — disables Langfuse's own usage telemetry

Healthcheck issue: `wget` not available in the langfuse image. Switched `langfuse-server` to `condition: service_started` in api depends_on — server boots in ~2.5s, fast enough. Postgres healthcheck retained (`pg_isready` is always available).

**Startup sequence:**
```bash
docker compose up -d
curl http://localhost:3100/api/public/health
# -> {"status":"OK","version":"2.95.11"}
```

Login: `dev@chefagent.local` / `devpassword` -> ChefAgent project auto-created.

### New files in `src/shared/`

**`LangfuseOptions.cs`** — config binding record. Binds from "Langfuse" section. Key fields: `Enabled` (master toggle, no-op when false), `BaseUrl` (only change needed to switch cloud/self-hosted), `BatchSize`, `FlushIntervalSeconds`.

**`TraceContext.cs`** — plain record, zero dependencies. Fields: `TraceId`, `SpanId`, `CorrelationId` (first 12 chars of TraceId for log correlation). Methods: `WithNewSpan()` creates child context without mutating parent. `IsNone` short-circuits tracing when disabled. `TraceContext.None` static for disabled/uninitialized paths.

**`Tracing.cs`** — the only class that knows Langfuse exists. Implements `IHostedService` for background flush worker. Public API:
- `StartTrace(name, sessionId, input)` -> TraceContext — call once per /chat request
- `StartSpan(parent, name, input?, metadata?)` -> child TraceContext
- `EndSpan(ctx, output?, statusMessage?, metadata?)`
- `LogGeneration(ctx, name, model, prompt, completion, estimatedTokens, latencyMs)` — LLM calls specifically
- `EndTrace(ctx, output?, statusMessage?)` — closes root span + updates trace

Channel design: `BoundedChannelFullMode.DropOldest` — if worker falls behind, old events drop rather than consuming unbounded memory. 5000 event capacity. `StopAsync` drain loop ensures no spans lost on shutdown.

Langfuse ingestion uses upsert-by-id: `trace-create` sent twice (create + update with final output) works because Langfuse merges on `id`.

NuGet packages added to `ChefAgent.Shared.csproj`:
```
Microsoft.Extensions.Hosting.Abstractions 8.0.1
Microsoft.Extensions.Options 8.0.2
```

### Wiring

**`appsettings.json`** — added Langfuse section:
```json
"Langfuse": {
  "Enabled": true,
  "BaseUrl": "http://localhost:3100",
  "PublicKey": "pk-lf-local",
  "SecretKey": "sk-lf-local",
  "BatchSize": 20,
  "FlushIntervalSeconds": 2
}
```
`FlushIntervalSeconds: 2` in dev for fast trace visibility. Use 5 in production.

**`ServiceRegistration.cs`** — added `AddObservability()`:
```csharp
services.Configure<LangfuseOptions>(config.GetSection(LangfuseOptions.Section));
services.AddHttpClient<Tracing>(client => client.Timeout = TimeSpan.FromSeconds(5));
services.AddSingleton<Tracing>();
services.AddHostedService(sp => sp.GetRequiredService<Tracing>());
```
`AddSingleton` + `AddHostedService` on the same instance: DI container starts the background worker on app boot while still letting agents resolve `Tracing` directly.

**`Endpoints.cs`** — `/chat` handler changes:
- `Tracing tracing` added to handler parameters (resolved from DI automatically)
- `StartTrace` called first line, before guardrails
- `EndTrace` called after `orchestrator.RouteAsync` returns
- Early exits (injection blocked, rate limited, repeat) originally skipped EndTrace — fixed in Day 3

---

## Day 2 — Instrument all agent calls

### AgentOrchestrator

`RouteAsync` signature updated to accept `TraceContext traceCtx` as second parameter. Correlation ID flows into every log line:

```
[f350354325bf] [Orchestrator] Intent=SearchRecipe Query='a pasta'
```

Spans added for every operation inside `RouteAsync`:

| Span | What it covers |
|------|---------------|
| `orchestrator` | Root — covers entire agent dispatch |
| `session.append_user_message` | Redis write, user turn |
| `session.load_profile` | Redis read + merge |
| `recipe_agent.search` | Full recipe search pipeline |
| `diet_agent.validate` | Per-recipe diet validation (one span each) |
| `session.append_assistant_message` | Redis write, assistant turn |
| `session.get_plan` | Redis read for GetMealPlan |
| `planner_agent.generate` | Full plan generation |
| `planner_agent.modify` | Plan modification |
| `ollama.general_question` | Direct Ollama call + LogGeneration |

Every span captures `statusMessage: "ok" / "error" / "circuit_open"` so failures are immediately visible in the dashboard without reading logs.

`LogGeneration` wired for `GeneralQuestion` Ollama calls — captures model name, prompt, completion, estimated token count, latency.

### Verified trace tree (first live trace)

Request: `"find me a quick chicken dinner"` | Session: `trace-test-1`

```
TRACE chat (17.30s)
└── SPAN chat
    └── SPAN orchestrator (17.28s)
        ├── SPAN session.append_user_message (0.03s)
        ├── SPAN session.load_profile (0.01s)
        ├── SPAN recipe_agent.search (17.24s)   <- bottleneck immediately visible
        └── SPAN session.append_assistant_message (0.00s)
```

**Key insight from first trace:** bottleneck is immediately obvious — `recipe_agent.search` consumed 17.24s of 17.30s total. Session operations are negligible (30ms, 10ms). No log reading needed — one glance at the dashboard answers "what was slow?"

---

## Day 3 — Full trace coverage: inner agent spans + guardrail exits

### Deeper agent instrumentation

Extended `TraceContext` propagation into every agent's internal LLM calls:

**`IntentRouter`** — `intent.llm_extraction` span added to `TryExtractProfileWithLlmAsync`. Fires when a message contains implicit dietary context ("I'm lactose intolerant, find me pasta") and LLM entity extraction is needed. Captures `statusMessage: "circuit_open"` when breaker is open. `traceCtx` now passed from `Endpoints.cs` -> `ClassifyAsync` -> `TryExtractProfileWithLlmAsync`.

**`RecipeReranker`** — `reranker.llm` span added to `RerankAsync`. Opt-in path (`rerank: true`) — span shows `circuit_open` / `invalid_output` / result count. `parentCtx` forwarded from `RecipeSearchPlugin.SearchRecipesAsync`.

**`DietValidationPlugin`** — `diet.llm_validation` span added to `CallLlmValidationAsync`. Only fires when rules engine can't decide (ambiguous ingredients). Span shows `circuit_open` / `error` / `ok`.

Full span inventory:

| Span | Layer | Fires when |
|------|-------|-----------|
| `chat` | Endpoint | Every request |
| `orchestrator` | Orchestrator | Every request past guardrails |
| `session.append_user_message` | Session | Every request with sessionId |
| `session.load_profile` | Session | Every request with sessionId |
| `recipe_agent.search` | Recipe Agent | SearchRecipe, ValidateDiet |
| `diet_agent.validate` | Diet Agent | Per recipe, when profile present |
| `diet.llm_validation` | Diet Agent LLM | Ambiguous ingredients only |
| `reranker.llm` | Recipe Agent LLM | When rerank=true |
| `intent.llm_extraction` | IntentRouter LLM | Implicit dietary context |
| `session.append_assistant_message` | Session | Every request with sessionId |
| `session.get_plan` | Session | GetMealPlan intent |
| `planner_agent.generate` | Planner Agent | CreateMealPlan intent |
| `planner_agent.modify` | Planner Agent | ModifyMealPlan intent |
| `ollama.general_question` | Orchestrator LLM | GeneralQuestion intent |

### Guardrail early exits — clean trace closure

**Design question:** should injection-blocked and rate-limited requests appear in Langfuse?

Decision: **yes, but closed cleanly with statusMessage** — not as orphaned open traces. Blocked requests don't reach agents so there's no agent span tree to pollute. But a closed trace with `statusMessage: "blocked"` lets you see attack patterns in the dashboard. The `GuardrailAuditLog` remains the authoritative source for security events.

`EndTrace` added to all three early exits in `Endpoints.cs`:

```csharp
// Injection blocked
tracing.EndTrace(traceCtx, output: validation.RejectionReason);

// Rate limited
tracing.EndTrace(traceCtx, output: rateMsg);

// Repeated query
tracing.EndTrace(traceCtx, output: repeatMsg);
```

### Verified trace tree (dairy-free pasta, Timeline view)

Request: `"find me a dairy-free pasta"` | Session: `trace-test-2`

```
chat
└── orchestrator
    ├── session.append_user_message  (~0.03s)
    ├── session.load_profile         (~0.01s)
    ├── recipe_agent.search          (~1.2s — Ollama embed)
    ├── diet_agent.validate          (~0.005s — rules, 1 violation)
    ├── diet_agent.validate          (~0.001s — rules, 5 violations)
    ├── diet_agent.validate          (~0.001s — rules, 3 violations)
    ├── diet_agent.validate          (~0.001s — rules, 0 violations — compatible)
    ├── diet_agent.validate          (~0.001s — skip, ambiguous)
    └── session.append_assistant_message
```

5 `diet_agent.validate` spans — one per recipe. No `diet.llm_validation` spans — rules handled all cases. Timeline confirms rules engine cost is negligible vs Ollama embedding.

### Test fixture update

`IntentRouterTests.cs` — `Tracing` with `Enabled: false` wired into test constructor. No HTTP calls to Langfuse in tests, no infrastructure dependency.

```csharp
var tracingOptions = Mock.Of<IOptions<LangfuseOptions>>(o =>
    o.Value == new LangfuseOptions { Enabled = false, BaseUrl = "http://localhost" }
);
var tracing = new Tracing(tracingOptions, logger, new HttpClient());
```

---

## Key Learnings

**Tracing must never break the request.** This single constraint drives the entire architecture — parameter passing over injection, fire-and-forget over synchronous calls, try/catch everywhere in `Tracing.cs`, `BoundedChannelFullMode.DropOldest` over unbounded queues.

**`IHostedService` is how background workers register in ASP.NET Core.** The container starts it on boot, stops it on shutdown. `StopAsync` drain ensures no spans lost on process exit.

**`LANGFUSE_INIT_*` variables eliminate manual UI setup.** First-boot auto-provisioning of org, project, and API keys is the right pattern for dev environments — reproducible, no manual steps, works from `docker compose up`.

**`SALT` is a required env var in Langfuse v2.** Not documented prominently — found via container crash logs. Used for API key encryption.

**The bottleneck is always obvious in a trace.** The first live trace immediately showed `recipe_agent.search` at 17.24s vs 0.03s for Redis. No log archaeology needed.

**`statusMessage` on every span is essential.** `"ok"` / `"error"` / `"circuit_open"` lets you filter the dashboard for failures without reading individual span details. Design for failure visibility from the start.

**Disable tracing in tests explicitly.** `Enabled: false` in test fixtures prevents HTTP calls to Langfuse during unit tests — no flaky tests, no infrastructure dependency, zero overhead.

**Span granularity follows the cost hierarchy.** LLM calls get their own spans (expensive, variable). Rules engine checks are bundled inside the `diet_agent.validate` span (cheap, deterministic). No need to span individual rule evaluations — the signal isn't there.

---

## Files Changed

```
docker-compose.yml                           — langfuse-db + langfuse-server services
src/shared/LangfuseOptions.cs                — NEW: config binding
src/shared/TraceContext.cs                   — NEW: lightweight context record
src/shared/Tracing.cs                        — NEW: fire-and-forget Langfuse client
src/shared/ChefAgent.Shared.csproj           — hosting + options NuGet packages
src/api/appsettings.json                     — Langfuse section
src/api/ServiceRegistration.cs               — AddObservability() + Tracing in all agent ctors
src/api/Endpoints.cs                         — StartTrace/EndTrace on all exits + traceCtx to ClassifyAsync
src/agents/Orchestrator/AgentOrchestrator.cs — RouteAsync(classified, traceCtx), all handler spans
src/agents/Orchestrator/IntentRouter.cs      — intent.llm_extraction span, traceCtx param
src/agents/RecipeAgent/RecipeSearchPlugin.cs — parentCtx forwarded to reranker
src/agents/RecipeAgent/RecipeReranker.cs     — reranker.llm span
src/agents/DietAgent/DietValidationPlugin.cs — diet.llm_validation span
src/tests/IntentRouterTests.cs               — Tracing(Enabled=false) in test fixture
docs/adrs/010-observability-architecture.md  — NEW: ADR-010
```

---

## Deferred to Later Days

- `/admin/metrics` aggregate endpoint — Day 4
- Tracing overhead measurement — Day 6-7
- Screenshot best traces for portfolio — Day 6-7

---

## Next: Day 4 — `/admin/metrics` aggregate endpoint

In-memory aggregation of request counts, latency percentiles (p50/p95/p99), circuit breaker states, and guardrail event counts. Served at `GET /admin/metrics`. Simple dashboard polling it every few seconds.