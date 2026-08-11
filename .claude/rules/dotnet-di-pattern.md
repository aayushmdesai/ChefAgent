# .NET dependency injection pattern

All service wiring for the backend lives in one place: `src/api/ServiceRegistration.cs`, in `AddChefAgentServices`, which calls a sequence of private extension methods (`AddRedis`, `AddInfrastructure`, `AddObservability`, `AddRecipeAgent`, `AddDietAgent`, `AddOrchestrator`, `AddMealPlannerAgent`, `AddApiServices`). Everything is registered `AddSingleton` — one instance per process, shared across requests — except `CircuitBreaker`, which uses `AddKeyedSingleton` with keys `"ollama"` and `"redis"` for two independently-tripping breakers.

**Why:** This is the only wiring pattern used anywhere in the solution — confirmed with zero exceptions across every provider, agent, and guardrail registration in the file.

**How to apply:**
- Adding a new LLM or embedding provider: add another `if (providerName == "x") { ... return new XProvider(...); }` branch inside the existing `services.AddSingleton<ILlmProvider>(sp => {...})` or `<IEmbeddingProvider>(sp => {...})` factory lambda in `AddInfrastructure` — don't create a separate registration method. Follow the existing branch shape exactly: resolve `IHttpClientFactory` → `CreateClient("Cloud")`, read `config["X:ApiKey"]` and throw `InvalidOperationException` if missing, read `config["X:Model"]` with a sane default.
- Adding a new agent: give it its own private `AddXAgent(this IServiceCollection services, IConfiguration config)` extension method (matching `AddRecipeAgent`/`AddDietAgent`/`AddMealPlannerAgent`), register it as a singleton with a factory lambda that resolves its dependencies via `sp.GetRequiredService<T>()`, and add one line calling it from `AddChefAgentServices`.
- Config is read via raw `IConfiguration` indexing (`config["Section:Key"] ?? "default"`), not `IOptions<T>` binding — except `LangfuseOptions`, the one place that uses `services.Configure<LangfuseOptions>(...)`. Match whichever pattern the surrounding code already uses; don't introduce a third style.
