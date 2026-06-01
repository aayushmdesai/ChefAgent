using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChefAgent.Shared;

/// <summary>
/// The ONLY class in ChefAgent that knows Langfuse exists.
///
/// Design principles:
///
/// 1. NEVER block the request thread.
///    Every public method writes to a Channel and returns immediately.
///    A background worker (IHostedService) reads from the channel and
///    POSTs to the Langfuse REST API. Langfuse latency is invisible to users.
///
/// 2. NEVER throw to callers.
///    Every method is wrapped in try/catch. If Langfuse is unreachable,
///    the channel write silently drops the event. The recipe search succeeds.
///
/// 3. Disabled = zero overhead.
///    When Langfuse__Enabled is false, all methods return immediately
///    without touching the channel or allocating any objects.
///
/// Why Langfuse REST API directly instead of an official SDK?
///   At time of implementation, no official .NET SDK existed for Langfuse.
///   The REST API is simple and well-documented — a POST to /api/public/ingestion
///   with a batch of typed event payloads. This is more transparent than a
///   black-box SDK and teaches the underlying protocol.
///
/// Why Langfuse over alternatives?
///   - LangSmith: not open-source, requires Enterprise license for self-hosting
///   - OpenTelemetry + Jaeger: no first-class LLM generation concept (prompt/completion/tokens)
///   - Arize Phoenix: smaller ecosystem, less .NET integration at time of decision
///   See ADR-010 for full comparison.
///
/// Why self-hosted v2 over v3?
///   v3 adds ClickHouse + MinIO + worker container (~1.2-1.5GB RAM overhead).
///   v2 needs only Postgres + server (~650MB). On an 8GB dev machine with
///   Qdrant + Redis + Ollama already running, v3 exceeds available headroom.
///   See ADR-010 for full RAM budget analysis.
/// </summary>
public sealed class Tracing : IHostedService, IDisposable
{
    private readonly LangfuseOptions _options;
    private readonly ILogger<Tracing> _logger;
    private readonly HttpClient _http;

    // Bounded channel — if the worker falls behind, new events are dropped
    // rather than consuming unbounded memory. 5000 events is generous headroom.
    private readonly Channel<LangfuseEvent> _channel = Channel.CreateBounded<LangfuseEvent>(
        new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        }
    );

    private Task? _workerTask;
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Tracing(IOptions<LangfuseOptions> options, ILogger<Tracing> logger, HttpClient http)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        if (_options.Enabled)
        {
            // Basic auth: base64(publicKey:secretKey)
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.PublicKey}:{_options.SecretKey}")
            );
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
            _http.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
            _workerTask = RunWorkerAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        if (_workerTask is not null)
            await _workerTask.ConfigureAwait(false);
    }

    public void Dispose() => _cts.Dispose();

    // ── Public API — called by ChatController + agents ────────────────────────

    /// <summary>
    /// Creates a new root-level trace for a /chat request.
    /// Returns a TraceContext carrying the traceId + initial spanId.
    /// Call this once per request at the controller level.
    /// </summary>
    public TraceContext StartTrace(string name, string sessionId, string input)
    {
        if (!_options.Enabled)
            return TraceContext.None;

        try
        {
            var traceId = NewId();
            var spanId = NewId();

            Enqueue(
                new LangfuseEvent
                {
                    Type = "trace-create",
                    Body = new
                    {
                        id = traceId,
                        name,
                        sessionId,
                        input,
                        timestamp = DateTime.UtcNow,
                    },
                }
            );

            // Root span — covers the entire request
            Enqueue(
                new LangfuseEvent
                {
                    Type = "span-create",
                    Body = new
                    {
                        id = spanId,
                        traceId,
                        name,
                        startTime = DateTime.UtcNow,
                        input,
                    },
                }
            );

            return new TraceContext { TraceId = traceId, SpanId = spanId };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracing.StartTrace failed silently");
            return TraceContext.None;
        }
    }

    /// <summary>
    /// Starts a child span. Returns a new TraceContext with the child's spanId.
    /// Pass this child context to any sub-components so they attach under this span.
    ///
    /// Usage:
    ///   var childCtx = _tracing.StartSpan(ctx, "RecipeSearch", query);
    ///   // ... do work ...
    ///   _tracing.EndSpan(childCtx, result);
    /// </summary>
    public TraceContext StartSpan(
        TraceContext parent,
        string name,
        object? input = null,
        Dictionary<string, object>? metadata = null
    )
    {
        if (!_options.Enabled || parent.IsNone)
            return TraceContext.None;

        try
        {
            var spanId = NewId();

            Enqueue(
                new LangfuseEvent
                {
                    Type = "span-create",
                    Body = new
                    {
                        id = spanId,
                        traceId = parent.TraceId,
                        parentObservationId = parent.SpanId,
                        name,
                        startTime = DateTime.UtcNow,
                        input,
                        metadata,
                    },
                }
            );

            return parent.WithNewSpan(spanId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracing.StartSpan failed silently");
            return TraceContext.None;
        }
    }

    /// <summary>
    /// Ends a span. Attach the output and any metadata produced by the operation.
    /// statusMessage: "ok", "error", "skipped", "circuit_open", etc.
    /// </summary>
    public void EndSpan(
        TraceContext ctx,
        object? output = null,
        string? statusMessage = null,
        Dictionary<string, object>? metadata = null
    )
    {
        if (!_options.Enabled || ctx.IsNone)
            return;

        try
        {
            Enqueue(
                new LangfuseEvent
                {
                    Type = "span-update",
                    Body = new
                    {
                        id = ctx.SpanId,
                        traceId = ctx.TraceId,
                        endTime = DateTime.UtcNow,
                        output,
                        statusMessage,
                        metadata,
                    },
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracing.EndSpan failed silently");
        }
    }

    /// <summary>
    /// Logs an LLM generation — the most important event type in Langfuse.
    /// Captures the full input/output of an Ollama call: prompt, completion,
    /// model name, token estimate, and latency.
    ///
    /// This is what lets you see in the dashboard:
    ///   - Which prompts produced which completions
    ///   - Token usage per call (estimated — Ollama doesn't return exact counts)
    ///   - Which LLM calls are slow
    ///   - Input/output for debugging unexpected responses
    /// </summary>
    public void LogGeneration(
        TraceContext ctx,
        string name,
        string model,
        string prompt,
        string completion,
        int estimatedTokens,
        long latencyMs
    )
    {
        if (!_options.Enabled || ctx.IsNone)
            return;

        try
        {
            var genId = NewId();

            Enqueue(
                new LangfuseEvent
                {
                    Type = "generation-create",
                    Body = new
                    {
                        id = genId,
                        traceId = ctx.TraceId,
                        parentObservationId = ctx.SpanId,
                        name,
                        model,
                        startTime = DateTime.UtcNow.AddMilliseconds(-latencyMs),
                        endTime = DateTime.UtcNow,
                        input = new[] { new { role = "user", content = prompt } },
                        output = new { role = "assistant", content = completion },
                        usage = new
                        {
                            input = estimatedTokens / 2, // rough split — Ollama doesn't expose per-role counts
                            output = estimatedTokens / 2,
                            total = estimatedTokens,
                            unit = "TOKENS",
                        },
                        metadata = new { latencyMs },
                    },
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracing.LogGeneration failed silently");
        }
    }

    /// <summary>
    /// Marks the root trace as complete with final output.
    /// Call this at the end of the /chat handler before returning the response.
    /// </summary>
    public void EndTrace(TraceContext ctx, object? output = null, string? statusMessage = null)
    {
        if (!_options.Enabled || ctx.IsNone)
            return;

        try
        {
            // Close the root span
            EndSpan(ctx, output, statusMessage);

            // Update the trace with final output
            Enqueue(
                new LangfuseEvent
                {
                    Type = "trace-create", // Langfuse uses trace-create for updates too (upsert by id)
                    Body = new
                    {
                        id = ctx.TraceId,
                        output,
                        metadata = new { statusMessage },
                    },
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracing.EndTrace failed silently");
        }
    }

    // ── Background Worker ─────────────────────────────────────────────────────

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        var batch = new List<LangfuseEvent>(_options.BatchSize);
        var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait up to FlushIntervalSeconds for events, then flush whatever we have
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(flushInterval);

                try
                {
                    await foreach (var evt in _channel.Reader.ReadAllAsync(timeoutCts.Token))
                    {
                        batch.Add(evt);
                        if (batch.Count >= _options.BatchSize)
                            break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timer fired — flush what we have
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch, ct);
                    batch.Clear();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Langfuse worker iteration failed — continuing");
                await Task.Delay(1000, ct);
            }
        }

        // Drain remaining events on shutdown
        while (_channel.Reader.TryRead(out var evt))
            batch.Add(evt);
        if (batch.Count > 0)
            await FlushBatchAsync(batch, CancellationToken.None);
    }

    private async Task FlushBatchAsync(List<LangfuseEvent> batch, CancellationToken ct)
    {
        try
        {
            var payload = new { batch };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/api/public/ingestion", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug(
                    "Langfuse ingestion returned {Status}: {Body}",
                    response.StatusCode,
                    body[..Math.Min(200, body.Length)]
                );
            }
        }
        catch (Exception ex)
        {
            // Langfuse is unreachable — drop the batch, never throw to caller
            _logger.LogDebug(
                ex,
                "Langfuse flush failed — batch of {Count} events dropped",
                batch.Count
            );
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Enqueue(LangfuseEvent evt)
    {
        // TryWrite is synchronous and non-blocking — if the channel is full,
        // DropOldest removes the oldest unprocessed event to make room.
        _channel.Writer.TryWrite(evt);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}

// ── Internal event model (matches Langfuse REST API schema) ──────────────────

internal sealed class LangfuseEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("body")]
    public object Body { get; set; } = new();
}
