namespace ChefAgent.Shared.Providers.Llm;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// ILlmProvider implementation backed by Groq's OpenAI-compatible API.
/// Used in cloud deployment — free tier with generous rate limits.
///
/// Key differences from Ollama:
///   - Requires API key (Bearer token)
///   - Rate limiting returns 429 — retry with backoff, not circuit breaker
///   - Much faster than CPU Ollama (sub-second vs 14s+)
/// </summary>
public class GroqProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private const string GroqApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const int MaxRetries = 3;

    public GroqProvider(HttpClient httpClient, string apiKey, string model = "llama-3.2-3b-preview")
    {
        _httpClient = httpClient;
        _model = model;
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> ChatAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken ct = default
    )
    {
        var request = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            temperature = 0.3,
            max_tokens = 512,
        };

        // 429 = rate limit — retry with exponential backoff
        // Not circuit breaker — Groq is healthy, just throttling us
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await _httpClient.PostAsJsonAsync(GroqApiUrl, request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                await Task.Delay(delay, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Groq API error {(int)response.StatusCode}: {errorBody}"
                );
            }

            var result = await response.Content.ReadFromJsonAsync<GroqResponse>(
                cancellationToken: ct
            );
            return result?.Choices?[0]?.Message?.Content?.Trim() ?? string.Empty;
        }

        throw new InvalidOperationException(
            $"Groq rate limit exceeded after {MaxRetries} retries."
        );
    }

    public string ModelName => _model;

    // ── Response DTOs (OpenAI-compatible shape) ───────────────────────────────

    private record GroqResponse([property: JsonPropertyName("choices")] GroqChoice[]? Choices);

    private record GroqChoice([property: JsonPropertyName("message")] GroqMessage? Message);

    private record GroqMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );
}
