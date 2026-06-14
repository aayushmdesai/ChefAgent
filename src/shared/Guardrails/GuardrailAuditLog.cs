using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared.Guardrails;

public record GuardrailEvent
{
    public required string EventType { get; init; } // "injection_blocked", "rate_limited", etc.
    public required string SessionId { get; init; }
    public string? Detail { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class GuardrailAuditLog
{
    private readonly ILogger<GuardrailAuditLog> _logger;
    private readonly ConcurrentQueue<GuardrailEvent> _events = new();
    private const int MaxEvents = 1000; // ring buffer

    public GuardrailAuditLog(ILogger<GuardrailAuditLog> logger)
    {
        _logger = logger;
    }

    public void Record(string eventType, string sessionId, string? detail = null)
    {
        var evt = new GuardrailEvent
        {
            EventType = eventType,
            SessionId = sessionId,
            Detail = detail,
        };

        _events.Enqueue(evt);

        // Ring buffer — trim oldest
        while (_events.Count > MaxEvents)
            _events.TryDequeue(out _);

        _logger.LogInformation(
            "[GuardrailAudit] {EventType} | session={SessionId} | {Detail}",
            eventType,
            sessionId,
            detail ?? ""
        );
    }

    public List<GuardrailEvent> GetRecent(int count = 50) => _events.TakeLast(count).ToList();
}
