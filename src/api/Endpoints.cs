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
        app.MapServiceInfo();
        app.MapRecipeSearch();
        app.MapRecipeSearchValidated();
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
                    },
                    agents = new[] { "recipe", "diet", "orchestrator", "planner (coming soon)" },
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

    // ── Chat (Orchestrator — natural language) ────────────────

    private static void MapChat(this WebApplication app)
    {
        app.MapPost(
            "/chat",
            async (
                ChatRequest request,
                IntentRouter intentRouter,
                AgentOrchestrator orchestrator
            ) =>
            {
                // Step 1: Classify intent + extract entities
                // Rules-only for MVP (UseLlmClassification=false default)
                // Month 2: pass UseLlmClassification=true for LLM-powered classification
                var classified = await intentRouter.ClassifyAsync(
                    request.Message,
                    request.DietaryProfile
                );

                // Step 2: Route to right agent(s) and build response
                var response = await orchestrator.RouteAsync(classified);

                return Results.Ok(response);
            }
        );
    }
}
