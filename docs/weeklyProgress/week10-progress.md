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

**`appsettings.json`** — added Langfuse section. `FlushIntervalSeconds: 2` in dev for fast trace visibility. Use 5 in production.

**`ServiceRegistration.cs`** — added `AddObservability()`:
```csharp
services.Configure<LangfuseOptions>(config.GetSection(LangfuseOptions.Section));
services.AddHttpClient<Tracing>(client => client.Timeout = TimeSpan.FromSeconds(5));
services.AddSingleton<Tracing>();
services.AddHostedService(sp => sp.GetRequiredService<Tracing>());
```
`AddSingleton` + `AddHostedService` on the same instance: DI container starts the background worker on app boot while still letting agents resolve `Tracing` directly.

**`Endpoints.cs`** — `Tracing tracing` added to handler parameters, `StartTrace` first line, `EndTrace` after `RouteAsync`. Early exits fixed in Day 3.

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

Every span captures `statusMessage: "ok" / "error" / "circuit_open"`.

`LogGeneration` wired for `GeneralQuestion` Ollama calls — captures model, prompt, completion, estimated tokens, latency.

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

Bottleneck visible in one glance — `recipe_agent.search` consumed 17.24s of 17.30s total.

---

## Day 3 — Full trace coverage: inner agent spans + guardrail exits

### Deeper agent instrumentation

**`IntentRouter`** — `intent.llm_extraction` span. Fires on implicit dietary context. `traceCtx` passed from `Endpoints.cs` -> `ClassifyAsync` -> `TryExtractProfileWithLlmAsync`.

**`RecipeReranker`** — `reranker.llm` span. Opt-in path (`rerank: true`).

**`DietValidationPlugin`** — `diet.llm_validation` span. Ambiguous ingredients only.

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

Decision: blocked requests appear in Langfuse as closed traces with `statusMessage`, not orphaned open traces. The `GuardrailAuditLog` remains authoritative for security events — Langfuse shows the pattern, audit log shows the detail.

```csharp
tracing.EndTrace(traceCtx, output: validation.RejectionReason);  // injection blocked
tracing.EndTrace(traceCtx, output: rateMsg);                      // rate limited
tracing.EndTrace(traceCtx, output: repeatMsg);                    // repeated query
```

### Verified trace tree (dairy-free pasta, Timeline view)

```
chat
└── orchestrator
    ├── session.append_user_message  (~0.03s)
    ├── session.load_profile         (~0.01s)
    ├── recipe_agent.search          (~1.2s — Ollama embed)
    ├── diet_agent.validate x5       (~0.001-0.005s each — rules engine)
    └── session.append_assistant_message
```

5 `diet_agent.validate` spans, no `diet.llm_validation` — rules handled all cases.

### Test fixture update

`IntentRouterTests.cs` — `Tracing(Enabled=false)` in constructor. No Langfuse HTTP calls in unit tests.

---

## Day 4 — `/admin/metrics` aggregate endpoint

### Design: in-memory vs querying Langfuse

**Rejected: query Langfuse** — creates runtime dependency. If Langfuse is down, metrics fail too. Breaks the isolation built all week. Langfuse aggregation queries aren't designed for sub-100ms polling.

**Chosen: in-memory `MetricsCollector`** — same pattern as `GuardrailAuditLog`. `ConcurrentQueue<RequestSample>`, 5000-sample ring buffer, singleton. Zero infrastructure dependencies, microsecond reads.

### Sliding 5-minute window vs last-N requests

"Last 1000 requests" at low traffic reflects the last 100 hours — a spike from 3 hours ago still drags p95. 5-minute window reflects right now.

### What p50/p95/p99 mean

**p50** — median. Half faster, half slower. Your normal experience.

**p95** — 95% of requests faster than this. Ignores outliers. What a typical user experiences under load.

**p99** — worst 1%. Alert on this in production. p99 spike + p50 stable = occasional cold starts. Both rising = systemic problem.

Average is deceptive: 99 requests at 200ms + 1 at 15000ms = 350ms average. No user experienced 350ms. Percentiles tell the real story.

### `MetricsCollector.cs`

- `Record(intent, latencyMs, wasBlocked)` — blocked requests excluded from latency (sub-ms, would skew p50)
- `GetSnapshot()` — filters to 5-min window, nearest-rank percentiles computed fresh each call
- `ConcurrentQueue<RequestSample>` — same thread-safety as `GuardrailAuditLog`

### Verified output (full test run)

```json
{
  "windowMinutes": 5,
  "requestsTotal": 13,
  "requestsCompleted": 11,
  "requestsBlocked": 2,
  "requestsByIntent": {
    "SearchRecipe": 6, "GeneralQuestion": 1, "CreateMealPlan": 1,
    "GetMealPlan": 1, "ModifyMealPlan": 1, "ValidateDiet": 1
  },
  "latency": { "p50Ms": 101, "p95Ms": 14326, "p99Ms": 14326, "minMs": 12, "maxMs": 14326 }
}
```

p50 of 101ms = warm system fast. p99 of 14326ms = LLM extraction path (Ollama cold). Numbers validate the architecture.

---

## Day 5 — Correlation IDs in structured logs

### Goal

Every log line carries the same short ID as the Langfuse trace URL. Before: agent log lines orphaned, no request association. After: one ID cross-references logs and traces instantly.

### `appsettings.json` — two bugs fixed

**`Console.IncludeScopes: true`** — without this, `BeginScope` calls are no-ops. Why Option B appeared broken on first attempt.

**`Langfuse.BaseUrl: http://langfuse-server:3000`** — `localhost:3100` works from host but not inside Docker. Inside containers the service name is the hostname. Was silently failing — traces sent to wrong address.

**`Langfuse` section was missing entirely from repo** — section existed in generated files but never committed. Fixed here.

### `BeginScope` pattern

```csharp
using (_logger.BeginScope(new { CorrelationId = parentCtx?.CorrelationId ?? "none" }))
{
    // all existing log calls unchanged
}
```

Applied to: `RecipeSearchPlugin`, `RecipeReranker`, `DietValidationPlugin`, `IntentRouter`.
`AgentOrchestrator` uses explicit interpolation (`[{CorrelationId}]`) — same result, different style.

Option B (scopes) won over Option A (explicit on every call) — one line per method vs touching 20+ log calls across 5 files.

### Verified output

```
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       Validating recipe 'Pasta Fagiol' | restrictions: [dairy-free]
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       [DietAgent] Recipe='Pasta Fagiol' Layer=Rules Time=0ms Compatible=true
api-1  | => { CorrelationId = 54887b20a749 }
api-1  |       [Orchestrator] Intent=SearchRecipe Time=78ms Recipes=5
```

Same ID across all agents. Open Langfuse, find trace `54887b20a749`, exact same request.

---

## Days 6-7 — Overhead measurement + test suite + portfolio artifacts

### Overhead assessment

Tracing overhead is < 1ms per request. Evidence from architecture and live data:

- `StartSpan` writes to `Channel<T>` and returns — nanoseconds on the request thread
- Background worker POSTs to Langfuse asynchronously (199ms, 47ms seen in logs — after response returned)
- p50 of 101ms reflects Ollama embedding cost, not tracing overhead
- Injection blocked: 4ms total. Repeated query: 3ms total. These are the guardrail-only paths — if tracing added overhead it would show here. It doesn't.
- Fire-and-forget confirmed working as intended

No rebuild needed to verify — the numbers already prove it.

### Observability test suite

`scripts/eval/week10_observability_test.py` — 13 test scenarios covering every trace path:

| # | Scenario | Intent | Session |
|---|----------|--------|---------|
| 1 | Quick chicken dinner | SearchRecipe | obs-basic-1 |
| 2 | Dairy-free pasta | SearchRecipe | obs-dairy-1 |
| 3 | Nut-free + dairy-free merge | SearchRecipe | obs-dairy-1 |
| 4 | Broiling vs baking | GeneralQuestion | obs-general-1 |
| 5 | Plan dinners for the week | CreateMealPlan | obs-plan-1 |
| 6 | Show meal plan | GetMealPlan | obs-plan-1 |
| 7 | Swap Monday dinner | ModifyMealPlan | obs-plan-1 |
| 8 | Injection attempt | blocked | obs-inject-1 |
| 9 | Repeated query x3 | repeated_query | obs-repeat-1 |
| 10 | Is pasta carbonara safe? | ValidateDiet | obs-diet-1 |
| 11 | Lactose intolerant soup | SearchRecipe + LLM extraction | obs-extract-1 |
| 12 | /admin/metrics snapshot | — | — |
| 13 | /admin/guardrails snapshot | — | — |

Auto-saves to `docs/architecture/screenshots/`:
- `metrics_snapshot.json` — full metrics JSON
- `guardrails_snapshot.json` — all guardrail events
- `observability_test_summary.md` — table of all results + metrics breakdown

### Final test run results

```
total:     13 requests
completed: 11
blocked:   2 (injection + repeated query)
p50:       101ms
p95:       14326ms  (LLM extraction path)
p99:       14326ms
min:       12ms
max:       14326ms

by intent: SearchRecipe=6, GeneralQuestion=1, CreateMealPlan=1,
           GetMealPlan=1, ModifyMealPlan=1, ValidateDiet=1
guardrails: injection_blocked x1, repeated_query x1
```

### Portfolio screenshots

Three traces to screenshot from `http://localhost:3100` (manual — Codespaces browser can't save to remote filesystem):

| File | Session | Timeline view |
|------|---------|--------------|
| `trace_search_dairy_free.png` | obs-dairy-1 | chat → orchestrator → recipe_agent.search → diet_agent.validate x5 |
| `trace_meal_plan_generation.png` | obs-plan-1 | planner_agent.generate → 7 day branches |
| `trace_injection_blocked.png` | obs-inject-1 | short trace, no agent spans |

Screenshots deferred to Month 5 portfolio site build — Langfuse traces persist as long as the Docker volume exists.

---

## Key Learnings

**Tracing must never break the request.** This single constraint drives the entire architecture — parameter passing over injection, fire-and-forget over synchronous calls, try/catch everywhere in `Tracing.cs`, `BoundedChannelFullMode.DropOldest` over unbounded queues.

**`IHostedService` is how background workers register in ASP.NET Core.** The container starts it on boot, stops it on shutdown. `StopAsync` drain ensures no spans lost on process exit.

**`LANGFUSE_INIT_*` variables eliminate manual UI setup.** First-boot auto-provisioning of org, project, and API keys — reproducible, no manual steps, works from `docker compose up`.

**`SALT` is a required env var in Langfuse v2.** Not documented prominently — found via container crash logs. Used for API key encryption.

**The bottleneck is always obvious in a trace.** First live trace immediately showed `recipe_agent.search` at 17.24s vs 0.03s for Redis. No log archaeology needed.

**`statusMessage` on every span is essential.** `"ok"` / `"error"` / `"circuit_open"` lets you filter failures without reading individual span details.

**Disable tracing in tests explicitly.** `Enabled: false` in test fixtures — no Langfuse HTTP calls, no flaky tests, no infrastructure dependency.

**Span granularity follows the cost hierarchy.** LLM calls get their own spans (expensive, variable). Rules engine checks bundle inside `diet_agent.validate` (cheap, deterministic).

**`Console.IncludeScopes: true` is required for BeginScope to render.** Without it, scopes are silently swallowed. Easy to miss — document it.

**Docker service names are hostnames, not localhost.** `http://langfuse-server:3000` inside Docker, `http://localhost:3100` from host. Mixing them causes silent failures — traces appear to send but go nowhere.

**p50 is your normal experience. p99 is your worst day.** Both matter. Neither tells the full story alone.

---

## Files Changed

```
docker-compose.yml                           — langfuse-db + langfuse-server services
src/shared/LangfuseOptions.cs                — NEW: config binding
src/shared/TraceContext.cs                   — NEW: lightweight context record
src/shared/Tracing.cs                        — NEW: fire-and-forget Langfuse client
src/shared/MetricsCollector.cs               — NEW: sliding 5-min window, p50/p95/p99
src/shared/ChefAgent.Shared.csproj           — hosting + options NuGet packages
src/api/appsettings.json                     — Langfuse + Console.IncludeScopes + correct BaseUrl
src/api/ServiceRegistration.cs               — AddObservability() + Tracing + MetricsCollector
src/api/Endpoints.cs                         — StartTrace/EndTrace all exits + /admin/metrics + collector.Record
src/agents/Orchestrator/AgentOrchestrator.cs — RouteAsync(classified, traceCtx), all handler spans
src/agents/Orchestrator/IntentRouter.cs      — intent.llm_extraction span + BeginScope(CorrelationId)
src/agents/RecipeAgent/RecipeSearchPlugin.cs — parentCtx to reranker + BeginScope(CorrelationId)
src/agents/RecipeAgent/RecipeReranker.cs     — reranker.llm span + BeginScope(CorrelationId)
src/agents/DietAgent/DietValidationPlugin.cs — diet.llm_validation span + BeginScope(CorrelationId)
src/tests/IntentRouterTests.cs               — Tracing(Enabled=false) in test fixture
docs/adrs/010-observability-architecture.md  — NEW: ADR-010
docs/architecture/screenshots/metrics_snapshot.json       — NEW: live metrics artifact
docs/architecture/screenshots/guardrails_snapshot.json    — NEW: guardrail events artifact
docs/architecture/screenshots/observability_test_summary.md — NEW: full test run summary
scripts/eval/week10_observability_test.py    — NEW: 13-scenario observability test suite
```

---

## ADR Coverage

No new ADR needed for Week 10. ADR-010 covers the full observability architecture — tool choice (Langfuse vs LangSmith vs OTel), deployment mode (v2 vs v3 vs cloud), and propagation pattern (parameter passing vs injection). The metrics endpoint and correlation IDs are implementation details that flow naturally from those decisions.