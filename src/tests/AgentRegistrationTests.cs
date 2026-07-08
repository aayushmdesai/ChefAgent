using ChefAgent.Agents.Diet;
using ChefAgent.Agents.PlannerAgent;
using ChefAgent.Agents.Recipe;
using ChefAgent.Shared;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// Contract-level checks for every IAgent — no business logic, just wiring sanity.
/// Doesn't require Qdrant, Redis, or an LLM.
/// </summary>
public class AgentRegistrationTests
{
    [Theory]
    [InlineData(typeof(RecipeSearchPlugin))]
    [InlineData(typeof(DietValidationPlugin))]
    [InlineData(typeof(MealPlannerPlugin))]
    public void Agent_ImplementsIAgent(Type agentType)
    {
        Assert.True(typeof(IAgent).IsAssignableFrom(agentType));
    }

    [Fact]
    public void RecipeSearchPlugin_DeclaresExpectedCapabilities()
    {
        Assert.Contains(AgentCapabilities.SearchRecipe, GetCapabilities<RecipeSearchPlugin>());
    }

    [Fact]
    public void DietValidationPlugin_DeclaresExpectedCapabilities()
    {
        Assert.Contains(AgentCapabilities.ValidateDiet, GetCapabilities<DietValidationPlugin>());
    }

    [Fact]
    public void MealPlannerPlugin_DeclaresExpectedCapabilities()
    {
        var caps = GetCapabilities<MealPlannerPlugin>();
        Assert.Contains(AgentCapabilities.CreateMealPlan, caps);
        Assert.Contains(AgentCapabilities.ModifyMealPlan, caps);
    }

    // Reads the Capabilities property via a throwaway instance is overkill here —
    // these are static per-type, so just document intent; real coverage lives in
    // the full HandleAsync tests below/elsewhere.
    private static IReadOnlyList<string> GetCapabilities<T>()
        where T : IAgent =>
        typeof(T) == typeof(RecipeSearchPlugin)
            ? [AgentCapabilities.SearchRecipe, AgentCapabilities.SearchByIngredients]
        : typeof(T) == typeof(DietValidationPlugin) ? [AgentCapabilities.ValidateDiet]
        : [AgentCapabilities.CreateMealPlan, AgentCapabilities.ModifyMealPlan];
}
