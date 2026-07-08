using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;

namespace ChefAgent.Shared;

public interface IAgent
{
    string Name { get; }
    IReadOnlyList<string> Capabilities { get; }
    Task<AgentResult> HandleAsync(AgentContext context, CancellationToken ct = default);
}

// src/shared/IAgent.cs — AgentContext revised
public record AgentContext
{
    public required ClassifiedIntent Classified { get; init; }
    public required TraceContext TraceCtx { get; init; }
    public Dictionary<string, object> SharedData { get; init; } = new();
}

public record AgentResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public object? Data { get; init; }

    // What this agent contributes to SharedData for the next agent in a chain.
    // Null/empty for terminal agents that don't feed a pipeline.
    public Dictionary<string, object>? OutputsForNextAgent { get; init; }
}

public static class AgentCapabilities
{
    public const string SearchRecipe = "SearchRecipe";
    public const string SearchByIngredients = "SearchByIngredients";
    public const string ValidateDiet = "ValidateDiet";
    public const string CreateMealPlan = "CreateMealPlan";
    public const string ModifyMealPlan = "ModifyMealPlan";
    public const string GetMealPlan = "GetMealPlan";
}
