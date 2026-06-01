// ============================================================
// ChefAgent API — Endpoint Mappings
// ============================================================
// Three endpoint groups:
//
//   /health                    — health check
//   /                          — service info
//   /recipes/search            — Recipe Agent only (no diet validation)
//   /recipes/search-validated  — Recipe Agent + Diet Agent (direct, no orchestrator)
//   /chat                      — Orchestrator (natural language → right agents)
//
// Use /chat for conversational queries.
// Use /recipes/search-validated for direct API calls with known profile.
// Use /recipes/search for raw search without dietary context.
// ============================================================

namespace ChefAgent.Api;

using ChefAgent.Agents.Diet;
using ChefAgent.Agents.Orchestrator;
using ChefAgent.Agents.PlannerAgent;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared;
using ChefAgent.Shared.Models;

public static class Endpoints
{
    public static WebApplication MapChefAgentEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapGet(
            "/admin/guardrails",
            (GuardrailAuditLog audit) => Results.Ok(audit.GetRecent(50))
        );
        app.MapServiceInfo();
        app.MapRecipeSearch();
        app.MapRecipeSearchValidated();
        app.MapProfile();
        app.MapChat();

        return app;
    }

    // ── Service Info ──────────────────────────────────────────

    private static void MapServiceInfo(this WebApplication app)
    {
        app.MapGet(
            "/",
            () =>
                new
                {
                    service = "ChefAgent API",
                    version = "0.1.0",
                    status = "running",
                    stack = new
                    {
                        vectorDb = "Qdrant",
                        llm = "Ollama",
                        memory = "Redis",
                        observability = "Langfuse",
                    },
                    agents = new[] { "recipe", "diet", "orchestrator", "planner" },
                    endpoints = new[]
                    {
                        "GET  /health",
                        "POST /recipes/search",
                        "POST /recipes/search-validated",
                        "POST /chat",
                    },
                }
        );
    }

    // ── Recipe Search (no diet validation) ───────────────────

    private static void MapRecipeSearch(this WebApplication app)
    {
        app.MapPost(
            "/recipes/search",
            async (RecipeSearchRequest request, RecipeSearchPlugin plugin) =>
            {
                var results = await plugin.SearchRecipesAsync(
                    request.Query,
                    request.MaxResults,
                    request.MaxIngredients,
                    request.MaxSteps,
                    request.Rerank,
                    request.Expand
                );

                return Results.Ok(
                    new
                    {
                        query = request.Query,
                        count = results.Count,
                        recipes = results,
                    }
                );
            }
        );
    }

    // ── Recipe Search + Diet Validation (direct, no orchestrator) ──

    private static void MapRecipeSearchValidated(this WebApplication app)
    {
        app.MapPost(
            "/recipes/search-validated",
            async (
                RecipeSearchRequest request,
                RecipeSearchPlugin recipePlugin,
                DietValidationPlugin dietPlugin
            ) =>
            {
                // Step 1: Find candidates
                var results = await recipePlugin.SearchRecipesAsync(
                    request.Query,
                    request.MaxResults,
                    request.MaxIngredients,
                    request.MaxSteps,
                    request.Rerank,
                    request.Expand
                );

                // Step 2: Validate each recipe — skip validation if no profile
                var validated = new List<object>();
                foreach (var recipe in results)
                {
                    var validation = await dietPlugin.ValidateRecipeAsync(
                        recipe,
                        request.DietaryProfile
                    );

                    validated.Add(
                        new
                        {
                            recipe,
                            dietary = new
                            {
                                isCompatible = validation.IsCompatible,
                                violations = validation.Violations,
                                substitutions = validation.Substitutions,
                                explanation = validation.Explanation,
                            },
                        }
                    );
                }

                // Compatible recipes first
                var sorted = validated
                    .OrderByDescending(v => ((dynamic)v).dietary.isCompatible)
                    .ToList();

                return Results.Ok(
                    new
                    {
                        query = request.Query,
                        count = sorted.Count,
                        profileApplied = request.DietaryProfile is not null,
                        recipes = sorted,
                    }
                );
            }
        );
    }

    // ── Profile ───────────────────────────────────────────────

    private static void MapProfile(this WebApplication app)
    {
        // GET — frontend loads this on page startup to restore sidebar toggles
        app.MapGet(
            "/profile/{sessionId}",
            async (string sessionId, SessionStore store) =>
            {
                var profile = await store.GetProfileAsync(sessionId);
                return profile is null
                    ? Results.NotFound(new { message = "No profile found for this session." })
                    : Results.Ok(profile);
            }
        );

        // POST — sidebar toggle changes call this directly (not bundled with /chat)
        app.MapPost(
            "/profile/{sessionId}",
            async (string sessionId, DietaryProfile profile, SessionStore store) =>
            {
                await store.SaveProfileAsync(sessionId, profile);
                return Results.Ok(new { saved = true, sessionId });
            }
        );
    }

    // ── Chat (Orchestrator — natural language) ────────────────
    private static void MapChat(this WebApplication app)
    {
        app.MapPost(
            "/chat",
            async (
                ChatRequest request,
                IntentRouter intentRouter,
                AgentOrchestrator orchestrator,
                SessionStore sessionStore,
                RateLimiter rateLimiter,
                GuardrailAuditLog audit,
                Tracing tracing
            ) =>
            {
                // Start a trace for this request — covers everything below.
                // Returns TraceContext.None immediately if Langfuse is disabled.
                // Never throws — tracing failures are always silent.
                var traceCtx = tracing.StartTrace(
                    name: "chat",
                    sessionId: request.SessionId ?? "anonymous",
                    input: request.Message
                );

                // ── Guardrails ────────────────────────────────────────────
                var validation = InputGuard.Validate(request.Message);
                if (!validation.IsValid)
                {
                    audit.Record(
                        "injection_blocked",
                        request.SessionId ?? "unknown",
                        request.Message
                    );
                    // Early exits skip EndTrace — too fast to be worth tracing,
                    // and they never reach the agents.
                    return Results.Ok(
                        new OrchestratorResponse
                        {
                            Message = validation.RejectionReason!,
                            DetectedIntent = UserIntent.Unknown,
                        }
                    );
                }
                // Rate limiting
                if (!rateLimiter.IsAllowed(request.SessionId))
                {
                    audit.Record("rate_limited", request.SessionId ?? "unknown");
                    return Results.Json(
                        new OrchestratorResponse
                        {
                            Message =
                                "You're sending requests too quickly. Please wait a moment and try again.",
                            DetectedIntent = UserIntent.Unknown,
                            Confidence = ResponseConfidence.High,
                        },
                        statusCode: 429
                    );
                }
                // Repeated query detection
                var repeatCount = rateLimiter.CheckRepeat(
                    request.SessionId,
                    validation.SanitizedMessage
                );
                if (repeatCount >= 3)
                {
                    audit.Record(
                        "repeated_query",
                        request.SessionId ?? "unknown",
                        validation.SanitizedMessage
                    );
                    return Results.Ok(
                        new OrchestratorResponse
                        {
                            Message =
                                "I already answered that — would you like to try a different query?",
                            DetectedIntent = UserIntent.Unknown,
                            Confidence = ResponseConfidence.High,
                        }
                    );
                }

                // ── Intent Classification ─────────────────────────────────
                List<ConversationEntry>? history = null;
                if (!string.IsNullOrEmpty(request.SessionId))
                {
                    try
                    {
                        history = await sessionStore.GetHistoryAsync(request.SessionId, limit: 6);
                    }
                    catch
                    {
                        /* non-critical — proceed without history */
                    }
                }

                var classified = await intentRouter.ClassifyAsync(
                    validation.SanitizedMessage,
                    request.DietaryProfile,
                    request.SessionId,
                    history
                );

                // ── Agent Dispatch ────────────────────────────────────────
                var response = await orchestrator.RouteAsync(classified, traceCtx);

                // Close the root trace with the final response message.
                // At this point all agent spans are already ended inside RouteAsync.
                tracing.EndTrace(traceCtx, output: response.Message);

                return Results.Ok(response);
            }
        );
    }
}
