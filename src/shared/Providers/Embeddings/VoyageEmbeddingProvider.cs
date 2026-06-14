namespace ChefAgent.Shared.Providers.Embeddings;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// IEmbeddingProvider backed by Voyage AI API.
/// Uses voyage-3-lite — 512 dimensions, optimised for retrieval.
/// Endpoint: https://api.voyageai.com/v1/embeddings
/// </summary>
public class VoyageEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private const string BaseUrl = "https://api.voyageai.com/v1/embeddings";

    public VoyageEmbeddingProvider(
        HttpClient httpClient,
        string apiKey,
        string model = "voyage-4-lite"
    )
    {
        _httpClient = httpClient;
        _model = model;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = new { model = _model, input = new[] { text } };

        const int maxRetries = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var response = await _httpClient.PostAsJsonAsync(BaseUrl, request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // respect Retry-After header if present, else back off
                var retryAfter =
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(20 * (attempt + 1));
                await Task.Delay(retryAfter, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<VoyageResponse>(
                cancellationToken: ct
            );
            return result!.Data[0].Embedding;
        }

        throw new InvalidOperationException($"Voyage API returned 429 after {maxRetries} retries.");
    }

    private record EmbeddingObject([property: JsonPropertyName("embedding")] float[] Embedding);

    private record VoyageResponse([property: JsonPropertyName("data")] EmbeddingObject[] Data);
}
