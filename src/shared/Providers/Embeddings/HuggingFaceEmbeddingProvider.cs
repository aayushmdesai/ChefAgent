namespace ChefAgent.Shared.Providers.Embeddings;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// IEmbeddingProvider implementation backed by HuggingFace Inference API.
/// Used in cloud deployment — free tier, no GPU required.
///
/// Model: nomic-ai/nomic-embed-text-v1
/// Same model as local Ollama — vector space is identical, no compatibility issues.
/// Handles search_query: prefix internally, same as OllamaEmbeddingProvider.
///
/// Free tier note: model may cold-start (10-20s first call).
/// Pre-warm on API startup eliminates cold start for real user requests.
/// </summary>
public class HuggingFaceEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _baseUrl;

    public HuggingFaceEmbeddingProvider(
        HttpClient httpClient,
        string apiKey,
        string model = "nomic-ai/nomic-embed-text-v1",
        string baseUrl = "https://api-inference.huggingface.co/models"
    )
    {
        _httpClient = httpClient;
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new
        {
            inputs = $"search_query: {text}", // nomic-embed-text convention
            options = new { wait_for_model = true }, // blocks until model warm, avoids 503
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/{_model}", request, ct);
        response.EnsureSuccessStatusCode();

        // HuggingFace returns float[][] for batch or float[] for single
        // We send one input so expect float[] directly
        var result = await response.Content.ReadFromJsonAsync<float[]>(cancellationToken: ct);
        return result!;
    }
}
