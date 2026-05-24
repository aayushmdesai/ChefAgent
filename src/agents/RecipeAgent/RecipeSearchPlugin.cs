using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ChefAgent.Shared.Models;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using System.Net.Http.Json;

namespace ChefAgent.Agents.Recipe;

/// <summary>
/// Semantic Kernel plugin that performs vector search over the recipe corpus in Qdrant.
/// Embeds the query via Ollama, then searches Qdrant for similar recipes.
/// </summary>
public class RecipeSearchPlugin
{
    private readonly QdrantClient _qdrantClient;
    private readonly HttpClient _httpClient;
    private readonly string _collectionName;
    private readonly string _ollamaUrl;
    private readonly string _embeddingModel;
    private readonly ILogger<RecipeSearchPlugin> _logger;

    public RecipeSearchPlugin(
        QdrantClient qdrantClient,
        HttpClient httpClient,
        string ollamaUrl,
        string embeddingModel,
        string collectionName,
        ILogger<RecipeSearchPlugin> logger)
    {
        _qdrantClient = qdrantClient;
        _httpClient = httpClient;
        _ollamaUrl = ollamaUrl;
        _embeddingModel = embeddingModel;
        _collectionName = collectionName;
        _logger = logger;
    }

    [KernelFunction("search_recipes")]
    [Description("Search for recipes by a natural language query. Returns ranked recipes matching the search terms. Use this when the user wants to find recipes by name, ingredient, cuisine, or cooking style.")]
    public async Task<List<RecipeDocument>> SearchRecipesAsync(
        [Description("Natural language search query, e.g., 'quick chicken stir fry' or 'vegetarian pasta under 30 minutes'")] string query,
        [Description("Maximum number of results to return (default 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Searching recipes for query: {Query}, maxResults: {MaxResults}", query, maxResults);

        // 1. Embed the query via Ollama
        var queryVector = await GetEmbeddingAsync(query, cancellationToken);

        // 2. Search Qdrant
        var searchResult = await _qdrantClient.SearchAsync(
            collectionName: _collectionName,
            vector: queryVector,
            limit: (ulong)maxResults,
            cancellationToken: cancellationToken
        );

        // 3. Map results to RecipeDocuments
        var results = searchResult.Select(point =>
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
        }).ToList();

        _logger.LogInformation("Found {Count} recipes for query: {Query}", results.Count, query);
        return results;
    }

    [KernelFunction("search_by_ingredients")]
    [Description("Search for recipes that use specific ingredients. Use this when the user says 'what can I make with...' or lists ingredients they have on hand.")]
    public async Task<List<RecipeDocument>> SearchByIngredientsAsync(
        [Description("Comma-separated list of ingredients, e.g., 'chicken, garlic, soy sauce'")] string ingredients,
        [Description("Maximum number of results to return (default 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var query = $"recipes with {ingredients}";
        _logger.LogInformation("Searching by ingredients: {Ingredients}", ingredients);
        return await SearchRecipesAsync(query, maxResults, cancellationToken);
    }

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var request = new { model = _embeddingModel, input = $"search_query: {text}" };
        var response = await _httpClient.PostAsJsonAsync($"{_ollamaUrl}/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct);
        return result!.Embeddings[0];
    }

    private static string GetString(IDictionary<string, Value> payload, string key)
        => payload.TryGetValue(key, out var v) ? v.StringValue ?? "" : "";

    private static int GetInt(IDictionary<string, Value> payload, string key)
        => payload.TryGetValue(key, out var v) ? (int)v.IntegerValue : 0;

    private static List<string> GetStringList(IDictionary<string, Value> payload, string key)
    {
        if (!payload.TryGetValue(key, out var v) || v.ListValue == null) return [];
        return v.ListValue.Values.Select(x => x.StringValue ?? "").Where(s => s != "").ToList();
    }

    private record OllamaEmbedResponse(float[][] Embeddings);
}
