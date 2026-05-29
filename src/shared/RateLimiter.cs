using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared;

public class RateLimiter
{
    private readonly ILogger<RateLimiter> _logger;
    private readonly int _maxRequestsPerMinute;
    private readonly ConcurrentDictionary<string, SessionWindow> _sessions = new();

    public RateLimiter(ILogger<RateLimiter> logger, int maxRequestsPerMinute = 30)
    {
        _logger = logger;
        _maxRequestsPerMinute = maxRequestsPerMinute;
    }

    /// <summary>
    /// Checks if a session is within its rate limit.
    /// Returns true if allowed, false if rate limited.
    /// </summary>
    public bool IsAllowed(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return true; // no session = no per-session limiting

        var window = _sessions.GetOrAdd(sessionId, _ => new SessionWindow());

        lock (window)
        {
            window.CleanExpired();

            if (window.Count >= _maxRequestsPerMinute)
            {
                _logger.LogWarning(
                    "[RateLimiter] Session {SessionId} exceeded {Limit} requests/min — throttled",
                    sessionId,
                    _maxRequestsPerMinute
                );
                return false;
            }

            window.Record();
            return true;
        }
    }

    /// <summary>
    /// Checks if the message is a repeat of recent messages from the same session.
    /// Returns the repeat count (0 = not a repeat).
    /// </summary>
    public int CheckRepeat(string? sessionId, string message)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrWhiteSpace(message))
            return 0;

        var window = _sessions.GetOrAdd(sessionId, _ => new SessionWindow());

        lock (window)
        {
            var normalized = message.ToLowerInvariant().Trim();

            if (normalized == window.LastMessage)
            {
                window.RepeatCount++;
                _logger.LogInformation(
                    "[RateLimiter] Session {SessionId} repeated message {Count} times",
                    sessionId,
                    window.RepeatCount
                );
                return window.RepeatCount;
            }

            window.LastMessage = normalized;
            window.RepeatCount = 1;
            return 1; // first occurrence
        }
    }

    /// <summary>
    /// Sliding window per session — tracks timestamps of recent requests.
    /// </summary>
    private class SessionWindow
    {
        private readonly Queue<DateTime> _timestamps = new();
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public int Count => _timestamps.Count;
        public string? LastMessage { get; set; }
        public int RepeatCount { get; set; }

        public void Record() => _timestamps.Enqueue(DateTime.UtcNow);

        public void CleanExpired()
        {
            var cutoff = DateTime.UtcNow - Window;
            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();
        }
    }
}
