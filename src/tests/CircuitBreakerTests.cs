using ChefAgent.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// Unit tests for CircuitBreaker state transitions.
/// Covers: Closed → Open → HalfOpen → Closed and HalfOpen → Open on re-failure.
/// </summary>
public class CircuitBreakerTests
{
    private CircuitBreaker MakeBreaker(int threshold = 3, int cooldownSeconds = 60)
    {
        var cbLogger = new Mock<ILogger<CircuitBreaker>>().Object;
        var auditLogger = new Mock<ILogger<GuardrailAuditLog>>().Object;
        var audit = new GuardrailAuditLog(auditLogger); // real instance — has no parameterless ctor
        return new CircuitBreaker(cbLogger, audit, threshold, cooldownSeconds);
    }

    // ── Initial State ─────────────────────────────────────────────────

    [Fact]
    public void InitialState_IsClosed()
    {
        var cb = MakeBreaker();
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public void InitialState_IsAllowed()
    {
        var cb = MakeBreaker();
        Assert.True(cb.IsAllowed());
    }

    // ── Closed → Open ─────────────────────────────────────────────────

    [Fact]
    public void BelowThreshold_StaysClosed()
    {
        var cb = MakeBreaker(threshold: 3);
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.True(cb.IsAllowed());
    }

    [Fact]
    public void AtThreshold_OpensCircuit()
    {
        var cb = MakeBreaker(threshold: 3);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
    }

    [Fact]
    public void OpenState_BlocksRequests()
    {
        var cb = MakeBreaker(threshold: 3);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.False(cb.IsAllowed());
    }

    [Fact]
    public void ConsecutiveFailures_TrackedCorrectly()
    {
        var cb = MakeBreaker(threshold: 5);
        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(2, cb.ConsecutiveFailures);
    }

    // ── Success Resets Counter ────────────────────────────────────────

    [Fact]
    public void SuccessBeforeThreshold_ResetsCounter()
    {
        var cb = MakeBreaker(threshold: 3);
        cb.RecordFailure();
        cb.RecordFailure();
        cb.RecordSuccess();
        Assert.Equal(0, cb.ConsecutiveFailures);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    // ── Open → HalfOpen (cooldown expired) ───────────────────────────

    [Fact]
    public void AfterCooldown_TransitionsToHalfOpen()
    {
        // Use 0-second cooldown so we can test the transition without waiting
        var cb = MakeBreaker(threshold: 1, cooldownSeconds: 0);
        cb.RecordFailure(); // opens circuit
        Assert.Equal(CircuitState.Open, cb.State);

        // After 0s cooldown, next IsAllowed should transition to HalfOpen
        Thread.Sleep(10); // tiny sleep to ensure time has passed
        Assert.True(cb.IsAllowed());
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    // ── HalfOpen → Closed (success) ───────────────────────────────────

    [Fact]
    public void HalfOpenSuccess_ClosesCircuit()
    {
        var cb = MakeBreaker(threshold: 1, cooldownSeconds: 0);
        cb.RecordFailure();
        Thread.Sleep(10);
        cb.IsAllowed(); // transitions to HalfOpen
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.ConsecutiveFailures);
    }

    // ── HalfOpen → Open (failure) ─────────────────────────────────────

    [Fact]
    public void HalfOpenFailure_ReOpensCircuit()
    {
        var cb = MakeBreaker(threshold: 1, cooldownSeconds: 0);
        cb.RecordFailure();
        Thread.Sleep(10);
        cb.IsAllowed(); // transitions to HalfOpen
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        cb.RecordFailure(); // HalfOpen test call fails — re-open
        Assert.Equal(CircuitState.Open, cb.State);
        // Note: with 0s cooldown, a subsequent IsAllowed() immediately re-transitions
        // to HalfOpen — correct behavior. State assertion is the meaningful check.
    }

    // ── Threshold variations ──────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void CircuitOpens_AtExactThreshold(int threshold)
    {
        var cb = MakeBreaker(threshold: threshold);
        for (int i = 0; i < threshold; i++)
        {
            Assert.Equal(CircuitState.Closed, cb.State);
            cb.RecordFailure();
        }
        Assert.Equal(CircuitState.Open, cb.State);
    }
}
