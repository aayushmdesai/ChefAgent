// ============================================================
// ChefAgent API — Entry Point
// ============================================================
// Startup order:
//   1. Register services (ServiceRegistration.cs)
//   2. Configure middleware
//   3. Map endpoints (Endpoints.cs)
// ============================================================

using ChefAgent.Api;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────
builder.Services.AddChefAgentServices(builder.Configuration);

// ── Build ─────────────────────────────────────────────────────
var app = builder.Build();

// ── Pre-warm Redis connection ──────────────────────────────────
// Moves the Upstash cold-start penalty (~3,000ms) from the first
// user request to deployment startup. Prevents the Redis circuit
// breaker from tripping on cold connects, which was causing
// GetHistoryAsync to return empty and breaking conversation context.
try
{
    var redis = app.Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
    await redis.GetDatabase().PingAsync();
    app.Logger.LogInformation("[Startup] Redis pre-warm ping succeeded");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "[Startup] Redis pre-warm ping failed — continuing");
}

// ── Middleware ────────────────────────────────────────────────
app.UseCors();

// ── Endpoints ─────────────────────────────────────────────────
app.MapChefAgentEndpoints();

app.Run();
