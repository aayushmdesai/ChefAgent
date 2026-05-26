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

// ── Middleware ────────────────────────────────────────────────
app.UseCors();

// ── Endpoints ─────────────────────────────────────────────────
app.MapChefAgentEndpoints();

app.Run();
