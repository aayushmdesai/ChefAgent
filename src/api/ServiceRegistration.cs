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
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Observability;
using ChefAgent.Shared.Providers.Embeddings;
using ChefAgent.Shared.Providers.Llm;
using Microsoft.Extensions.DependencyInjection;
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
        // ── HTTP Clients — registered first, used by providers below ─────────────
        var ollamaTimeout = config.GetValue<int>("Ollama:TimeoutSeconds", 120);
        services.AddHttpClient(
            "Ollama",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(ollamaTimeout);
            }
        );
        services.AddHttpClient(
            "Cloud",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }
        );

        // ── Qdrant — vector database ──────────────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var endpoint = config["Qdrant:Endpoint"] ?? "http://localhost:6334";
            var apiKey = config["Qdrant:ApiKey"];
            var uri = new Uri(endpoint);

            if (!string.IsNullOrEmpty(apiKey))
            {
                // Cloud — use host/port with https + apiKey
                return new QdrantClient(
                    host: uri.Host,
                    port: uri.Port,
                    https: true,
                    apiKey: apiKey
                );
            }

            // Local — plain gRPC no TLS
            return new QdrantClient(uri.Host, uri.Port);
        });
        // ── LLM Provider — config-driven, swap without code changes ──────────────
        var llmProvider = config["LlmProvider"] ?? "ollama";
        var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";

        services.AddSingleton<ILlmProvider>(sp =>
        {
            if (llmProvider == "groq")
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Cloud");
                var apiKey =
                    config["Groq:ApiKey"]
                    ?? throw new InvalidOperationException(
                        "Groq:ApiKey required when LlmProvider=groq"
                    );
                var model = config["Groq:Model"] ?? "llama-3.3-70b-versatile";
                return new GroqProvider(httpClient, apiKey, model);
            }

            var ollamaClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            return new OllamaLlmProvider(ollamaClient, ollamaUrl, chatModel);
        });

        // ── Embedding Provider — config-driven ───────────────────────────────────
        var embeddingProvider = config["EmbeddingProvider"] ?? "ollama";

        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            if (embeddingProvider == "huggingface")
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Cloud");
                var apiKey =
                    config["HuggingFace:ApiKey"]
                    ?? throw new InvalidOperationException(
                        "HuggingFace:ApiKey required when EmbeddingProvider=huggingface"
                    );
                var model = config["HuggingFace:Model"] ?? "nomic-ai/nomic-embed-text-v1";
                var baseUrl =
                    config["HuggingFace:BaseUrl"] ?? "https://api-inference.huggingface.co/models";
                return new HuggingFaceEmbeddingProvider(httpClient, apiKey, model, baseUrl);
            }

            if (embeddingProvider == "nomic")
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Cloud");
                var apiKey =
                    config["Nomic:ApiKey"]
                    ?? throw new InvalidOperationException(
                        "Nomic:ApiKey required when EmbeddingProvider=nomic"
                    );
                var model = config["Nomic:Model"] ?? "nomic-embed-text-v1";
                var baseUrl =
                    config["Nomic:BaseUrl"] ?? "https://api-atlas.nomic.ai/v1/embedding/text";
                return new NomicEmbeddingProvider(httpClient, apiKey, model, baseUrl);
            }

            if (embeddingProvider == "voyage")
            {
                var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Cloud");
                var apiKey =
                    config["Voyage:ApiKey"]
                    ?? throw new InvalidOperationException(
                        "Voyage:ApiKey required when EmbeddingProvider=voyage"
                    );
                var model = config["Voyage:Model"] ?? "voyage-4-lite";
                return new VoyageEmbeddingProvider(httpClient, apiKey, model);
            }
            var ollamaClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ollama");
            var embeddingModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
            return new OllamaEmbeddingProvider(ollamaClient, ollamaUrl, embeddingModel);
        });

        // Serialize enums as strings in JSON responses
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
            var tracing = sp.GetRequiredService<Tracing>();
            var ollamaUrl = config["Ollama:Endpoint"] ?? "http://localhost:11434";
            var embeddingModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
            var embeddingProvider = sp.GetRequiredService<IEmbeddingProvider>();
            var chatModel = config["Ollama:ChatModel"] ?? "llama3.2";
            var collection = config["Qdrant:CollectionName"] ?? "recipes";

            return new RecipeSearchPlugin(
                qdrant,
                httpClient,
                collection,
                logger,
                outputGuard,
                tracing,
                embeddingProvider,
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
            return new DietValidationPlugin(
                sp.GetRequiredService<ILlmProvider>(),
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
            return new AgentOrchestrator(
                sp.GetRequiredService<RecipeSearchPlugin>(),
                sp.GetRequiredService<DietValidationPlugin>(),
                sp.GetRequiredService<MealPlannerPlugin>(),
                sp.GetRequiredService<SessionStore>(),
                sp.GetRequiredService<ILlmProvider>(),
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
