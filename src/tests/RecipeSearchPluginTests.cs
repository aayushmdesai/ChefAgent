using ChefAgent.Agents.PlannerAgent;
using ChefAgent.Agents.Recipe;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// HandleAsync coverage deferred — both plugins depend on concrete external
/// clients (QdrantClient) or concrete sibling plugins (RecipeSearchPlugin,
/// DietValidationPlugin) rather than interfaces, so Moq can't intercept the
/// calls that matter. Real coverage needs either live Qdrant/LLM infra
/// (integration-style) or a refactor where MealPlannerPlugin depends on
/// IAgent/narrower interfaces instead of concrete types — likely a natural
/// side effect of the Day 4 pipeline builder work. Logged as tech debt,
/// not silently skipped.
/// </summary>
public class RecipeSearchPluginTests
{
    [Fact(
        Skip = "P2-1: HandleAsync needs live Qdrant + embedding provider — integration test, Week 2+"
    )]
    public void HandleAsync_ReturnsRecipes_WhenQueryMatches()
    {
        // Placeholder — see class summary.
    }
}

public class MealPlannerPluginTests
{
    [Fact(
        Skip = "P2-2: HandleAsync depends on concrete RecipeSearchPlugin + DietValidationPlugin, not interfaces — needs live infra or IAgent-based refactor, Week 2+"
    )]
    public void HandleAsync_CreateMealPlan_ReturnsPlan()
    {
        // Placeholder — see class summary.
    }

    [Fact(Skip = "P2-2: same as above — ModifyMealPlan path")]
    public void HandleAsync_ModifyMealPlan_ReturnsUpdatedPlan()
    {
        // Placeholder — see class summary.
    }
}
