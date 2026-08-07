using ChefAgent.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChefAgent.Tests;

public class AgentRegistryTests
{
    private class FakeAgent : IAgent
    {
        public string Name { get; }
        public IReadOnlyList<string> Capabilities { get; }

        public FakeAgent(string name, params string[] capabilities)
        {
            Name = name;
            Capabilities = capabilities;
        }

        public Task<AgentResult> HandleAsync(
            AgentContext context,
            CancellationToken ct = default
        ) => Task.FromResult(new AgentResult { Success = true });
    }

    private static AgentRegistry MakeRegistry() => new(new Mock<ILogger<AgentRegistry>>().Object);

    private static AgentRegistry MakeRegistry(params string[] capabilities)
    {
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        foreach (var cap in capabilities)
            registry.Register(new FakeAgent($"Agent_{cap}", cap));
        return registry;
    }

    [Fact]
    public void EmptyRegistry_FindByCapability_ReturnsNull()
    {
        Assert.Null(MakeRegistry().FindByCapability(AgentCapabilities.SearchRecipe));
    }

    [Fact]
    public void EmptyRegistry_CountIsZero()
    {
        Assert.Equal(0, MakeRegistry().Count);
    }

    [Fact]
    public void Register_SingleAgent_FindByCapability_ReturnsIt()
    {
        var registry = MakeRegistry();
        var agent = new FakeAgent("RecipeAgent", AgentCapabilities.SearchRecipe);

        registry.Register(agent);

        Assert.Same(agent, registry.FindByCapability(AgentCapabilities.SearchRecipe));
    }

    [Fact]
    public void Register_MultipleCapabilitiesOnOneAgent_AllResolve()
    {
        var registry = MakeRegistry();
        var agent = new FakeAgent(
            "PlannerAgent",
            AgentCapabilities.CreateMealPlan,
            AgentCapabilities.ModifyMealPlan
        );

        registry.Register(agent);

        Assert.Same(agent, registry.FindByCapability(AgentCapabilities.CreateMealPlan));
        Assert.Same(agent, registry.FindByCapability(AgentCapabilities.ModifyMealPlan));
    }

    [Fact]
    public void Register_DuplicateCapability_Throws()
    {
        var registry = MakeRegistry();
        registry.Register(new FakeAgent("AgentA", AgentCapabilities.SearchRecipe));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new FakeAgent("AgentB", AgentCapabilities.SearchRecipe))
        );

        Assert.Contains("SearchRecipe", ex.Message);
    }

    [Fact]
    public void UnregisteredCapability_ReturnsNull()
    {
        var registry = MakeRegistry();
        registry.Register(new FakeAgent("RecipeAgent", AgentCapabilities.SearchRecipe));

        // Real-world case: GetMealPlan has no agent.
        Assert.Null(registry.FindByCapability(AgentCapabilities.GetMealPlan));
    }
}
