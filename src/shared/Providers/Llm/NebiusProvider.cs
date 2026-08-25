namespace ChefAgent.Shared.Providers.Llm;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Result of a single LLM call, carrying the metrics the benchmark needs.
/// Only the harness uses this — agents keep calling ChatAsync and are unchanged.
/// </summary>
public record LlmCallResult(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens, // prompt_tokens_details.cached_tokens
    double TtftMs, // -1 when non-streaming
    double TotalMs,
    int StatusCode,
    int RetryCount,
    int RateLimitHits, // 429s absorbed — reported, never hidden
    string? Error
);

/// <summary>
/// ILlmProvider backed by Nebius Token Factory's OpenAI-compatible API.
///
/// Verified against the live API:
///   - Endpoint is /v1/chat/completions. The model catalog page steers you to
///     /v1/responses instead — different surface, different stream event shape.
///   - Plain-string message content works; the array form shown in their
///     example is not required for text-only.
///   - stream_options.include_usage is honored: the final SSE frame carries
///     usage with an empty choices array.
///   - Prompt caching is ON by default, reported as
///     prompt_tokens_details.cached_tokens. This MUST be recorded — repeated
///     prompts across runs get cache hits that inflate later configs.
///   - Response carries vLLM-flavored extras (prompt_token_ids, stop_reason,
///     kv_transfer_params) outside the OpenAI spec. Harmless; we deserialize
///     selectively. Detail objects can be null — guard every hop.
/// </summary>
public class NebiusProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey; // ← add this field
    private readonly string _model;
    private readonly string _endpoint;
    private const int MaxRetries = 3;

    public const string DefaultBaseUrl = "https://api.tokenfactory.nebius.com/v1";

    public NebiusProvider(
        HttpClient httpClient,
        string apiKey,
        string model = "meta-llama/Llama-3.3-70B-Instruct",
        string? baseUrl = null
    )
    {
        _httpClient = httpClient;
        _apiKey = apiKey; 
        _model = model;
        _endpoint = $"{(baseUrl ?? DefaultBaseUrl).TrimEnd('/')}/chat/completions";
        // No mutation of the shared "Cloud" client — auth is set per request,
        // and the timeout stays whatever the HttpClientFactory configured (30s).
    }

    public string ModelName => _model;

    /// <summary>Production path. Unchanged contract — agents call this.</summary>
    public async Task<string> ChatAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken ct = default
    )
    {
        var result = await ChatWithMetricsAsync(messages, stream: false, ct);
        if (result.Error is not null)
            throw new HttpRequestException($"Nebius API error {result.StatusCode}: {result.Error}");
        return result.Content;
    }

    /// <summary>
    /// Benchmark path. Set stream: true to measure time-to-first-token.
    /// Never throws — errors are returned as data so the harness records them
    /// instead of losing the sample.
    /// </summary>
    public async Task<LlmCallResult> ChatWithMetricsAsync(
        IEnumerable<ChatMessage> messages,
        bool stream = true,
        CancellationToken ct = default
    )
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }),
            ["temperature"] = 0.3,
            ["max_tokens"] = 512,
            ["stream"] = stream,
        };

        if (stream)
            payload["stream_options"] = new { include_usage = true };

        var sw = Stopwatch.StartNew();
        int rateLimitHits = 0;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = JsonContent.Create(payload),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var response = await _httpClient.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct
                );

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    rateLimitHits++;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    return Fail(
                        (int)response.StatusCode,
                        attempt,
                        rateLimitHits,
                        sw,
                        Truncate(body)
                    );
                }

                return stream
                    ? await ReadStreamAsync(response, sw, attempt, rateLimitHits, ct)
                    : await ReadWholeAsync(response, sw, attempt, rateLimitHits, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail(-1, attempt, rateLimitHits, sw, "timeout");
            }
            catch (Exception ex)
            {
                return Fail(-1, attempt, rateLimitHits, sw, Truncate(ex.Message));
            }
        }

        return Fail(429, MaxRetries, rateLimitHits, sw, "rate limited after retries");
    }

    private static LlmCallResult Fail(
        int status,
        int attempt,
        int rateLimitHits,
        Stopwatch sw,
        string error
    ) => new("", 0, 0, 0, -1, sw.Elapsed.TotalMilliseconds, status, attempt, rateLimitHits, error);

    private static async Task<LlmCallResult> ReadStreamAsync(
        HttpResponseMessage response,
        Stopwatch sw,
        int attempt,
        int rateLimitHits,
        CancellationToken ct
    )
    {
        double ttft = -1;
        var content = new System.Text.StringBuilder();
        int promptTokens = 0,
            completionTokens = 0,
            cachedTokens = 0;

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // The usage frame arrives with choices: [] — this guard skips the
            // delta branch correctly and falls through to usage below.
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (
                    choices[0].TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.String
                )
                {
                    var text = c.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Stamp on the first token carrying text, not the first
                        // SSE frame — role-only deltas arrive first.
                        if (ttft < 0)
                            ttft = sw.Elapsed.TotalMilliseconds;
                        content.Append(text);
                    }
                }
            }

            if (
                root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
            )
            {
                promptTokens = ReadInt(usage, "prompt_tokens");
                completionTokens = ReadInt(usage, "completion_tokens");
                cachedTokens = ReadCached(usage);
            }
        }

        return new LlmCallResult(
            content.ToString().Trim(),
            promptTokens,
            completionTokens,
            cachedTokens,
            ttft,
            sw.Elapsed.TotalMilliseconds,
            200,
            attempt,
            rateLimitHits,
            null
        );
    }

    private static async Task<LlmCallResult> ReadWholeAsync(
        HttpResponseMessage response,
        Stopwatch sw,
        int attempt,
        int rateLimitHits,
        CancellationToken ct
    )
    {
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct
        );
        var root = doc.RootElement;

        var text = "";
        if (
            root.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var c)
            && c.ValueKind == JsonValueKind.String
        )
        {
            text = c.GetString()?.Trim() ?? "";
        }

        int prompt = 0,
            completion = 0,
            cached = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = ReadInt(usage, "prompt_tokens");
            completion = ReadInt(usage, "completion_tokens");
            cached = ReadCached(usage);
        }

        return new LlmCallResult(
            text,
            prompt,
            completion,
            cached,
            -1,
            sw.Elapsed.TotalMilliseconds,
            200,
            attempt,
            rateLimitHits,
            null
        );
    }

    // completion_tokens_details came back null on the non-streaming call, so
    // assume any details object can be null and guard every hop.
    private static int ReadCached(JsonElement usage) =>
        usage.TryGetProperty("prompt_tokens_details", out var d)
        && d.ValueKind == JsonValueKind.Object
            ? ReadInt(d, "cached_tokens")
            : 0;

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
