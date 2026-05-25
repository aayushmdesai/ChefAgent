using ChefAgent.Agents.Recipe;
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
// Use an increased timeout for embedding and chart calls, which can take several seconds.
builder.Services.AddHttpClient(
    "Ollama",
    client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    }
);

// Recipe Agent
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

    // Create the recipe search plugin with optional LLM-powered preprocessing and reranking.
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
            agents = new[] { "recipe", "diet (coming soon)", "planner (coming soon)" },
        }
);

// Recipe search endpoint
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

// Chat endpoint — will wire to Orchestrator in Week 4
app.MapPost(
    "/chat",
    (ChatRequest request) =>
    {
        // TODO: Route through Orchestrator -> appropriate agent(s)
        return Results.Ok(
            new
            {
                message = $"Received: {request.Message}. Orchestrator coming in Week 4!",
                intent = "unknown",
            }
        );
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
    bool Expand = false
);

record ChatRequest(string Message, string? SessionId = null);
