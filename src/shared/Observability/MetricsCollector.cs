using System.Collections.Concurrent;

namespace ChefAgent.Shared.Observability;

/// <summary>
/// In-memory metrics collector. Records per-request latency and intent counts.
/// All reads are computed over a sliding 5-minute window — numbers reflect
/// right now, not the last N requests regardless of when they happened.
///
/// Why not query Langfuse for this?
///   Langfuse is an observability backend for humans debugging specific requests.
///   Querying it from a metrics endpoint creates a runtime dependency — if Langfuse
///   is slow or down, /admin/metrics fails too. This in-memory store has zero
///   infrastructure dependencies and responds in microseconds.
///
/// Why sliding time window instead of last-N requests?
///   "Last 1000 requests" at low traffic (10 req/hour) reflects the last 100 hours.
///   A latency spike from 3 hours ago still drags your p95. A 5-minute window
///   reflects right now — which is what an ops dashboard needs.
///
/// Registered as a singleton. Thread-safe via ConcurrentQueue.
/// </summary>
public class MetricsCollector
{
    private readonly ConcurrentQueue<RequestSample> _samples = new();
    private const int MaxSamples = 5000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Record a completed request. Call this at the end of every /chat handler,
    /// just before returning the response.
    /// </summary>
    public void Record(string intent, long latencyMs, bool wasBlocked = false)
    {
        _samples.Enqueue(
            new RequestSample
            {
                Intent = intent,
                LatencyMs = latencyMs,
                WasBlocked = wasBlocked,
                Timestamp = DateTime.UtcNow,
            }
        );

        // Ring buffer — trim oldest beyond cap
        while (_samples.Count > MaxSamples)
            _samples.TryDequeue(out _);
    }

    /// <summary>
    /// Compute aggregate metrics over the last 5 minutes.
    /// All percentiles are computed fresh on each call — no pre-aggregation.
    /// Fast enough for a polling dashboard hitting every few seconds.
    /// </summary>
    public MetricsSnapshot GetSnapshot()
    {
        var cutoff = DateTime.UtcNow - Window;
        var recent = _samples.Where(s => s.Timestamp >= cutoff).ToList();

        var completed = recent.Where(s => !s.WasBlocked).ToList();
        var latencies = completed.Select(s => s.LatencyMs).OrderBy(x => x).ToList();

        return new MetricsSnapshot
        {
            WindowMinutes = (int)Window.TotalMinutes,
            RequestsTotal = recent.Count,
            RequestsCompleted = completed.Count,
            RequestsBlocked = recent.Count(s => s.WasBlocked),
            RequestsByIntent = completed
                .GroupBy(s => s.Intent)
                .ToDictionary(g => g.Key, g => g.Count()),
            Latency =
                latencies.Count == 0
                    ? new LatencyStats()
                    : new LatencyStats
                    {
                        P50Ms = Percentile(latencies, 50),
                        P95Ms = Percentile(latencies, 95),
                        P99Ms = Percentile(latencies, 99),
                        MinMs = latencies.First(),
                        MaxMs = latencies.Last(),
                        MeanMs = (long)latencies.Average(),
                    },
        };
    }

    // ── Percentile calculation ─────────────────────────────────────────────────

    /// <summary>
    /// Nearest-rank percentile. Input must be sorted ascending.
    ///
    /// Why nearest-rank over interpolation?
    ///   Simpler, returns an actual observed value (not a synthetic midpoint),
    ///   and at the sample sizes we're dealing with (hundreds, not millions)
    ///   the difference is negligible.
    /// </summary>
    private static long Percentile(List<long> sorted, int percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}

// ── Data types ────────────────────────────────────────────────────────────────

public record RequestSample
{
    public required string Intent { get; init; }
    public required long LatencyMs { get; init; }
    public required bool WasBlocked { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public record MetricsSnapshot
{
    /// <summary>How many minutes back this snapshot covers.</summary>
    public int WindowMinutes { get; init; }

    /// <summary>All requests in window — including blocked ones.</summary>
    public int RequestsTotal { get; init; }

    /// <summary>Requests that reached the agents (past guardrails).</summary>
    public int RequestsCompleted { get; init; }

    /// <summary>Requests blocked by InputGuard or RateLimiter.</summary>
    public int RequestsBlocked { get; init; }

    /// <summary>Completed requests grouped by detected intent.</summary>
    public Dictionary<string, int> RequestsByIntent { get; init; } = new();

    /// <summary>Latency stats over completed requests only.</summary>
    public LatencyStats Latency { get; init; } = new();
}

public record LatencyStats
{
    public long P50Ms { get; init; }
    public long P95Ms { get; init; }
    public long P99Ms { get; init; }
    public long MinMs { get; init; }
    public long MaxMs { get; init; }
    public long MeanMs { get; init; }
}
