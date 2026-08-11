# src/ — .NET backend

Single solution, `src/ChefAgent.sln`, all projects targeting `net8.0` with Microsoft.SemanticKernel 1.30.0.

| Project | Path | Purpose |
|---|---|---|
| `ChefAgent.Api` | `src/api/` | Entry point, endpoints, DI wiring, DTOs |
| `ChefAgent.Shared` | `src/shared/` | Providers, guardrails, `DietaryRules`, `SessionStore`, `Observability` |
| `ChefAgent.Agents.Recipe` | `src/agents/RecipeAgent/` | Retrieval — see [src/agents/CLAUDE.md](agents/CLAUDE.md) |
| `ChefAgent.Agents.Diet` | `src/agents/DietAgent/` | Dietary validation — see [src/agents/CLAUDE.md](agents/CLAUDE.md) |
| `ChefAgent.Agents.Planner` | `src/agents/PlannerAgent/` | Meal planning — see [src/agents/CLAUDE.md](agents/CLAUDE.md) |
| `ChefAgent.Agents.Orchestrator` | `src/agents/Orchestrator/` | Intent routing + coordination — see [src/agents/CLAUDE.md](agents/CLAUDE.md) |
| `ChefAgent.Tests` | `src/tests/` | xUnit + Moq, 10 files — see coverage note below |

**On this branch (`phase-2`/`phase-2-agentic`)**, `ChefAgent.Shared` also carries the pipeline layer (`IAgent`, `AgentRegistry`, `PipelineBuilder`, `PipelineDefinitions`, `PipelineRegistry`, `PipelineRunner`) and `ChefAgent.Agents.Orchestrator` has the feature-flagged pipeline cutover — see [src/agents/CLAUDE.md](agents/CLAUDE.md) for the full architecture, current cutover state, and open blockers. This file covers what's unchanged from the Phase 1 baseline.

## Entry point & startup order

`src/api/Program.cs`:
1. `builder.Services.AddChefAgentServices(builder.Configuration)` — all DI, see below
2. `app.Build()`
3. Redis pre-warm ping, then (same `try` block) a forced `PipelineRegistry` resolution so capability-wiring errors fail at startup rather than on the first pipeline-routed request — **but both share one `catch`, which only logs and continues; see [src/agents/CLAUDE.md](agents/CLAUDE.md) for why that's a real gap, not just defensive redundancy.**
4. `app.UseCors()`
5. `app.MapChefAgentEndpoints()` (`src/api/Endpoints.cs`) — `/health`, `GET /` (service info), `/recipes/search`, `/recipes/search-validated`, `/profile`, `/chat`, plus an admin/guardrails route.

## DI / provider wiring pattern

All wiring is centralized in `src/api/ServiceRegistration.cs` — see [[dotnet-di-pattern]] for the full convention. Provider selection is config-driven:
- `LlmProvider`: `"groq"` (prod, via Groq's OpenAI-compatible API) or default `"ollama"` (local)
- `EmbeddingProvider`: `"voyage"` (prod), `"nomic"`, `"huggingface"`, or default `"ollama"` (local)

Both are read as top-level `IConfiguration` keys (not nested under a section), e.g. `config["LlmProvider"]`. Missing an API key for a cloud provider throws `InvalidOperationException`, but only lazily — the factory lambda only runs the first time something resolves `ILlmProvider`/`IEmbeddingProvider` (typically the first `/chat` or `/recipes/search` request), **not** at `app.Build()`. A misconfigured deployment can pass its health check and look up, then 500 on the first real request. Don't assume "health check passed" means provider config is valid.

**Verified footgun on this branch**: `AddChefAgentServices` calls both `AddAgentRegistry()` and `AddApiServices()` twice each (`ServiceRegistration.cs`, lines ~50-54). Checked and appears harmless in practice — .NET's `AddSingleton` last-registration-wins on `GetRequiredService<T>()`, and both duplicated calls register identical factories, so the second call's registration is simply the one that's ever resolved; `AddCors`/`AddHealthChecks` are documented-idempotent APIs. Still genuinely dead/duplicate code, not intentional — don't copy this pattern when adding a new `AddX()` call here.

## Config pattern

See [[dotnet-di-pattern]] for the raw-`IConfiguration`-indexing-vs-`IOptions<T>` convention itself. The fact specific to this file's scope:

`.env.example` at the repo root is stale — it lists only the local-Ollama vars (`OLLAMA_*`, `QDRANT_ENDPOINT`, `QDRANT_COLLECTION`, `REDIS_CONNECTION_STRING`) and doesn't mention `LlmProvider`, `EmbeddingProvider`, or any cloud API key (`Groq__ApiKey`, `Voyage__ApiKey`, `Nomic__ApiKey`, `HuggingFace__ApiKey`, `Qdrant__ApiKey`) even though `docker-compose.yml`'s `api` service environment block sets all of them. Use `docker-compose.yml` as the source of truth for what env vars the app actually reads in a cloud-provider configuration, not `.env.example`.

## Observability (`src/shared/Observability/`)

`Tracing.cs` is, by its own doc-comment, "the ONLY class in ChefAgent that knows Langfuse exists" — everything else passes a `TraceContext` (`TraceContext.cs`, a plain record with `TraceId`/`SpanId`, no Langfuse dependency) through method parameters rather than injecting a tracing service everywhere, specifically so a Langfuse outage can't propagate an exception into business logic. Call pattern, used consistently in `AgentOrchestrator`, `IntentRouter`, `DietValidationPlugin`: `var ctx = _tracing.StartSpan(parentCtx, "span.name"); ... _tracing.EndSpan(ctx, output: ..., statusMessage: "ok"|"error"|"circuit_open")`. `Tracing` never blocks the request thread (writes to a bounded `Channel`, a background `IHostedService` worker does the actual POST to Langfuse) and never throws to callers (every method try/catches internally; if the channel write fails, the event is silently dropped, not retried). `MetricsCollector.cs` is separate and unrelated to Langfuse — an in-memory, dependency-free sliding-5-minute-window latency/intent counter, deliberately not backed by querying Langfuse (so `/admin/metrics` can't fail because Langfuse is slow or down).

To add a new span around an existing call: wrap it with `StartSpan`/`EndSpan` following the pattern above — don't add a new tracing abstraction, and don't call Langfuse directly from anywhere outside `Tracing.cs`. **On this branch**, pipeline-dispatched calls get a second, different span lifecycle owned by `PipelineRunner` rather than the agent itself — see [src/agents/CLAUDE.md](agents/CLAUDE.md).

## Build / test / run

```bash
dotnet restore src/ChefAgent.sln
dotnet build src/ChefAgent.sln --no-restore --configuration Release
dotnet test src/ChefAgent.sln --no-build --configuration Release --verbosity normal
```
(exact commands as used in `.github/workflows/ci.yml`)

Local run:
- `make up` — API + Ollama only (`docker-compose.local.yml`, `.env.local`)
- `make up-full` — full 6-service stack (`docker-compose.yml`): api, qdrant, ollama, redis, langfuse-db, langfuse-server
- `make health` — curls `http://localhost:5100/health`
- `make frontend` — `cd src/frontend && npm install --silent && npm run dev`
- `make fresh` — full teardown + rebuild + reload vectors, for when local state is suspect

CI (`.github/workflows/ci.yml`) also runs a `health-check` job against `docker-compose.ci.yml` (Redis + Qdrant only, no Ollama — "model too large for CI") via `dotnet run --project src/api/ChefAgent.Api.csproj -- --urls "http://localhost:5100"`. A third `eval` job exists in the file but is entirely commented out — eval does not currently run in CI.

## Test coverage — grown substantially on this branch, still real gaps

`src/tests/` has **10 files** on `phase-2`/`phase-2-agentic`, not the 4 from the Phase 1 baseline: the original `CircuitBreakerTests.cs`, `InputGuardTests.cs`, `IntentRouterTests.cs`, `DietaryRulesTests.cs`, plus `AgentRegistrationTests.cs`, `AgentRegistryTests.cs`, `DietValidationPluginTests.cs`, `PipelineBuilderTests.cs`, `PipelineRunnerTests.cs`, `RecipeSearchPluginTests.cs`. Freshly counted on this branch: 97 `[Fact]`/`[Theory]` attributes, 25 `[InlineData]` rows, 114 total test invocations, **6 explicitly skipped** — matches the number in `phase2-week2-progress.md`'s own last reported run exactly, independently reproduced, not just cited.

Real remaining gaps, not fixed by the above: `AgentOrchestrator`, `QueryPreprocessor`, `RecipeReranker`, `OutputGuard`, `RateLimiter`, and every provider class still have zero coverage. `RecipeSearchPlugin.HandleAsync` and `MealPlannerPlugin.HandleAsync` (the `IAgent` entry points) are only covered by `[Fact(Skip = "P2-1: ...")]`/`[Fact(Skip = "P2-2: ...")]` placeholders in `RecipeSearchPluginTests.cs` — empty method bodies documenting the gap, not real assertions (both depend on concrete types Moq can't intercept — `QdrantClient`, or sibling concrete plugins). Don't count these 3 skipped tests as coverage.

README's "80+ tests" claim (Phase 1 era, unchanged on this branch) is now conservative rather than wrong, coincidentally — but still not verified by anyone, so don't treat that as vindication.

Test style: xUnit + Moq, `Make*()` private factory helpers to construct the SUT (e.g. `MakeBreaker(...)`), `// ── Section Name ──` banner comments mirroring production code, method names in `Scenario_ExpectedOutcome` form. This convention holds across the new Phase 2 test files too.

## Deploy

Deploys to Railway via `infra/docker/Dockerfile.api` (multi-stage `dotnet/sdk:8.0` → `dotnet/aspnet:8.0`, `EXPOSE 8080`) — this is confirmed only via `docs/adrs/012-cloud-deployment.md` and a hardcoded production URL referenced in `eval/harnesses/retrieve.py` (`chefagent-production.up.railway.app`); **no Railway config file, IaC, or CI deploy step exists in this repo**, so treat "deploys to Railway" as a documented fact to re-verify if it ever seems out of date, not something this repo can prove on its own.
