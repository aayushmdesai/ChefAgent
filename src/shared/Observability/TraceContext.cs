namespace ChefAgent.Shared.Observability;

/// <summary>
/// Carries trace identity through the call chain.
///
/// This is a plain record — no dependencies, no Langfuse imports.
/// Every class that participates in tracing accepts and passes this through
/// method parameters. The only class that actually talks to Langfuse is Tracing.cs.
///
/// Why parameter passing instead of injecting ITracingService everywhere?
/// If Langfuse is down and ITracingService throws, that exception would propagate
/// through QdrantService, RulesEngine, OllamaService — killing the user's request.
/// With TraceContext, business logic is completely decoupled from the tracing backend.
/// A Langfuse outage produces silent no-ops, not request failures.
///
/// This maps directly to OpenTelemetry's Activity/SpanContext concept —
/// switching to OTel later only requires changing Tracing.cs.
/// </summary>
public record TraceContext
{
    /// <summary>
    /// Identifies the entire request tree — all spans under one /chat call
    /// share the same TraceId. Used to pull up a complete request trace in Langfuse.
    /// </summary>
    public string TraceId { get; init; } = string.Empty;

    /// <summary>
    /// The span currently "active" in this part of the call chain.
    /// When a component starts a child span, it passes its own SpanId
    /// as the parentSpanId — building the tree structure.
    /// </summary>
    public string SpanId { get; init; } = string.Empty;

    /// <summary>
    /// Short ID included in every log line for this request.
    /// Lets you correlate structured logs with Langfuse traces without
    /// opening the full trace dashboard.
    /// Format: first 12 chars of TraceId.
    /// </summary>
    public string CorrelationId => TraceId.Length >= 12 ? TraceId[..12] : TraceId;

    /// <summary>
    /// Returns a new TraceContext with the same TraceId but a new SpanId.
    /// Used when starting a child span — the child inherits the trace
    /// but gets its own span identity.
    /// </summary>
    public TraceContext WithNewSpan(string newSpanId) => this with { SpanId = newSpanId };

    /// <summary>
    /// A no-op context used when tracing is disabled or not yet initialized.
    /// All Tracing.cs methods check for this and short-circuit immediately.
    /// </summary>
    public static TraceContext None => new() { TraceId = string.Empty, SpanId = string.Empty };

    public bool IsNone => string.IsNullOrEmpty(TraceId);
}
