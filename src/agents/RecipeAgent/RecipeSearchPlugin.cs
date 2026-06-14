using System.ComponentModel;
using System.Net.Http.Json;
using ChefAgent.Shared;
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using ChefAgent.Shared.Providers.Embeddings;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace ChefAgent.Agents.Recipe;

/// <summary>
/// Semantic Kernel plugin that performs vector search over the recipe corpus in Qdrant.
/// Embeds the query via LLM, then searches Qdrant for similar recipes.
/// </summary>
public class RecipeSearchPlugin
{
    private readonly QdrantClient _qdrantClient;
    private readonly HttpClient _httpClient;
    private readonly string _collectionName;
    private readonly ILogger<RecipeSearchPlugin> _logger;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly OutputGuard _outputGuard;

    // Optional reranker plugin for LLM-based candidate reordering.
    private readonly RecipeReranker? _reranker;

    // Optional preprocessor plugin for negation parsing and query expansion.
    private readonly QueryPreprocessor? _preprocessor;
    private readonly Tracing _tracing;

    // In-memory embedding cache — query text → vector.
    // Model-specific: cleared on restart, which is correct (model changes invalidate cached vectors).
    // Size cap: 1000 entries × 768 floats × 4 bytes ≈ 3MB — negligible.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        float[]
    > _embeddingCache = new(StringComparer.OrdinalIgnoreCase);
    private const int EmbeddingCacheMaxSize = 1000;

    public RecipeSearchPlugin(
        QdrantClient qdrantClient,
        HttpClient httpClient,
        string collectionName,
        ILogger<RecipeSearchPlugin> logger,
        OutputGuard outputGuard,
        Tracing tracing,
        IEmbeddingProvider embeddingProvider,
        RecipeReranker? reranker = null,
        QueryPreprocessor? preprocessor = null
    )
    {
        _qdrantClient = qdrantClient;
        _httpClient = httpClient;
        _collectionName = collectionName;
        _logger = logger;
        _reranker = reranker;
        _preprocessor = preprocessor;
        _outputGuard = outputGuard;
        _tracing = tracing;
        _embeddingProvider = embeddingProvider;
    }

    [KernelFunction("search_recipes")]
    [Description(
        "Search for recipes by a natural language query. Returns ranked recipes matching the search terms. Use this when the user wants to find recipes by name, ingredient, cuisine, or cooking style."
    )]
    public async Task<List<RecipeDocument>> SearchRecipesAsync(
        [Description("Natural language search query")] string query,
        [Description("Maximum number of results to return (default 5)")] int maxResults = 5,
        [Description("Filter: max number of ingredients (null = no filter)")]
            int? maxIngredients = null,
        [Description("Filter: max number of steps (null = no filter)")] int? maxSteps = null,
        bool rerank = false,
        bool expand = false,
        CancellationToken cancellationToken = default,
        TraceContext? parentCtx = null
    )
    {
        using (_logger.BeginScope(new { CorrelationId = parentCtx?.CorrelationId ?? "none" }))
        {
            _logger.LogInformation(
                "Searching recipes for query: {Query}, maxResults: {MaxResults}",
                query,
                maxResults
            );

            // 1. Preprocess: parse negation (always) + expand abstract queries (opt-in)
            var searchQuery = query;
            List<string> excludedTerms = [];

            if (_preprocessor != null)
            {
                var preprocessed = await _preprocessor.PreprocessAsync(
                    query,
                    expand,
                    cancellationToken
                );
                searchQuery = preprocessed.SearchQuery;
                excludedTerms = preprocessed.ExcludedTerms;
            }

            // 2. Embed the cleaned/expanded query
            var queryVector = await GetEmbeddingAsync(searchQuery, cancellationToken, parentCtx);

            // 3. Build optional Qdrant filter
            Filter? filter = null;
            var conditions = new List<Condition>();

            if (maxIngredients.HasValue)
            {
                conditions.Add(
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "ingredient_count",
                            Range = new Qdrant.Client.Grpc.Range { Lte = maxIngredients.Value },
                        },
                    }
                );
            }

            if (maxSteps.HasValue)
            {
                conditions.Add(
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key = "step_count",
                            Range = new Qdrant.Client.Grpc.Range { Lte = maxSteps.Value },
                        },
                    }
                );
            }

            if (conditions.Count > 0)
            {
                filter = new Filter();
                filter.Must.AddRange(conditions);
            }

            if (filter != null)
                _logger.LogInformation(
                    "Applying filters — maxIngredients: {MaxIng}, maxSteps: {MaxSteps}",
                    maxIngredients,
                    maxSteps
                );

            // Fetch extra candidates if re-ranking or if negation may remove some
            var fetchLimit = (_reranker != null && rerank) ? maxResults + 5 : maxResults;
            if (excludedTerms.Count > 0)
                fetchLimit += 10; // buffer for negation removals

            var searchResult = await _qdrantClient.SearchAsync(
                collectionName: _collectionName,
                vector: queryVector,
                filter: filter,
                limit: (ulong)fetchLimit,
                cancellationToken: cancellationToken
            );
            // 4. Map results to RecipeDocuments
            var results = searchResult
                .Select(point =>
                {
                    var payload = point.Payload;
                    return new RecipeDocument
                    {
                        Id = GetString(payload, "doc_id"),
                        Title = GetString(payload, "title"),
                        Ingredients = GetStringList(payload, "ingredients"),
                        Directions = GetStringList(payload, "directions"),
                        Source = GetString(payload, "source"),
                        SourceUrl = GetString(payload, "source_url"),
                        IngredientCount = GetInt(payload, "ingredient_count"),
                        StepCount = GetInt(payload, "step_count"),
                        RelevanceScore = point.Score,
                    };
                })
                .Where(r => _outputGuard.IsRecipeSane(r, r.RelevanceScore)) // ← here, after mapping
                .ToList();

            // 5. Filter negation violations
            if (_preprocessor != null && excludedTerms.Count > 0)
            {
                results = _preprocessor.FilterNegations(results, excludedTerms);
            }

            // 6. Re-rank if enabled
            if (_reranker != null && rerank && results.Count > 0)
            {
                results = await _reranker.RerankAsync(
                    query,
                    results,
                    maxResults,
                    cancellationToken,
                    parentCtx
                );
            }
            else
            {
                // Preserve the original vector similarity ranking when reranking is not requested.
                results = results.Take(maxResults).ToList();
            }

            _logger.LogInformation(
                "Found {Count} recipes for query: {Query}",
                results.Count,
                query
            );
            return results;
        }
    }

    [KernelFunction("search_by_ingredients")]
    [Description(
        "Search for recipes that use specific ingredients. Use this when the user says 'what can I make with...' or lists ingredients they have on hand."
    )]
    public async Task<List<RecipeDocument>> SearchByIngredientsAsync(
        [Description("Comma-separated list of ingredients, e.g., 'chicken, garlic, soy sauce'")]
            string ingredients,
        [Description("Maximum number of results to return (default 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default
    )
    {
        var query = $"recipes with {ingredients}";
        _logger.LogInformation("Searching by ingredients: {Ingredients}", ingredients);
        return await SearchRecipesAsync(
            query,
            maxResults,
            null,
            null,
            false,
            false,
            cancellationToken
        );
    }

    private async Task<float[]> GetEmbeddingAsync(
        string text,
        CancellationToken ct,
        TraceContext? parentCtx = null
    )
    {
        // Cache hit — skip embedding provider entirely
        if (_embeddingCache.TryGetValue(text, out var cached))
        {
            _logger.LogInformation(
                "[EmbeddingCache] Hit — skipping embedding for: \"{Text}\" (cache size: {Size})",
                text,
                _embeddingCache.Count
            );

            if (parentCtx is not null)
            {
                var hitCtx = _tracing.StartSpan(
                    parentCtx,
                    "embed.cache_hit",
                    input: text,
                    metadata: new() { ["cache_size"] = _embeddingCache.Count }
                );
                _tracing.EndSpan(hitCtx, output: "cache_hit", statusMessage: "ok");
            }

            return cached;
        }

        // Cache miss — call embedding provider (LLM or HuggingFace)
        var missCtx = parentCtx is not null
            ? _tracing.StartSpan(parentCtx, "embed.provider", input: text)
            : TraceContext.None;

        var vector = await _embeddingProvider.EmbedAsync(text, ct);

        if (!missCtx.IsNone)
            _tracing.EndSpan(
                missCtx,
                output: "embedded",
                statusMessage: "ok",
                metadata: new() { ["dims"] = vector.Length, ["cache_size"] = _embeddingCache.Count }
            );

        // Store in cache
        if (_embeddingCache.Count < EmbeddingCacheMaxSize)
            _embeddingCache[text] = vector;

        _logger.LogInformation(
            "[EmbeddingCache] Miss — embedded via provider: \"{Text}\" (cache size: {Size})",
            text,
            _embeddingCache.Count
        );

        return vector;
    }

    private static string GetString(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var v) ? v.StringValue ?? "" : "";

    private static int GetInt(IDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var v) ? (int)v.IntegerValue : 0;

    private static List<string> GetStringList(IDictionary<string, Value> payload, string key)
    {
        // Safely extract a list of string values from the payload field.
        if (!payload.TryGetValue(key, out var v) || v.ListValue == null)
            return [];
        return v.ListValue.Values.Select(x => x.StringValue ?? "").Where(s => s != "").ToList();
    }
}
