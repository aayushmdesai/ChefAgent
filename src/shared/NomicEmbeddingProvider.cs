namespace ChefAgent.Shared;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// IEmbeddingProvider backed by Nomic Atlas API.
/// Uses nomic-embed-text-v1 — identical model to local Ollama.
/// Zero vector space mismatch: stored vectors and query vectors use the same model.
/// Endpoint: https://api-atlas.nomic.ai/v1/embedding/text
/// </summary>
public class NomicEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _baseUrl;

    public NomicEmbeddingProvider(
        HttpClient httpClient,
        string apiKey,
        string model = "nomic-embed-text-v1",
        string baseUrl = "https://api-atlas.nomic.ai/v1/embedding/text"
    )
    {
        _httpClient = httpClient;
        _model = model;
        _baseUrl = baseUrl;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new
        {
            model = _model,
            texts = new[] { $"search_query: {text}" }, // nomic prefix convention
        };

        var response = await _httpClient.PostAsJsonAsync(_baseUrl, request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<NomicResponse>(cancellationToken: ct);
        return result!.Embeddings[0];
    }

    private record NomicResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);
}
