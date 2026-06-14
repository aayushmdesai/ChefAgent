using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared.Guardrails;

public enum CircuitState
{
    Closed, // normal — LLM calls allowed
    Open, // tripped — skip LLM, use rules-only
    HalfOpen, // testing — allow one call to see if LLM recovered
}

public class CircuitBreaker
{
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly GuardrailAuditLog _audit;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldown;

    private int _consecutiveFailures;
    private DateTime _openedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public CircuitState State { get; private set; } = CircuitState.Closed;
    public int ConsecutiveFailures => _consecutiveFailures;

    public CircuitBreaker(
        ILogger<CircuitBreaker> logger,
        GuardrailAuditLog audit,
        int failureThreshold = 3,
        int cooldownSeconds = 60
    )
    {
        _logger = logger;
        _audit = audit;
        _failureThreshold = failureThreshold;
        _cooldown = TimeSpan.FromSeconds(cooldownSeconds);
    }

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
                return true;
            }

            if (State == CircuitState.HalfOpen)
                return true;

            return false;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (State == CircuitState.HalfOpen)
            {
                _logger.LogInformation(
                    "[CircuitBreaker] HalfOpen test succeeded — closing circuit"
                );
                _audit.Record("circuit_closed", "system", "HalfOpen test succeeded");
            }

            _consecutiveFailures = 0;
            State = CircuitState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (State == CircuitState.HalfOpen)
            {
                State = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "[CircuitBreaker] HalfOpen test failed — re-opening circuit for {Cooldown}s",
                    _cooldown.TotalSeconds
                );
                _audit.Record("circuit_opened", "system", "HalfOpen test failed — re-opened");
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
                _audit.Record(
                    "circuit_opened",
                    "system",
                    $"{_consecutiveFailures} consecutive failures"
                );
            }
        }
    }
}
