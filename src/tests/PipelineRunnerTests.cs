using ChefAgent.Shared;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChefAgent.Tests;

public class PipelineRunnerTests
{
    /// <summary>
    /// Records every context it was handed so tests can assert on isolation
    /// between fan-out branches, not just on return values.
    /// </summary>
    private class RecordingAgent(
        string name,
        string capability,
        Func<AgentContext, AgentResult>? behavior = null
    ) : IAgent
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Capabilities { get; } = [capability];
        public List<AgentContext> Calls { get; } = [];

        private readonly Func<AgentContext, AgentResult> _behavior =
            behavior ?? (_ => new AgentResult { Success = true });

        public Task<AgentResult> HandleAsync(AgentContext context, CancellationToken ct = default)
        {
            Calls.Add(context);
            return Task.FromResult(_behavior(context));
        }
    }

    private class ThrowingAgent(string name, string capability) : IAgent
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Capabilities { get; } = [capability];

        public Task<AgentResult> HandleAsync(
            AgentContext context,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("qdrant exploded");
    }

    private static AgentRegistry MakeRegistry(params IAgent[] agents)
    {
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        foreach (var a in agents)
            registry.Register(a);
        return registry;
    }

    private static PipelineRunner MakeRunner(AgentRegistry registry) =>
        new(registry, new Mock<ILogger<PipelineRunner>>().Object);

    private static AgentContext MakeContext(DietaryProfile? profile = null) =>
        new()
        {
            Classified = new ClassifiedIntent
            {
                Intent = UserIntent.SearchRecipe,
                SearchQuery = "pasta",
                ClassifiedBy = "rules",
                MergedProfile = profile,
                SessionId = "session-1",
            },
            TraceCtx = TraceContext.None,
        };

    private static RecipeDocument Recipe(string id) =>
        new()
        {
            Id = id,
            Title = $"Recipe {id}",
            Ingredients = [],
            Directions = [],
        };

    // ── Sequential execution ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_TwoSteps_RunsBothInOrder()
    {
        var search = new RecordingAgent("Search", AgentCapabilities.SearchRecipe);
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Then(AgentCapabilities.ValidateDiet)
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.True(result.Success);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(AgentCapabilities.SearchRecipe, result.Steps[0].Capability);
        Assert.Equal(AgentCapabilities.ValidateDiet, result.Steps[1].Capability);
        Assert.Single(search.Calls);
        Assert.Single(diet.Calls);
    }

    [Fact]
    public async Task OutputsForNextAgent_VisibleToLaterSteps()
    {
        var search = new RecordingAgent(
            "Search",
            AgentCapabilities.SearchRecipe,
            _ => new AgentResult
            {
                Success = true,
                OutputsForNextAgent = new()
                {
                    ["Recipes"] = new List<RecipeDocument> { Recipe("r1") },
                },
            }
        );
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Then(AgentCapabilities.ValidateDiet)
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.True(diet.Calls[0].SharedData.ContainsKey("Recipes"));
        Assert.True(result.SharedData.ContainsKey("Recipes"));
    }

    // ── RunIf ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunIf_False_SkipsStepWithoutInvokingAgent()
    {
        var search = new RecordingAgent("Search", AgentCapabilities.SearchRecipe);
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Then(
                AgentCapabilities.ValidateDiet,
                runIf: ctx => ctx.Classified.MergedProfile is not null
            )
            .Build();

        // No profile — the real gate for skipping diet validation.
        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext(profile: null));

        Assert.True(result.Success);
        Assert.True(result.Steps[1].Skipped);
        Assert.Null(result.Steps[1].Result);
        Assert.Empty(diet.Calls);
    }

    // ── Failure handling ───────────────────────────────────────────────

    [Fact]
    public async Task StepFails_WithoutContinueOnFailure_AbortsPipeline()
    {
        var search = new RecordingAgent(
            "Search",
            AgentCapabilities.SearchRecipe,
            _ => new AgentResult { Success = false, ErrorMessage = "no results" }
        );
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Then(AgentCapabilities.ValidateDiet)
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.False(result.Success);
        Assert.Equal(AgentCapabilities.SearchRecipe, result.AbortedAt);
        Assert.Single(result.Steps);
        Assert.Empty(diet.Calls);
    }

    [Fact]
    public async Task AgentThrows_BecomesFailedResult_NotException()
    {
        var registry = MakeRegistry(new ThrowingAgent("Search", AgentCapabilities.SearchRecipe));

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.False(result.Success);
        Assert.Contains("qdrant exploded", result.Steps[0].Result!.ErrorMessage);
    }

    // ── Fan-out ────────────────────────────────────────────────────────

    private static (
        AgentRegistry Registry,
        RecordingAgent Diet,
        AgentPipeline Pipeline
    ) FanOutSetup(
        Func<AgentContext, AgentResult>? dietBehavior = null,
        bool continueOnFailure = true
    )
    {
        var search = new RecordingAgent(
            "Search",
            AgentCapabilities.SearchRecipe,
            _ => new AgentResult
            {
                Success = true,
                OutputsForNextAgent = new()
                {
                    [AgentCapabilities.SearchRecipe] = new List<RecipeDocument>
                    {
                        Recipe("r1"),
                        Recipe("r2"),
                        Recipe("r3"),
                    },
                },
            }
        );
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet, dietBehavior);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: "TargetRecipe",
                continueOnFailure: continueOnFailure
            )
            .Build();

        return (registry, diet, pipeline);
    }

    [Fact]
    public async Task FanOut_InvokesAgentOncePerItem()
    {
        var (registry, diet, pipeline) = FanOutSetup();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.True(result.Success);
        Assert.Equal(3, diet.Calls.Count);
        Assert.Equal(3, result.Steps[1].FanOutResults!.Count);
    }

    [Fact]
    public async Task FanOut_WritesEachItemToFanOutItemKey()
    {
        var (registry, diet, pipeline) = FanOutSetup();

        await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        // "TargetRecipe" is what DietValidationPlugin.HandleAsync actually reads.
        var seen = diet
            .Calls.Select(c => ((RecipeDocument)c.SharedData["TargetRecipe"]).Id)
            .ToList();

        Assert.Equal(["r1", "r2", "r3"], seen);
    }

    [Fact]
    public async Task FanOut_EachBranchGetsIsolatedSharedData()
    {
        var (registry, diet, pipeline) = FanOutSetup();

        await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        // Three distinct dictionaries — one branch's TargetRecipe must not
        // overwrite another's.
        Assert.Equal(3, diet.Calls.Select(c => c.SharedData).Distinct().Count());
    }

    [Fact]
    public async Task FanOut_EachBranchGetsItsOwnClassifiedIntent()
    {
        var (registry, diet, pipeline) = FanOutSetup();

        await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        // ClassifiedIntent has mutable setters — sharing one instance would let
        // one branch's agent corrupt its siblings.
        Assert.Equal(
            3,
            diet.Calls.Select(c => c.Classified)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count()
        );
    }

    [Fact]
    public async Task FanOut_BranchMutatingClassifiedIntent_DoesNotAffectSiblings()
    {
        var (registry, diet, pipeline) = FanOutSetup(dietBehavior: ctx =>
        {
            // ClassifiedIntent has mutable setters. If branches shared one
            // instance, this write would be visible to every sibling.
            ctx.Classified.TargetSlot = ((RecipeDocument)ctx.SharedData["TargetRecipe"]).Id;
            return new AgentResult { Success = true };
        });

        await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        var slots = diet.Calls.Select(c => c.Classified.TargetSlot).ToList();
        Assert.Equal(["r1", "r2", "r3"], slots);
    }

    [Fact]
    public async Task FanOut_OneItemFails_ContinueOnFailure_RunsRemainingItems()
    {
        var (registry, diet, pipeline) = FanOutSetup(
            dietBehavior: ctx =>
                ((RecipeDocument)ctx.SharedData["TargetRecipe"]).Id == "r2"
                    ? new AgentResult { Success = false, ErrorMessage = "violation" }
                    : new AgentResult { Success = true },
            continueOnFailure: true
        );

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        // All three attempted; pipeline survives; roll-up reports the failure.
        Assert.Equal(3, diet.Calls.Count);
        Assert.True(result.Success);
        Assert.False(result.Steps[1].Result!.Success);
        Assert.Contains("1 of 3", result.Steps[1].Result!.ErrorMessage);
    }

    [Fact]
    public async Task FanOut_OneItemFails_WithoutContinueOnFailure_StopsAndAborts()
    {
        var (registry, diet, pipeline) = FanOutSetup(
            dietBehavior: ctx =>
                ((RecipeDocument)ctx.SharedData["TargetRecipe"]).Id == "r2"
                    ? new AgentResult { Success = false, ErrorMessage = "violation" }
                    : new AgentResult { Success = true },
            continueOnFailure: false
        );

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.Equal(2, diet.Calls.Count); // stopped at r2, r3 never ran
        Assert.False(result.Success);
        Assert.Equal(AgentCapabilities.ValidateDiet, result.AbortedAt);
    }

    [Fact]
    public async Task FanOut_SourceKeyMissing_FailsWithWiringError()
    {
        // Search produces nothing, so the fan-out key is absent.
        var search = new RecordingAgent("Search", AgentCapabilities.SearchRecipe);
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: "TargetRecipe"
            )
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.False(result.Success);
        Assert.Empty(diet.Calls);
        Assert.Contains("no enumerable", result.Steps[1].Result!.ErrorMessage);
    }

    [Fact]
    public async Task FanOut_EmptyList_SucceedsWithZeroInvocations()
    {
        var search = new RecordingAgent(
            "Search",
            AgentCapabilities.SearchRecipe,
            _ => new AgentResult
            {
                Success = true,
                OutputsForNextAgent = new()
                {
                    [AgentCapabilities.SearchRecipe] = new List<RecipeDocument>(),
                },
            }
        );
        var diet = new RecordingAgent("Diet", AgentCapabilities.ValidateDiet);
        var registry = MakeRegistry(search, diet);

        var pipeline = PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: "TargetRecipe"
            )
            .Build();

        var result = await MakeRunner(registry).RunAsync(pipeline, MakeContext());

        Assert.True(result.Success);
        Assert.Empty(diet.Calls);
        Assert.Empty(result.Steps[1].FanOutResults!);
    }
}
