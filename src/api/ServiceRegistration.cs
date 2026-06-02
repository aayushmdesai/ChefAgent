// ============================================================
// ChefAgent API — Service Registration
// ============================================================
// All DI registrations in one place.
// Each agent is a singleton — one instance shared across requests.
//
// Dependency graph:
//   QdrantClient          ← infrastructure
//   HttpClient "Ollama"   ← infrastructure
//   RecipeSearchPlugin    ← depends on QdrantClient + HttpClient
//   DietValidationPlugin  ← depends on HttpClient
//   IntentRouter          ← depends on HttpClient (reserved for Month 2 LLM)
//   AgentOrchestrator     ← depends on RecipeSearchPlugin + DietValidationPlugin + HttpClient
// ============================================================

namespace ChefAgent.Api;

using System.Text.Json.Serialization;
using ChefAgent.Agents.Diet;
using ChefAgent.Agents.Orchestrator;
using ChefAgent.Agents.PlannerAgent;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared;
using Qdrant.Client;
using StackExchange.Redis;

public static class ServiceRegistration
{
    public static IServiceCollection AddChefAgentServices(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.AddRedis(config);
        services.AddInfrastructure(config);
        services.AddObservability(config);
        services.AddRecipeAgent(config);
        services.AddDietAgent(config);
        services.AddOrchestrator(config);
        services.AddMealPlannerAgent(config);
        services.AddApiServices();
        return services;
    }

    // ── Infrastructure ────────────────────────────────────────

    private static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        // Qdrant — vector database for recipe embeddings
        services.AddSingleton(sp =>
        {
            var endpoint = config["Qdrant:Endpoint"] ?? "http://localhost:6334";
            var uri = new Uri(endpoint);
            return new QdrantClient(uri.Host, uri.Port);
        });

        // Ollama HTTP client — shared across all agents
        // Timeout is long because LLM inference on CPU can take 30+ seconds
        // In AddInfrastructure — Ollama HTTP client
        var ollamaTimeout = config.GetValue<int>("Ollama:TimeoutSeconds", 120);
        services.AddHttpClient(
            "Ollama",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(ollamaTimeout);
            }
        );

        // Serialize enums as strings in JSON responses (e.g. "SearchRecipe" not 1)
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter())
        );

        return services;
    }

    // ── Redis ────────────────────────────────────────
    private static IServiceCollection AddRedis(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        var connectionString = config["Redis:ConnectionString"] ?? "localhost:6379";
        var options = ConfigurationOptions.Parse(connectionString);
        options.ConnectTimeout = config.GetValue<int>("Redis:ConnectTimeoutMs", 2000);
        options.SyncTimeout = config.GetValue<int>("Redis:SyncTimeoutMs", 2000);
        options.AsyncTimeout = config.GetValue<int>("Redis:AsyncTimeoutMs", 2000);
        options.AbortOnConnectFail = false;

        services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(options));
        services.AddSingleton<SessionStore>();
        return services;
    }

    // ── Recipe Agent ──────────────────────────────────────────

    private static IServiceCollection AddRecipeAgent(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.AddSingleton(sp =>
        {
            var qdrant = sp.GetRequiredService<QdrantClient>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            var logger = sp.GetRequiredService<ILogger<RecipeSearchPlugin>>();
            var outputGuard = sp.GetRequiredService<OutputGuard>();
            var circuitBreaker = sp.GetRequiredKeyedService<CircuitBreaker>("ollama");
            var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
            var embeddingModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
            var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";
            var collection = config["Qdrant:CollectionName"] ?? "recipes";

            return new RecipeSearchPlugin(
                qdrant,
                httpClient,
                ollamaUrl,
                embeddingModel,
                collection,
                logger,
                outputGuard,
                new RecipeReranker(
                    httpClient,
                    ollamaUrl,
                    chatModel,
                    outputGuard,
                    circuitBreaker,
                    sp.GetRequiredService<Tracing>(),
                    sp.GetRequiredService<ILogger<RecipeReranker>>()
                ),
                new QueryPreprocessor(
                    httpClient,
                    ollamaUrl,
                    chatModel,
                    sp.GetRequiredService<ILogger<QueryPreprocessor>>()
                )
            );
        });

        return services;
    }

    // ── Diet Agent ────────────────────────────────────────────

    private static IServiceCollection AddDietAgent(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
            var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";

            return new DietValidationPlugin(
                httpClient,
                ollamaUrl,
                chatModel,
                sp.GetRequiredKeyedService<CircuitBreaker>("ollama"),
                sp.GetRequiredService<Tracing>(),
                sp.GetRequiredService<ILogger<DietValidationPlugin>>()
            );
        });

        return services;
    }

    // ── Meal Planner Agent ──────────────────────────────────────────
    private static IServiceCollection AddMealPlannerAgent(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.AddSingleton(sp => new MealPlannerPlugin(
            sp.GetRequiredService<RecipeSearchPlugin>(),
            sp.GetRequiredService<DietValidationPlugin>(),
            sp.GetRequiredService<ILogger<MealPlannerPlugin>>(),
            sp.GetRequiredService<SessionStore>()
        ));

        return services;
    }

    // ── Orchestrator ──────────────────────────────────────────

    private static IServiceCollection AddOrchestrator(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.AddSingleton<GuardrailAuditLog>();
        services.AddSingleton<RateLimiter>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new RateLimiter(
                sp.GetRequiredService<ILogger<RateLimiter>>(),
                maxRequestsPerMinute: config.GetValue<int>("RateLimiter:PerSessionLimit", 30)
            );
        });

        services.AddKeyedSingleton<CircuitBreaker>(
            "ollama",
            (sp, _) =>
                new CircuitBreaker(
                    sp.GetRequiredService<ILogger<CircuitBreaker>>(),
                    sp.GetRequiredService<GuardrailAuditLog>(),
                    failureThreshold: 3,
                    cooldownSeconds: 60
                )
        );

        services.AddKeyedSingleton<CircuitBreaker>(
            "redis",
            (sp, _) =>
                new CircuitBreaker(
                    sp.GetRequiredService<ILogger<CircuitBreaker>>(),
                    sp.GetRequiredService<GuardrailAuditLog>(),
                    failureThreshold: 3,
                    cooldownSeconds: 30
                )
        );
        services.AddSingleton<OutputGuard>();
        // IntentRouter — rules-based classifier, Month 2 will add LLM path
        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
            var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";

            return new IntentRouter(
                httpClient,
                ollamaUrl,
                chatModel,
                sp.GetRequiredKeyedService<CircuitBreaker>("ollama"),
                sp.GetRequiredService<SessionStore>(),
                sp.GetRequiredService<Tracing>(),
                sp.GetRequiredService<ILogger<IntentRouter>>()
            );
        });

        // AgentOrchestrator — resolves agents registered above
        services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
            var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";

            return new AgentOrchestrator(
                sp.GetRequiredService<RecipeSearchPlugin>(),
                sp.GetRequiredService<DietValidationPlugin>(),
                sp.GetRequiredService<MealPlannerPlugin>(),
                sp.GetRequiredService<SessionStore>(),
                httpClient,
                ollamaUrl,
                chatModel,
                sp.GetRequiredKeyedService<CircuitBreaker>("ollama"),
                sp.GetRequiredService<GuardrailAuditLog>(),
                sp.GetRequiredService<Tracing>(),
                sp.GetRequiredService<ILogger<AgentOrchestrator>>()
            );
        });

        return services;
    }

    // ── API Services ──────────────────────────────────────────

    private static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        services.AddSingleton<MetricsCollector>();
        // CORS — allow React dev
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
            );
        });

        services.AddHealthChecks();

        return services;
    }

    // ── Observability ──────────────────────────────────────────
    private static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        services.Configure<LangfuseOptions>(config.GetSection(LangfuseOptions.Section));

        services.AddHttpClient<Tracing>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<Tracing>();
        services.AddHostedService(sp => sp.GetRequiredService<Tracing>());

        return services;
    }
}
