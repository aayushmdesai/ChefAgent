# Structured logging convention

Backend logging uses `ILogger<T>` with a bracketed component tag as the first token of the message, and named placeholders for interpolated values — never raw string interpolation into the log message itself.

Example (from `RateLimiter.cs`):
```csharp
_logger.LogWarning("[RateLimiter] Session {SessionId} exceeded {Limit} requests/min — throttled", sessionId, _maxRequestsPerMinute);
```

**Why:** Confirmed with zero exceptions across every backend file read during the August 2026 investigation (`CircuitBreaker`, `OutputGuard`, `RateLimiter`, `GuardrailAuditLog`, `Program.cs`'s startup logging, `IntentRouter`). The bracket tag makes component-scoped log filtering possible in Langfuse/console output; named placeholders keep structured-logging semantics intact (unlike `$"..."` interpolation, which flattens everything to a string at the call site).

**How to apply:** Any new `LogInformation`/`LogWarning`/`LogError` call should start the message with `"[ComponentName] ..."` and use `{PlaceholderName}` tokens with the values passed as trailing arguments, not inline interpolation. Use `LogInformation` for expected-but-notable events (cache hits, state transitions), `LogWarning` for degraded/non-fatal failure paths, `LogError` for actual exceptions.
