namespace ChefAgent.Shared.Observability;

/// <summary>
/// Binds to the "Langfuse" section in appsettings.json / environment variables.
/// Environment variable format: Langfuse__Enabled, Langfuse__BaseUrl, etc.
/// </summary>
public class LangfuseOptions
{
    public const string Section = "Langfuse";

    /// <summary>
    /// Master toggle. When false, Tracing.cs becomes a no-op — no HTTP calls,
    /// no overhead. Lets you disable tracing without redeploying.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Self-hosted: http://localhost:3100
    /// Langfuse Cloud: https://cloud.langfuse.com
    /// Only this URL changes when switching between deployments.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:3100";

    public string PublicKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// How many spans to buffer before flushing to Langfuse.
    /// Spans are also flushed every FlushIntervalSeconds regardless of batch size.
    /// </summary>
    public int BatchSize { get; set; } = 20;

    public int FlushIntervalSeconds { get; set; } = 5;
}
