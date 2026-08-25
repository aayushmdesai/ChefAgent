// Level 1 harness: provider microbenchmark, interleaved.
//
// Sends the FIVE PROMPT SHAPES ChefAgent actually issues — lifted verbatim from
// RecipeReranker, IntentRouter, DietValidationPlugin, QueryPreprocessor and
// AgentOrchestrator, filled with content from the committed golden dataset.
//
// WHY INTERLEAVED (see FINDINGS F-09):
// This endpoint showed ~15x latency variance on identical single requests with
// zero concurrency. A sweep that runs c=1, then c=8, then c=32 assigns whatever
// the endpoint was doing during each block to that config. Run 20260824-215404
// produced c=1 SLOWER than c=32 for exactly this reason — an artifact, not a
// result. So: each ROUND runs all three concurrency levels back to back in
// shuffled order, rounds repeat, and analysis pools by config across rounds.
// Session drift then hits every config roughly equally instead of landing
// entirely on whichever ran last.
//
// Health probes are LOGGED, NOT USED TO ABORT. Aborting on one slow probe would
// repeat last night's n=1 error: a single 13.9s probe was read as "the endpoint
// is degraded" when five probes showed it was simply variable. The probe is
// recorded so analysis can correlate config results against endpoint state.
//
// Usage:
//   export NEBIUS_API_KEY=...
//   dotnet run --project bench -- bench/queries.json bench/results/ 5
//                                                                   ^ rounds
//
// Output: results/raw-<runId>.jsonl       one record per request
//         results/probes-<runId>.jsonl    one record per health probe
//         results/manifest-<runId>.json   executed order + config

using System.Diagnostics;
using System.Text.Json;
using ChefAgent.Shared.Providers.Llm;

namespace ChefAgent.Bench;

public record BenchMessage(string Role, string Content);

public record BenchQuery(
    string Id,
    string Path,
    string ExpectFormat,          // "json" | "text"
    List<BenchMessage> Messages);

public record BenchRecord(
    string RunId, int Round, string QueryId, string Path, string Model,
    int Concurrency, int Iteration, DateTimeOffset StartedAt,
    double TtftMs, double TotalMs,
    int PromptTokens, int CompletionTokens, int CachedTokens,
    string ExpectFormat, bool ParseOk, bool NeededFenceStrip,
    string? ContentSample,        // first 400 chars — lets parse failures be inspected
    int StatusCode, int RateLimitHits, string? Error);

public record ProbeRecord(
    string RunId, int Round, string When, DateTimeOffset At,
    double TtftMs, double TotalMs, int CompletionTokens, string? Error);

public record RunManifest(
    string RunId, int Seed, int Rounds, string Model, int[] ConcurrencyLevels,
    int QueryCount, string[] ExecutedOrder, DateTimeOffset StartedAt);

public static class BenchmarkRunner
{
    private const int Seed = 20260825;
    private const string Model = "meta-llama/Llama-3.3-70B-Instruct";
    private static readonly int[] ConcurrencyLevels = { 1, 8, 32 };

    public static async Task Main(string[] args)
    {
        var apiKey = Environment.GetEnvironmentVariable("NEBIUS_API_KEY")
            ?? throw new InvalidOperationException("NEBIUS_API_KEY not set");

        var queryPath = args.Length > 0 ? args[0] : "bench/queries.json";
        var outDir = args.Length > 1 ? args[1] : "bench/results";
        var rounds = args.Length > 2 && int.TryParse(args[2], out var r) ? r : 5;
        Directory.CreateDirectory(outDir);

        var queries = JsonSerializer.Deserialize<List<BenchQuery>>(
            File.ReadAllText(queryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var rawFile = Path.Combine(outDir, $"raw-{runId}.jsonl");
        var probeFile = Path.Combine(outDir, $"probes-{runId}.jsonl");

        var rng = new Random(Seed);
        var executedOrder = new List<string>();

        Console.WriteLine($"Run {runId} (seed {Seed})");
        Console.WriteLine($"{queries.Count} queries x {ConcurrencyLevels.Length} levels x {rounds} rounds");
        Console.WriteLine($"= {queries.Count * ConcurrencyLevels.Length * rounds} requests\n");

        // One handler for the whole run. MaxConnectionsPerServer must exceed the
        // highest concurrency level or the client becomes the bottleneck and the
        // measurement is of .NET, not of Nebius.
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler);
        var provider = new NebiusProvider(http, apiKey, Model);

        // Warm the connection pool once. First call pays TLS + routing setup.
        await provider.ChatWithMetricsAsync(
            new[] { new ChatMessage("user", "warmup") }, stream: true);

        for (int round = 1; round <= rounds; round++)
        {
            // Shuffle level order within each round so no level systematically
            // occupies the same position in the round.
            var levels = ConcurrencyLevels.OrderBy(_ => rng.Next()).ToArray();
            Console.WriteLine($"── Round {round}/{rounds}  (order: {string.Join(", ", levels.Select(l => "c" + l))})");

            await ProbeAsync(provider, runId, round, "round-start", probeFile);

            foreach (var c in levels)
            {
                executedOrder.Add($"r{round}:c{c}");
                Console.Write($"   c={c,-3} ... ");
                var sw = Stopwatch.StartNew();

                using var gate = new SemaphoreSlim(c);
                var tasks = queries.Select(async (q, i) =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var msgs = q.Messages
                            .Select(m => new ChatMessage(m.Role, m.Content))
                            .ToArray();

                        var startedAt = DateTimeOffset.UtcNow;
                        var res = await provider.ChatWithMetricsAsync(msgs, stream: true);
                        var (parseOk, fenced) = Validate(q.ExpectFormat, res.Content, res.Error);

                        return new BenchRecord(runId, round, q.Id, q.Path, Model, c, i,
                            startedAt, res.TtftMs, res.TotalMs,
                            res.PromptTokens, res.CompletionTokens, res.CachedTokens,
                            q.ExpectFormat, parseOk, fenced,
                            Sample(res.Content), res.StatusCode, res.RateLimitHits, res.Error);
                    }
                    finally { gate.Release(); }
                }).ToList();   // materialize before WhenAll so tasks start together

                var batch = await Task.WhenAll(tasks);
                await File.AppendAllLinesAsync(rawFile,
                    batch.Select(x => JsonSerializer.Serialize(x)));

                var ok = batch.Count(x => x.Error is null);
                var parsed = batch.Count(x => x.ParseOk);
                Console.WriteLine($"{ok}/{batch.Length} ok, {parsed} parsed, {sw.Elapsed.TotalSeconds,6:F1}s");
            }

            await ProbeAsync(provider, runId, round, "round-end", probeFile);
            Console.WriteLine();
        }

        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"manifest-{runId}.json"),
            JsonSerializer.Serialize(
                new RunManifest(runId, Seed, rounds, Model, ConcurrencyLevels,
                    queries.Count, executedOrder.ToArray(), DateTimeOffset.UtcNow),
                new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Raw:    {rawFile}");
        Console.WriteLine($"Probes: {probeFile}");
        Console.WriteLine($"\nAnalyse with: python3 bench/analyze.py {rawFile}");
    }

    /// <summary>
    /// Single uncontended request, logged. Never aborts the run — see the header
    /// comment. Its purpose is to let analysis ask "what was the endpoint doing
    /// when this round ran", not to gate execution on one sample.
    /// </summary>
    private static async Task ProbeAsync(
        NebiusProvider provider, string runId, int round, string when, string probeFile)
    {
        var at = DateTimeOffset.UtcNow;
        var res = await provider.ChatWithMetricsAsync(
            new[] { new ChatMessage("user", "Say OK") }, stream: true);

        await File.AppendAllTextAsync(probeFile,
            JsonSerializer.Serialize(new ProbeRecord(
                runId, round, when, at, res.TtftMs, res.TotalMs,
                res.CompletionTokens, res.Error)) + "\n");

        Console.WriteLine($"   probe {when,-11} ttft={res.TtftMs,7:F0}ms total={res.TotalMs,7:F0}ms");
    }

    /// <summary>
    /// Four of five ChefAgent call sites demand strict JSON. A provider that is
    /// faster but emits unparseable output more often is WORSE for the pipeline,
    /// and latency-only numbers hide that — so parse rate is first-class here.
    ///
    /// NeededFenceStrip mirrors DietValidationPlugin's defensive
    /// raw.Replace("```json",""), tracking how often the model ignores
    /// "no markdown" and the app has to clean up after it.
    /// </summary>
    private static (bool ParseOk, bool NeededFenceStrip) Validate(
        string expectFormat, string content, string? error)
    {
        if (error is not null) return (false, false);
        if (!string.Equals(expectFormat, "json", StringComparison.OrdinalIgnoreCase))
            return (!string.IsNullOrWhiteSpace(content), false);

        var trimmed = content.Trim();
        var cleaned = trimmed.Replace("```json", "").Replace("```", "").Trim();
        var fenced = cleaned.Length != trimmed.Length;

        try
        {
            using var _ = JsonDocument.Parse(cleaned);
            return (true, fenced);
        }
        catch (JsonException)
        {
            return (false, fenced);
        }
    }

    private static string? Sample(string content) =>
        string.IsNullOrEmpty(content) ? null
        : content.Length <= 400 ? content : content[..400];
}