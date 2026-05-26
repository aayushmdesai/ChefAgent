using System.Text.Json.Serialization;
using ChefAgent.Agents.Diet;
using ChefAgent.Agents.Orchestrator;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared.Models;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// --- Service Registration ---

// Qdrant vector database
builder.Services.AddSingleton(sp =>
{
    var endpoint = builder.Configuration["Qdrant:Endpoint"] ?? "http://localhost:6334";
    var uri = new Uri(endpoint);
    return new QdrantClient(uri.Host, uri.Port);
});

// HTTP client for Ollama
builder.Services.AddHttpClient(
    "Ollama",
    client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    }
);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Recipe Agent
builder.Services.AddSingleton(sp =>
{
    var qdrant = sp.GetRequiredService<QdrantClient>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("Ollama");
    var logger = sp.GetRequiredService<ILogger<RecipeSearchPlugin>>();

    var ollamaUrl = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
    var embeddingModel = builder.Configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "llama3.2";
    var collection = builder.Configuration["Qdrant:CollectionName"] ?? "recipes";

    var reranker = new RecipeReranker(
        httpClient,
        ollamaUrl,
        chatModel,
        sp.GetRequiredService<ILogger<RecipeReranker>>()
    );
    var preprocessor = new QueryPreprocessor(
        httpClient,
        ollamaUrl,
        chatModel,
        sp.GetRequiredService<ILogger<QueryPreprocessor>>()
    );

    return new RecipeSearchPlugin(
        qdrant,
        httpClient,
        ollamaUrl,
        embeddingModel,
        collection,
        logger,
        reranker,
        preprocessor
    );
});

// Diet Agent
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("Ollama");
    var ollamaUrl = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
    var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "llama3.2";
    return new DietValidationPlugin(
        httpClient,
        ollamaUrl,
        chatModel,
        sp.GetRequiredService<ILogger<DietValidationPlugin>>()
    );
});

// Intent Router
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("Ollama");
    var ollamaUrl = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
    var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "llama3.2";
    return new IntentRouter(
        httpClient,
        ollamaUrl,
        chatModel,
        sp.GetRequiredService<ILogger<IntentRouter>>()
    );
});

// Agent Orchestrator — depends on Recipe + Diet agents (already registered above)
builder.Services.AddSingleton(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("Ollama");
    var ollamaUrl = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
    var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "llama3.2";
    return new AgentOrchestrator(
        sp.GetRequiredService<RecipeSearchPlugin>(),
        sp.GetRequiredService<DietValidationPlugin>(),
        httpClient,
        ollamaUrl,
        chatModel,
        sp.GetRequiredService<ILogger<AgentOrchestrator>>()
    );
});

// CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy
            .WithOrigins("http://localhost:3000", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
    );
});

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors();

// --- Endpoints ---

app.MapHealthChecks("/health");

app.MapGet(
    "/",
    () =>
        new
        {
            service = "ChefAgent API",
            version = "0.1.0",
            status = "running",
            stack = new
            {
                vectorDb = "Qdrant",
                llm = "Ollama",
                memory = "Redis",
            },
            agents = new[] { "recipe", "diet", "orchestrator", "planner (coming soon)" },
        }
);

// Recipe search endpoint — no dietary validation
app.MapPost(
    "/recipes/search",
    async (RecipeSearchRequest request, RecipeSearchPlugin plugin) =>
    {
        var results = await plugin.SearchRecipesAsync(
            request.Query,
            request.MaxResults,
            request.MaxIngredients,
            request.MaxSteps,
            request.Rerank,
            request.Expand
        );
        return Results.Ok(
            new
            {
                query = request.Query,
                count = results.Count,
                recipes = results,
            }
        );
    }
);

// Validated recipe search endpoint — Recipe Agent + Diet Agent combined
app.MapPost(
    "/recipes/search-validated",
    async (
        RecipeSearchRequest request,
        RecipeSearchPlugin recipePlugin,
        DietValidationPlugin dietPlugin
    ) =>
    {
        var results = await recipePlugin.SearchRecipesAsync(
            request.Query,
            request.MaxResults,
            request.MaxIngredients,
            request.MaxSteps,
            request.Rerank,
            request.Expand
        );

        var validated = new List<object>();
        foreach (var recipe in results)
        {
            var validation = await dietPlugin.ValidateRecipeAsync(recipe, request.DietaryProfile);
            validated.Add(
                new
                {
                    recipe,
                    dietary = new
                    {
                        isCompatible = validation.IsCompatible,
                        violations = validation.Violations,
                        substitutions = validation.Substitutions,
                        explanation = validation.Explanation,
                    },
                }
            );
        }

        var sorted = validated.OrderByDescending(v => ((dynamic)v).dietary.isCompatible).ToList();

        return Results.Ok(
            new
            {
                query = request.Query,
                count = sorted.Count,
                profileApplied = request.DietaryProfile is not null,
                recipes = sorted,
            }
        );
    }
);

// Chat endpoint — Orchestrator routes natural language to right agent(s)
app.MapPost(
    "/chat",
    async (ChatRequest request, IntentRouter intentRouter, AgentOrchestrator orchestrator) =>
    {
        // Step 1: Classify intent + extract entities from message
        // UseLlmClassification=false (default) → rules only, fast, no timeouts
        // UseLlmClassification=true → LLM classification, smart but slow on CPU
        ClassifiedIntent classified;
        if (request.UseLlmClassification)
        {
            classified = await intentRouter.ClassifyAsync(request.Message, request.DietaryProfile);
        }
        else
        {
            // Rules-only path — instant, no LLM
            classified = await intentRouter.ClassifyAsync(request.Message, request.DietaryProfile);
            // If rules couldn't classify and LLM is disabled, stay Unknown
            // AgentOrchestrator handles Unknown gracefully
        }

        // Step 2: Route to right agent(s) and build response
        var response = await orchestrator.RouteAsync(classified);

        return Results.Ok(response);
    }
);

app.Run();

// --- Request DTOs ---

record RecipeSearchRequest(
    string Query,
    int MaxResults = 5,
    int? MaxIngredients = null,
    int? MaxSteps = null,
    bool Rerank = false,
    bool Expand = false,
    DietaryProfile? DietaryProfile = null
);

record ChatRequest(
    string Message,
    string? SessionId = null, // unused in MVP, reserved for Month 2 Redis memory
    DietaryProfile? DietaryProfile = null,
    bool UseLlmClassification = false // false = rules only (fast), true = LLM (smart, slow on CPU)
);
