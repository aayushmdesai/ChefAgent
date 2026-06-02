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
src/shared/MetricsCollector.cs               — NEW: sliding 5-min window, p50/p95/p99
src/shared/ChefAgent.Shared.csproj           — hosting + options NuGet packages
src/api/appsettings.json                     — Langfuse section
src/api/ServiceRegistration.cs               — AddObservability() + Tracing + MetricsCollector
src/api/Endpoints.cs                         — /admin/metrics endpoint + collector.Record in /chat
src/api/appsettings.json                     — Console.IncludeScopes + Langfuse section + correct BaseUrl
src/agents/Orchestrator/AgentOrchestrator.cs — RouteAsync(classified, traceCtx), all handler spans
src/agents/Orchestrator/IntentRouter.cs      — intent.llm_extraction span + BeginScope(CorrelationId)
src/agents/RecipeAgent/RecipeSearchPlugin.cs — parentCtx forwarded to reranker + BeginScope(CorrelationId)
src/agents/RecipeAgent/RecipeReranker.cs     — reranker.llm span + BeginScope(CorrelationId)
src/agents/DietAgent/DietValidationPlugin.cs — diet.llm_validation span + BeginScope(CorrelationId)
src/tests/IntentRouterTests.cs               — Tracing(Enabled=false) in test fixture
docs/adrs/010-observability-architecture.md  — NEW: ADR-010
```

---

## Deferred to Later Days

- Tracing overhead measurement — Day 6-7
- Screenshot best traces for portfolio — Day 6-7

---

## Next: Days 6-7 — Overhead measurement + portfolio screenshots

Measure tracing overhead (20 requests with tracing on vs off, target < 5ms). Screenshot search trace, plan generation trace, and failure trace for portfolio site.

---

## Day 4 — `/admin/metrics` aggregate endpoint

### Design decision: in-memory vs querying Langfuse

**Rejected: query Langfuse** — Langfuse is an observability backend for humans debugging specific requests. Querying it from a metrics endpoint creates a runtime dependency — if Langfuse is slow or down, `/admin/metrics` fails too. The entire week was spent ensuring Langfuse never affects the request path. Querying it from an endpoint breaks that isolation in the other direction. Also, Langfuse aggregation queries aren't designed for sub-100ms API responses hit by a polling dashboard every few seconds.

**Chosen: in-memory `MetricsCollector`** — same pattern as `GuardrailAuditLog`. `ConcurrentQueue<RequestSample>`, ring buffer cap (5000 samples), singleton registered in DI. Zero infrastructure dependencies, microsecond reads.

### Sliding 5-minute window vs last-N requests

Tracking "last 1000 requests" at low traffic (10 req/hour) reflects the last 100 hours — a latency spike from 3 hours ago still drags p95. A 5-minute window reflects right now, which is what an ops dashboard needs.

### What p50/p95/p99 actually mean

**p50** — median. Half of requests are faster, half slower. Your "normal" experience.

**p95** — 95% of requests are faster than this. Ignores outliers. Tells you what a typical user experiences under real load, not what your worst cold start looks like.

**p99** — worst 1%. Alert on this in production. If p99 spikes while p50 is stable, occasional slow requests (cold starts, circuit breaker recovery). If p50 rises too, systemic problem.

Average is deceptive — 99 requests at 200ms + 1 at 15000ms = 350ms average that no user actually experienced. Percentiles tell the real story.

### `MetricsCollector.cs`

New file in `src/shared/`. Key design choices:

- `Record(intent, latencyMs, wasBlocked)` — blocked requests recorded separately with `wasBlocked: true`, excluded from latency percentiles (guardrail rejections are sub-ms and would skew p50 downward)
- `GetSnapshot()` — filters to 5-minute window, computes percentiles fresh on every call. No pre-aggregation — fast enough at hundreds of samples
- **Nearest-rank percentile** — returns an actual observed value, not a synthetic interpolated midpoint. Simpler to reason about at our sample sizes
- `ConcurrentQueue<RequestSample>` — same thread-safety pattern as `GuardrailAuditLog`

### Wiring

**`Endpoints.cs`** — `/chat` handler:
- `MetricsCollector collector` added to handler parameters
- `Stopwatch` wraps `orchestrator.RouteAsync` to measure end-to-end latency
- `collector.Record(intent, latencyMs)` called after response returns
- `collector.Record("blocked"/"rate_limited"/"repeated_query", 0, wasBlocked: true)` on early exits

**`ServiceRegistration.cs`** — `services.AddSingleton<MetricsCollector>()` in `AddApiServices()`

**`Endpoints.cs`** — new endpoint alongside `/admin/guardrails`:
```csharp
app.MapGet("/admin/metrics", (MetricsCollector collector) => Results.Ok(collector.GetSnapshot()));
```

### Verified output (6 requests, warm system)

```json
{
  "windowMinutes": 5,
  "requestsTotal": 6,
  "requestsCompleted": 6,
  "requestsBlocked": 0,
  "requestsByIntent": { "SearchRecipe": 6 },
  "latency": {
    "p50Ms": 111,
    "p95Ms": 16865,
    "p99Ms": 16865,
    "minMs": 70,
    "maxMs": 16865,
    "meanMs": 3041
  }
}
```

**What the numbers say:** p50 of 111ms means warm requests are fast — Ollama model loaded, Qdrant index warm. p99 of 16865ms is the first cold-start request. This is exactly the story the architecture tells — cold starts are expensive, which is why opt-in reranking and circuit breakers exist. The numbers validate the earlier design decisions.


---

## Day 5 — Correlation IDs in structured logs

### Goal

Every log line for a request carries the same short ID that appears in the Langfuse trace URL. Before this: log lines from `RecipeSearchPlugin`, `DietValidationPlugin`, `IntentRouter` were orphaned — no way to know which request they belonged to without reading timestamps and guessing. After this: one ID cross-references logs and traces instantly.

### `appsettings.json` — two fixes

**`Console.IncludeScopes: true`** — without this, `BeginScope` calls are no-ops in the console output. This is why Option B (log scopes) appeared to not work on first attempt — the config was missing.

**`Langfuse.BaseUrl: http://langfuse-server:3000`** — the previous value (`http://localhost:3100`) works from the host machine but not from inside a Docker container. Inside Docker, the service name is the hostname. This was silently failing — traces were being sent to the wrong address.

**`Langfuse` section was missing entirely** — the section was in the generated output file but never made it into the repo. Fixed here.

### `BeginScope` pattern

```csharp
using (_logger.BeginScope(new { CorrelationId = parentCtx?.CorrelationId ?? "none" }))
{
    // all existing log calls unchanged — they inherit the scope automatically
}
```

Applied to:
- `RecipeSearchPlugin.SearchRecipesAsync`
- `RecipeReranker.RerankAsync`
- `DietValidationPlugin.ValidateRecipeAsync` + `ValidateWithLlmAsync`
- `IntentRouter.ClassifyAsync` + `TryExtractProfileWithLlmAsync`

`AgentOrchestrator` uses explicit string interpolation (`[{CorrelationId}]`) — both approaches produce the same result. Scope is cleaner for agents with many log calls.

### Option A vs Option B — final verdict

Option B (scopes) won. One `BeginScope` at the top of each method, all log calls inside inherit it automatically. Option A (explicit interpolation on every log call) would have required touching 20+ log lines across 5 files — higher surface area, more merge conflicts, more drift risk.

The catch: `Console.IncludeScopes: true` must be set or scopes silently no-op. Documented here as a gotcha.

### Verified output

```
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       Validating recipe 'Pasta Fagiol' | allergies: [] restrictions: [dairy-free]
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       [DietAgent] Recipe='Pasta Fagiol' Layer=Rules Time=0ms Violations=0 Compatible=true
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       [Orchestrator] Intent=SearchRecipe Time=78ms Recipes=5
```

Same `54887b20a749` across `DietValidationPlugin`, `RecipeSearchPlugin`, `AgentOrchestrator` — one request, one ID, consistent across all agents.

Cross-reference: open Langfuse, search trace `54887b20a749`, see the full span tree for the exact same request.

The scope output is verbose (includes ASP.NET's own `SpanId`, `TraceId`, `ConnectionId`) — harmless in development. Production would use a custom log formatter to show only `CorrelationId`.