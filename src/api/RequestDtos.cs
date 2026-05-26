// ============================================================
// ChefAgent API — Request DTOs
// ============================================================

namespace ChefAgent.Api;

using ChefAgent.Shared.Models;

/// <summary>
/// Request for direct recipe search endpoints.
/// Used by /recipes/search and /recipes/search-validated.
/// </summary>
public record RecipeSearchRequest(
    string Query,
    int MaxResults = 5,
    int? MaxIngredients = null, // hard filter — only recipes with ≤ N ingredients
    int? MaxSteps = null, // hard filter — only recipes with ≤ N steps
    bool Rerank = false, // LLM re-ranking — slow on CPU, opt-in
    bool Expand = false, // LLM query expansion — slow on CPU, opt-in
    DietaryProfile? DietaryProfile = null // null = skip dietary validation
);

/// <summary>
/// Request for the /chat endpoint.
/// Natural language message — Orchestrator routes to right agent(s).
/// </summary>
public record ChatRequest(
    string Message,
    string? SessionId = null, // reserved for Month 2 Redis session memory
    DietaryProfile? DietaryProfile = null, // optional — merged with any profile extracted from message
    bool UseLlmClassification = false // false = rules only (fast, no timeouts)
// true  = LLM classification (smart, slow on 8GB CPU)
// Month 2: train classifier on labeled dataset
);
