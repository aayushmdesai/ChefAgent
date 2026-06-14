namespace ChefAgent.Shared.Providers.Embeddings;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// IEmbeddingProvider implementation backed by Ollama's /api/embed endpoint.
/// Used when running locally.
///
/// Handles nomic-embed-text prefix convention internally —
/// callers pass clean text, provider adds "search_query:" prefix.
/// </summary>
public class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaEmbeddingProvider(
        HttpClient httpClient,
        string baseUrl,
        string model = "nomic-embed-text"
    )
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new
        {
            model = _model,
            input = $"search_query: {text}" // nomic-embed-text convention
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/embed", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
            cancellationToken: ct
        );
        return result!.Embeddings[0];
    }

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][] Embeddings
    );
}
