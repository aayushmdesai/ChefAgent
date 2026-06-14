namespace ChefAgent.Shared.Providers.Llm;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>
/// ILlmProvider implementation backed by Ollama's /api/chat endpoint.
/// Used when running locally — no API key required.
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _baseUrl;

    public OllamaLlmProvider(HttpClient httpClient, string baseUrl, string model)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public async Task<string> ChatAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken ct = default
    )
    {
        var request = new
        {
            model = _model,
            stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken: ct
        );
        return result?.Message?.Content?.Trim() ?? string.Empty;
    }

    public string ModelName => _model;

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaChatMessage? Message
    );

    private record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );
}
