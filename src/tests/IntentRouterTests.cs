using ChefAgent.Agents.Orchestrator;
using ChefAgent.Shared;
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using ChefAgent.Shared.Providers.Llm;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace ChefAgent.Tests;

/// <summary>
/// Unit tests for IntentRouter — rules-based classification path only.
/// No LLM calls made: rules short-circuit before any HTTP request.
/// Asserts on Intent (UserIntent) and ClassifiedBy ("rules" or "rules-default").
/// </summary>
public class IntentRouterTests
{
    // Minimal IAgent stub — the router only reads Capabilities via the registry.
    private sealed class StubAgent(string name, params string[] capabilities) : IAgent
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Capabilities { get; } = capabilities;

        public Task<AgentResult> HandleAsync(AgentContext context, CancellationToken ct = default) =>
            Task.FromResult(new AgentResult { Success = true });
    }

    // Registry with the full agent-backed capability set registered — the normal
    // startup state, so existing classification tests behave as before.
    private static AgentRegistry FullRegistry()
    {
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        registry.Register(
            new StubAgent(
                "RecipeAgent",
                AgentCapabilities.SearchRecipe,
                AgentCapabilities.SearchByIngredients
            )
        );
        registry.Register(new StubAgent("DietAgent", AgentCapabilities.ValidateDiet));
        registry.Register(
            new StubAgent(
                "PlannerAgent",
                AgentCapabilities.CreateMealPlan,
                AgentCapabilities.ModifyMealPlan
            )
        );
        return registry;
    }

    private IntentRouter MakeRouter(AgentRegistry? registry = null)
    {
        var cbLogger = new Mock<ILogger<CircuitBreaker>>().Object;
        var auditLogger = new Mock<ILogger<GuardrailAuditLog>>().Object;
        var audit = new GuardrailAuditLog(auditLogger);
        var circuitBreaker = new CircuitBreaker(cbLogger, audit);
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        var sessionStore = new Mock<SessionStore>(
            mockMultiplexer.Object,
            circuitBreaker,
            new Mock<ILogger<SessionStore>>().Object
        ).Object;

        var tracingOptions = Mock.Of<Microsoft.Extensions.Options.IOptions<LangfuseOptions>>(o =>
            o.Value == new LangfuseOptions { Enabled = false, BaseUrl = "http://localhost" }
        );
        var tracing = new Tracing(
            tracingOptions,
            new Mock<ILogger<Tracing>>().Object,
            new HttpClient()
        );

        var llmProvider = new Mock<ILlmProvider>();
        llmProvider
            .Setup(p =>
                p.ChatAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync("{}");

        return new IntentRouter(
            llmProvider.Object,
            circuitBreaker,
            sessionStore,
            tracing,
            registry ?? FullRegistry(),
            new Mock<ILogger<IntentRouter>>().Object
        );
    }

    // ── SearchRecipe ───────────────────────────────────────────────────

    [Theory]
    [InlineData("find me pasta recipes")]
    [InlineData("chicken dinner ideas")]
    [InlineData("something with salmon")]
    [InlineData("soup")]
    [InlineData("show me recipes with garlic")]
    public async Task SearchRecipe_CanonicalPhrases_Classified(string query)
    {
        var result = await MakeRouter().ClassifyAsync(query);
        Assert.Equal(UserIntent.SearchRecipe, result.Intent);
    }

    [Fact]
    public async Task SearchRecipe_SingleWord_Classified()
    {
        var result = await MakeRouter().ClassifyAsync("pasta");
        Assert.Equal(UserIntent.SearchRecipe, result.Intent);
    }

    // ── CreateMealPlan ─────────────────────────────────────────────────

    [Theory]
    [InlineData("plan my dinners for the week")]
    [InlineData("create a meal plan")]
    public async Task CreateMealPlan_CanonicalPhrases_Classified(string query)
    {
        var result = await MakeRouter().ClassifyAsync(query);
        Assert.Equal(UserIntent.CreateMealPlan, result.Intent);
    }

    // ── GetMealPlan ────────────────────────────────────────────────────

    [Theory]
    [InlineData("what's my plan?")]
    [InlineData("show me my meal plan")]
    [InlineData("whats my plan")]
    public async Task GetMealPlan_CanonicalPhrases_Classified(string query)
    {
        var result = await MakeRouter().ClassifyAsync(query);
        Assert.Equal(UserIntent.GetMealPlan, result.Intent);
    }

    // ── ModifyMealPlan ─────────────────────────────────────────────────

    [Theory]
    [InlineData("swap Tuesday dinner to something with pasta")]
    [InlineData("swap Monday to a vegetarian meal")]
    public async Task ModifyMealPlan_CanonicalPhrases_Classified(string query)
    {
        var result = await MakeRouter().ClassifyAsync(query);
        Assert.Equal(UserIntent.ModifyMealPlan, result.Intent);
    }

    // ── ValidateDiet ───────────────────────────────────────────────────

    [Theory]
    [InlineData("is pasta safe for a nut allergy?")]
    [InlineData("can I eat this if I'm gluten free?")]
    [InlineData("check this recipe for dairy")]
    public async Task ValidateDiet_CanonicalPhrases_Classified(string query)
    {
        var result = await MakeRouter().ClassifyAsync(query);
        Assert.Equal(UserIntent.ValidateDiet, result.Intent);
    }

    // ── ClassifiedBy ───────────────────────────────────────────────────

    [Fact]
    public async Task RulesPath_ClassifiedBy_IsRules()
    {
        var result = await MakeRouter().ClassifyAsync("find me pasta recipes");
        Assert.Contains("rules", result.ClassifiedBy, StringComparison.OrdinalIgnoreCase);
    }

    // ── Case insensitivity ─────────────────────────────────────────────

    [Fact]
    public async Task Classification_CaseInsensitive()
    {
        var router = MakeRouter();
        var lower = await router.ClassifyAsync("find me pasta recipes");
        var upper = await router.ClassifyAsync("FIND ME PASTA RECIPES");
        var mixed = await router.ClassifyAsync("FiNd Me PaStA ReCiPeS");
        Assert.Equal(lower.Intent, upper.Intent);
        Assert.Equal(lower.Intent, mixed.Intent);
    }

    // ── SearchQuery extraction ─────────────────────────────────────────

    [Fact]
    public async Task SearchRecipe_ExtractsSearchQuery()
    {
        var result = await MakeRouter().ClassifyAsync("find me pasta recipes");
        Assert.False(string.IsNullOrWhiteSpace(result.SearchQuery));
    }

    // ── Known gaps — documented in tech-debt.md ────────────────────────

    [Fact(Skip = "I-1: 'change X to' not yet in rules — Month 3")]
    public async Task ModifyMealPlan_ChangeVariant_Classified()
    {
        var result = await MakeRouter().ClassifyAsync("change Friday to a vegetarian meal");
        Assert.Equal(UserIntent.ModifyMealPlan, result.Intent);
    }

    [Fact(Skip = "I-2: multi-meal-type phrasing not yet in rules — Month 3")]
    public async Task CreateMealPlan_MultiSlot_Classified()
    {
        var result = await MakeRouter()
            .ClassifyAsync("plan breakfast lunch and dinner for the week");
        Assert.Equal(UserIntent.CreateMealPlan, result.Intent);
    }

    [Fact(Skip = "I-3: 'make me a new plan' not yet in rules — Month 3")]
    public async Task CreateMealPlan_MakeVariant_Classified()
    {
        var result = await MakeRouter().ClassifyAsync("make me a new plan");
        Assert.Equal(UserIntent.CreateMealPlan, result.Intent);
    }

    // ── Registry-driven intent discovery ─────────────────────────────────
    // The router only routes to an agent-backed intent the registry can handle.

    [Fact]
    public async Task ValidateDiet_FallsBackToSearch_WhenCapabilityNotRegistered()
    {
        // Registry without ValidateDiet — e.g. DietAgent not registered.
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        registry.Register(new StubAgent("RecipeAgent", AgentCapabilities.SearchRecipe));

        var result = await MakeRouter(registry).ClassifyAsync("is pasta safe for a nut allergy?");

        // ValidateDiet keywords match, but with no DietAgent the intent isn't live,
        // so it degrades to the SearchRecipe default instead of routing to a dead intent.
        Assert.Equal(UserIntent.SearchRecipe, result.Intent);
    }

    [Fact]
    public async Task ValidateDiet_Classified_WhenCapabilityRegistered()
    {
        // Same query, full registry — the intent is live, so routing is unchanged.
        var result = await MakeRouter(FullRegistry())
            .ClassifyAsync("is pasta safe for a nut allergy?");
        Assert.Equal(UserIntent.ValidateDiet, result.Intent);
    }

    [Fact]
    public async Task CreateMealPlan_FallsBackToSearch_WhenCapabilityNotRegistered()
    {
        var registry = new AgentRegistry(new Mock<ILogger<AgentRegistry>>().Object);
        registry.Register(new StubAgent("RecipeAgent", AgentCapabilities.SearchRecipe));

        var result = await MakeRouter(registry).ClassifyAsync("create a meal plan for dinner");

        Assert.Equal(UserIntent.SearchRecipe, result.Intent);
    }
}
