using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared;

public enum CircuitState
{
    Closed, // normal — LLM calls allowed
    Open, // tripped — skip LLM, use rules-only
    HalfOpen, // testing — allow one call to see if LLM recovered
}

public class CircuitBreaker
{
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;

    private int _consecutiveFailures;
    private DateTime _openedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public CircuitState State { get; private set; } = CircuitState.Closed;
    public int ConsecutiveFailures => _consecutiveFailures;

    public CircuitBreaker(
        ILogger<CircuitBreaker> logger,
        int failureThreshold = 3,
        int cooldownSeconds = 60
    )
    {
        _logger = logger;
        _failureThreshold = failureThreshold;
        _cooldown = TimeSpan.FromSeconds(cooldownSeconds);
    }

    /// <summary>
    /// Check if LLM calls are allowed. If Open and cooldown expired, transitions to HalfOpen.
    /// </summary>
    public bool IsAllowed()
    {
        lock (_lock)
        {
            if (State == CircuitState.Closed)
                return true;

            if (State == CircuitState.Open && DateTime.UtcNow - _openedAt >= _cooldown)
            {
                State = CircuitState.HalfOpen;
                _logger.LogInformation(
                    "[CircuitBreaker] Cooldown expired — transitioning to HalfOpen (testing one call)"
                );
                return true; // allow one test call
            }

            if (State == CircuitState.HalfOpen)
                return true; // already testing

            return false; // Open, cooldown not expired
        }
    }

    /// <summary>
    /// Record a successful LLM call. Resets to Closed.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (State == CircuitState.HalfOpen)
            {
                _logger.LogInformation(
                    "[CircuitBreaker] HalfOpen test succeeded — closing circuit"
                );
            }

            _consecutiveFailures = 0;
            State = CircuitState.Closed;
        }
    }

    /// <summary>
    /// Record a failed LLM call. Trips to Open if threshold reached.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (State == CircuitState.HalfOpen)
            {
                // Test call failed — re-open
                State = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "[CircuitBreaker] HalfOpen test failed — re-opening circuit for {Cooldown}s",
                    _cooldown.TotalSeconds
                );
                return;
            }

            if (_consecutiveFailures >= _failureThreshold)
            {
                State = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "[CircuitBreaker] {Failures} consecutive failures — circuit OPEN, skipping LLM for {Cooldown}s",
                    _consecutiveFailures,
                    _cooldown.TotalSeconds
                );
            }
        }
    }
}
