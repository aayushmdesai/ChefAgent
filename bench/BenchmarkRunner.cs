// Level 1 harness: provider microbenchmark.
//
// Sends the FIVE PROMPT SHAPES ChefAgent actually issues — not synthetic chat
// messages. Prompts are lifted verbatim from RecipeReranker, IntentRouter,
// DietValidationPlugin, QueryPreprocessor and AgentOrchestrator, and filled
// with content from the committed golden dataset and e2e sweep.
//
// No Qdrant, no Redis, no Voyage — isolates Token Factory serving performance
// from the Upstash cold start and Voyage RPM ceiling that dominate end-to-end.
//
// Usage:
//   export NEBIUS_API_KEY=...
//   dotnet run --project bench -- bench/queries.json bench/results/
//
// Output: results/raw-<runId>.jsonl      one record per request
//         results/manifest-<runId>.json  executed order + config, for reruns

using System.Diagnostics;
using System.Text.Json;
using ChefAgent.Shared.Providers.Llm;

namespace ChefAgent.Bench;

public record BenchMessage(string Role, string Content);

public record BenchQuery(
    string Id,
    string Path,
    string ExpectFormat, // "json" | "text"
    List<BenchMessage> Messages
);

public record BenchRecord(
    string RunId,
    string QueryId,
    string Path,
    string Model,
    int Concurrency,
    int Iteration,
    DateTimeOffset StartedAt,
    double TtftMs,
    double TotalMs,
    int PromptTokens,
    int CompletionTokens,
    int CachedTokens,
    string ExpectFormat,
    bool ParseOk,
    bool NeededFenceStrip,
    int StatusCode,
    int RateLimitHits,
    string? Error
);

public record RunManifest(
    string RunId,
    int Seed,
    string[] Models,
    int[] ConcurrencyLevels,
    int QueryCount,
    string[] ExecutedOrder,
    DateTimeOffset StartedAt
);

public static class BenchmarkRunner
{
    // Fixed seed. Changing it changes the run order, so it belongs in the
    // manifest and in the writeup's methodology section.
    private const int Seed = 20260824;

    private static readonly int[] ConcurrencyLevels = { 1, 8, 32 };

    private static readonly string[] Models = { "meta-llama/Llama-3.3-70B-Instruct" };

    public static async Task Main(string[] args)
    {
        var apiKey =
            Environment.GetEnvironmentVariable("NEBIUS_API_KEY")
            ?? throw new InvalidOperationException("NEBIUS_API_KEY not set");

        var queryPath = args.Length > 0 ? args[0] : "bench/queries.json";
        var outDir = args.Length > 1 ? args[1] : "bench/results";
        Directory.CreateDirectory(outDir);

        var queries = JsonSerializer.Deserialize<List<BenchQuery>>(
            File.ReadAllText(queryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        )!;

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var rawFile = Path.Combine(outDir, $"raw-{runId}.jsonl");
        var records = new List<BenchRecord>();

        // Prompt caching is on by default at Token Factory. Running
        // model-then-concurrency in nested loops means later configs always
        // execute against a warmer cache, which would make c=32 look good for
        // reasons unrelated to concurrency. Flatten and shuffle under a fixed
        // seed so cache warmth doesn't correlate with any reported axis.
        var configs = (
            from m in Models
            from c in ConcurrencyLevels
            select (Model: m, Concurrency: c)
        ).ToList();
        var rng = new Random(Seed);
        for (int i = configs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (configs[i], configs[j]) = (configs[j], configs[i]);
        }

        var order = configs.Select(x => $"{x.Model}@c{x.Concurrency}").ToArray();
        Console.WriteLine($"Run {runId} (seed {Seed})");
        Console.WriteLine($"{queries.Count} queries x {configs.Count} configs");
        Console.WriteLine($"Order: {string.Join(" -> ", order)}\n");

        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"manifest-{runId}.json"),
            JsonSerializer.Serialize(
                new RunManifest(
                    runId,
                    Seed,
                    Models,
                    ConcurrencyLevels,
                    queries.Count,
                    order,
                    DateTimeOffset.UtcNow
                ),
                new JsonSerializerOptions { WriteIndented = true }
            )
        );

        foreach (var (model, c) in configs)
        {
            using var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 64,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            using var http = new HttpClient(handler);
            var provider = new NebiusProvider(http, apiKey, model);

            // Warm the connection. First call pays TLS + routing setup;
            // including it poisons the c=1 numbers. Excluded from records.
            await provider.ChatWithMetricsAsync(
                new[] { new ChatMessage("user", "warmup") },
                stream: true
            );

            Console.Write($"  {model} @ c={c} ... ");
            var sw = Stopwatch.StartNew();

            using var gate = new SemaphoreSlim(c);
            var tasks = queries.Select(
                async (q, i) =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var msgs = q
                            .Messages.Select(m => new ChatMessage(m.Role, m.Content))
                            .ToArray();

                        var startedAt = DateTimeOffset.UtcNow;
                        var r = await provider.ChatWithMetricsAsync(msgs, stream: true);

                        var (parseOk, fenced) = Validate(q.ExpectFormat, r.Content, r.Error);

                        return new BenchRecord(
                            runId,
                            q.Id,
                            q.Path,
                            model,
                            c,
                            i,
                            startedAt,
                            r.TtftMs,
                            r.TotalMs,
                            r.PromptTokens,
                            r.CompletionTokens,
                            r.CachedTokens,
                            q.ExpectFormat,
                            parseOk,
                            fenced,
                            r.StatusCode,
                            r.RateLimitHits,
                            r.Error
                        );
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            );

            var batch = await Task.WhenAll(tasks);
            records.AddRange(batch);

            await File.AppendAllLinesAsync(rawFile, batch.Select(r => JsonSerializer.Serialize(r)));

            var ok = batch.Count(r => r.Error is null);
            Console.WriteLine($"{ok}/{batch.Length} ok in {sw.Elapsed.TotalSeconds:F1}s");
        }

        Summarize(records);
        Console.WriteLine($"\nRaw records: {rawFile}");
    }

    /// <summary>
    /// Four of the five ChefAgent call sites demand strict JSON. A provider
    /// that is faster but emits unparseable output more often is WORSE for the
    /// pipeline, and latency-only numbers hide that entirely — so parse rate is
    /// a first-class metric here.
    ///
    /// NeededFenceStrip mirrors DietValidationPlugin's defensive
    /// raw.Replace("```json", "") — it tracks how often the model ignores
    /// "no markdown" and the app has to clean up after it.
    /// </summary>
    private static (bool ParseOk, bool NeededFenceStrip) Validate(
        string expectFormat,
        string content,
        string? error
    )
    {
        if (error is not null)
            return (false, false);
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

    private static void Summarize(List<BenchRecord> records)
    {
        Console.WriteLine("\n── By config ──");
        Header("model");
        foreach (
            var g in records
                .GroupBy(r => (r.Model, r.Concurrency))
                .OrderBy(g => g.Key.Model)
                .ThenBy(g => g.Key.Concurrency)
        )
        {
            PrintRow(g.Key.Model, g.Key.Concurrency, g);
        }

        Console.WriteLine("\n── By call site (all configs pooled) ──");
        Header("path");
        foreach (var g in records.GroupBy(r => r.Path).OrderBy(g => g.Key))
        {
            PrintRow(g.Key, g.Count(), g);
        }

        var jsonTotal = records.Count(r => r.ExpectFormat == "json");
        var fenceCount = records.Count(r => r.NeededFenceStrip);
        if (fenceCount > 0)
        {
            Console.WriteLine(
                $"\nMarkdown fences emitted despite \"no markdown\": "
                    + $"{fenceCount}/{jsonTotal} JSON responses"
            );
        }

        var totalIn = records.Sum(r => (long)r.PromptTokens);
        var totalOut = records.Sum(r => (long)r.CompletionTokens);
        var totalCached = records.Sum(r => (long)r.CachedTokens);
        Console.WriteLine(
            $"\nTokens: {totalIn:N0} in ({totalCached:N0} cached), {totalOut:N0} out"
        );
        Console.WriteLine(
            "Multiply by catalog rates for cost. Check whether cached input "
                + "tokens bill at a reduced rate before computing."
        );
    }

    private static void Header(string first) =>
        Console.WriteLine(
            "{0,-46} {1,4} {2,9} {3,9} {4,9} {5,9} {6,7} {7,6} {8,5} {9,5}",
            first,
            "n",
            "ttft_p50",
            "ttft_p95",
            "tot_p50",
            "tot_p95",
            "cache%",
            "parse%",
            "err",
            "429"
        );

    private static void PrintRow(string label, int n, IEnumerable<BenchRecord> g)
    {
        var all = g.ToList();
        var ok = all.Where(r => r.Error is null).ToList();
        var errs = all.Count(r => r.Error is not null);
        var rl = all.Sum(r => r.RateLimitHits);

        // Short prompts show a high cache rate from the chat template alone,
        // not from your content. Report it rather than letting it hide inside
        // the latency numbers.
        long promptSum = ok.Sum(r => (long)r.PromptTokens);
        long cachedSum = ok.Sum(r => (long)r.CachedTokens);
        double cachePct = promptSum > 0 ? 100.0 * cachedSum / promptSum : 0;
        double parsePct = all.Count > 0 ? 100.0 * all.Count(r => r.ParseOk) / all.Count : 0;

        Console.WriteLine(
            "{0,-46} {1,4} {2,9:F0} {3,9:F0} {4,9:F0} {5,9:F0} {6,7:F1} {7,6:F1} {8,5} {9,5}",
            label.Length > 45 ? label[^45..] : label,
            n,
            Pct(ok.Select(r => r.TtftMs).Where(v => v >= 0), 50),
            Pct(ok.Select(r => r.TtftMs).Where(v => v >= 0), 95),
            Pct(ok.Select(r => r.TotalMs), 50),
            Pct(ok.Select(r => r.TotalMs), 95),
            cachePct,
            parsePct,
            errs,
            rl
        );
    }

    private static double Pct(IEnumerable<double> values, int p)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0)
            return 0;
        var idx = (int)Math.Ceiling(p / 100.0 * v.Length) - 1;
        return v[Math.Clamp(idx, 0, v.Length - 1)];
    }
}
