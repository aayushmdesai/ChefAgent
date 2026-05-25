using System.Net.Http.Json;
using System.Text.Json;
using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Agents.Recipe;

/// <summary>
/// Re-ranks recipe search candidates using Ollama LLM to judge relevance.
/// Takes top N vector search results and asks the LLM to order them by true relevance to the query.
/// </summary>
public class RecipeReranker
{
    private readonly HttpClient _httpClient;
    private readonly string _ollamaUrl;
    private readonly string _chatModel;
    private readonly ILogger<RecipeReranker> _logger;

    public RecipeReranker(
        HttpClient httpClient,
        string ollamaUrl,
        string chatModel,
        ILogger<RecipeReranker> logger
    )
    {
        _httpClient = httpClient;
        _ollamaUrl = ollamaUrl;
        _chatModel = chatModel;
        _logger = logger;
    }

    /// <summary>
    /// Re-ranks candidates by asking the LLM to judge relevance to the query.
    /// Falls back to original vector search order if LLM response can't be parsed.
    /// </summary>
    public async Task<List<RecipeDocument>> RerankAsync(
        string query,
        List<RecipeDocument> candidates,
        int topN = 5,
        CancellationToken ct = default
    )
    {
        if (candidates.Count <= 1)
            return candidates;

        // Build a ranking prompt and ask Ollama to score each candidate.
        var prompt = BuildPrompt(query, candidates);

        _logger.LogInformation(
            "Re-ranking {Count} candidates for query: {Query}",
            candidates.Count,
            query
        );

        try
        {
            var llmResponse = await CallOllamaAsync(prompt, ct);
            var ranked = ParseRanking(llmResponse, candidates);

            _logger.LogInformation(
                "Re-ranking complete. Top result: {Title}",
                ranked.FirstOrDefault()?.Title ?? "none"
            );

            return ranked.Take(topN).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Re-ranking failed, falling back to original vector search order"
            );
            return candidates.Take(topN).ToList();
        }
    }

    private static string BuildPrompt(string query, List<RecipeDocument> candidates)
    {
        // The prompt explicitly requests JSON-only output so parsing remains deterministic.
        var sb = new System.Text.StringBuilder();

        sb.AppendLine(
            "You are a recipe relevance judge. Given a search query and candidate recipes, rank them by relevance."
        );
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Consider how well each recipe matches the query intent");
        sb.AppendLine(
            "- A query like \"simple chicken\" favors few ingredients and chicken as the main protein"
        );
        sb.AppendLine(
            "- A query like \"Mexican food\" favors recipes with Mexican ingredients and techniques"
        );
        sb.AppendLine("- Penalize recipes that match keywords but miss the actual intent");
        sb.AppendLine();
        sb.AppendLine($"Query: \"{query}\"");
        sb.AppendLine();
        sb.AppendLine("Candidates:");

        for (int i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];
            var ingredients = string.Join(", ", r.Ingredients.Take(5));
            sb.AppendLine($"[{i}] \"{r.Title}\" — Ingredients: {ingredients}");
        }

        sb.AppendLine();
        sb.AppendLine(
            "Return ONLY a JSON array of objects with \"index\" (int) and \"score\" (0.0–1.0), ordered by most relevant first."
        );
        sb.AppendLine("Example: [{\"index\": 2, \"score\": 0.95}, {\"index\": 0, \"score\": 0.7}]");
        sb.AppendLine("No explanation, no markdown, just the JSON array.");

        return sb.ToString();
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken ct)
    {
        var request = new
        {
            model = _chatModel,
            messages = new[] { new { role = "user", content = prompt } },
            stream = false,
        };

        var response = await _httpClient.PostAsJsonAsync($"{_ollamaUrl}/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(ct);
        return result?.Message?.Content ?? "";
    }

    private List<RecipeDocument> ParseRanking(string llmResponse, List<RecipeDocument> candidates)
    {
        // Strip markdown fences if the LLM wraps the JSON response in code blocks.
        var json = llmResponse.Replace("```json", "").Replace("```", "").Trim();

        var rankings = JsonSerializer.Deserialize<List<RankingEntry>>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        if (rankings == null || rankings.Count == 0)
            throw new InvalidOperationException("LLM returned empty ranking");

        var ranked = new List<RecipeDocument>();

        foreach (var entry in rankings)
        {
            if (entry.Index >= 0 && entry.Index < candidates.Count)
            {
                var recipe = candidates[entry.Index];
                // Overwrite the vector score with the LLM relevance score
                ranked.Add(recipe with { RelevanceScore = entry.Score });
            }
            else
            {
                _logger.LogWarning("LLM returned out-of-range index: {Index}", entry.Index);
            }
        }

        // Append any candidates the LLM missed (preserving original order)
        // Ensure any candidates omitted by the LLM are still present in the final ranked list.
        var rankedIds = ranked.Select(r => r.Id).ToHashSet();
        foreach (var candidate in candidates)
        {
            if (!rankedIds.Contains(candidate.Id))
                ranked.Add(candidate with { RelevanceScore = 0.0 });
        }

        return ranked;
    }

    private record RankingEntry(int Index, double Score);

    private record OllamaChatResponse(OllamaChatMessage Message);

    private record OllamaChatMessage(string Role, string Content);
}
