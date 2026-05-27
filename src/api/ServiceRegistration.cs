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
        services.AddHttpClient(
            "Ollama",
            client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
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
        services.AddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(connectionString)
        );
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
                new RecipeReranker(
                    httpClient,
                    ollamaUrl,
                    chatModel,
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
                httpClient,
                ollamaUrl,
                chatModel,
                sp.GetRequiredService<ILogger<AgentOrchestrator>>()
            );
        });

        return services;
    }

    // ── API Services ──────────────────────────────────────────

    private static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        // CORS — allow React dev server on 3000 and Vite on 5173
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy
                    .WithOrigins("http://localhost:3000", "http://localhost:5173")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
            );
        });

        services.AddHealthChecks();

        return services;
    }
}
