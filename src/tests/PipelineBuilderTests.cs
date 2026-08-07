using ChefAgent.Shared;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// Builder-shape tests only — no execution. Nothing here calls HandleAsync;
/// PipelineRunner is what walks the steps, and its tests live separately.
/// </summary>
public class PipelineBuilderTests
{
    private class FakeAgent(string name, params string[] capabilities) : IAgent
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Capabilities { get; } = capabilities;

        public Task<AgentResult> HandleAsync(
            AgentContext context,
            CancellationToken ct = default
        ) => Task.FromResult(new AgentResult { Success = true });
    }

    private static AgentRegistry MakeRegistry(params string[] capabilities)
    {
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        foreach (var cap in capabilities)
            registry.Register(new FakeAgent($"Agent_{cap}", cap));
        return registry;
    }

    private static AgentContext MakeContext(DietaryProfile? mergedProfile = null) =>
        new()
        {
            Classified = new ClassifiedIntent
            {
                Intent = UserIntent.SearchRecipe,
                SearchQuery = "pasta",
                ClassifiedBy = "rules",
                MergedProfile = mergedProfile,
            },
            TraceCtx = TraceContext.None,
        };

    [Fact]
    public void Build_EmptyPipeline_Throws()
    {
        var registry = MakeRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PipelineBuilder.For(UserIntent.SearchRecipe, registry).Build()
        );

        Assert.Contains("no steps", ex.Message);
    }

    [Fact]
    public void Build_UnregisteredCapability_ThrowsAndNamesIt()
    {
        var registry = MakeRegistry(AgentCapabilities.SearchRecipe);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PipelineBuilder
                .For(UserIntent.SearchRecipe, registry)
                .Then(AgentCapabilities.SearchRecipe)
                .Then(AgentCapabilities.ValidateDiet)
                .Build()
        );

        Assert.Contains(AgentCapabilities.ValidateDiet, ex.Message);
    }

    [Fact]
    public void Build_PreservesStepOrder()
    {
        var registry = MakeRegistry(AgentCapabilities.SearchRecipe, AgentCapabilities.ValidateDiet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Then(AgentCapabilities.ValidateDiet)
            .Build();

        Assert.Equal(2, pipeline.Steps.Count);
        Assert.Equal(AgentCapabilities.SearchRecipe, pipeline.Steps[0].Capability);
        Assert.Equal(AgentCapabilities.ValidateDiet, pipeline.Steps[1].Capability);
    }

    [Fact]
    public void Then_DefaultsToNoFanOutAndAbortOnFailure()
    {
        var registry = MakeRegistry(AgentCapabilities.SearchRecipe);

        var step = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Build()
            .Steps[0];

        Assert.Null(step.FanOutFrom);
        Assert.Null(step.FanOutItemKey);
        Assert.Null(step.RunIf);
        Assert.False(step.ContinueOnFailure);
    }

    /// <summary>
    /// The one real multi-agent chain in the codebase today: search returns N
    /// recipes, diet validation runs once per recipe. FanOutItemKey must be
    /// "TargetRecipe" — that is the SharedData key DietValidationPlugin.HandleAsync
    /// reads. If these two strings ever disagree, every validation silently
    /// finds nothing; nothing enforces the match at compile time.
    /// </summary>
    [Fact]
    public void RealShape_SearchThenFanOutDietValidation()
    {
        var registry = MakeRegistry(AgentCapabilities.SearchRecipe, AgentCapabilities.ValidateDiet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: "TargetRecipe",
                runIf: ctx => ctx.Classified.MergedProfile is not null,
                continueOnFailure: true
            )
            .Build();

        var dietStep = pipeline.Steps[1];

        Assert.Equal(AgentCapabilities.SearchRecipe, dietStep.FanOutFrom);
        Assert.Equal("TargetRecipe", dietStep.FanOutItemKey);
        Assert.True(dietStep.ContinueOnFailure);
    }

    [Fact]
    public void RunIf_GatesOnMergedProfilePresence()
    {
        var registry = MakeRegistry(AgentCapabilities.SearchRecipe, AgentCapabilities.ValidateDiet);

        var dietStep = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: "TargetRecipe",
                runIf: ctx => ctx.Classified.MergedProfile is not null
            )
            .Build()
            .Steps[1];

        Assert.NotNull(dietStep.RunIf);
        Assert.False(dietStep.RunIf!(MakeContext(mergedProfile: null)));
        Assert.True(dietStep.RunIf!(MakeContext(new DietaryProfile { Restrictions = ["vegan"] })));
    }
}
