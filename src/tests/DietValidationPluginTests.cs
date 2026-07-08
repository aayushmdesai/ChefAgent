using ChefAgent.Agents.Diet;
using ChefAgent.Shared;
using ChefAgent.Shared.Guardrails;
using ChefAgent.Shared.Models;
using ChefAgent.Shared.Observability;
using ChefAgent.Shared.Providers.Llm;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ChefAgent.Tests;

public class DietValidationPluginTests
{
    private DietValidationPlugin MakeAgent()
    {
        var cbLogger = new Mock<ILogger<CircuitBreaker>>().Object;
        var auditLogger = new Mock<ILogger<GuardrailAuditLog>>().Object;
        var audit = new GuardrailAuditLog(auditLogger);
        var circuitBreaker = new CircuitBreaker(cbLogger, audit);

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

        return new DietValidationPlugin(
            llmProvider.Object,
            circuitBreaker,
            tracing,
            new Mock<ILogger<DietValidationPlugin>>().Object
        );
    }

    private static RecipeDocument MakeRecipe(params string[] ingredients) =>
        new()
        {
            Id = "recipe-1",
            Title = "Test Recipe",
            Ingredients = ingredients.ToList(),
            Directions = ["Step 1"],
        };

    private static ClassifiedIntent MakeClassified(DietaryProfile? profile) =>
        new()
        {
            Intent = UserIntent.ValidateDiet,
            SearchQuery = "test recipe",
            ClassifiedBy = "rules",
            MergedProfile = profile,
        };

    [Fact]
    public async Task HandleAsync_NoProfile_ReturnsFailure()
    {
        var agent = MakeAgent();
        var context = new AgentContext
        {
            Classified = MakeClassified(null),
            TraceCtx = TraceContext.None,
        };

        var result = await agent.HandleAsync(context);

        Assert.False(result.Success);
        Assert.Contains("dietary profile", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NoTargetRecipe_ReturnsFailure()
    {
        var agent = MakeAgent();
        var profile = new DietaryProfile { Restrictions = ["vegan"] };
        var context = new AgentContext
        {
            Classified = MakeClassified(profile),
            TraceCtx = TraceContext.None,
        };

        var result = await agent.HandleAsync(context);

        Assert.False(result.Success);
        Assert.Contains("recipe", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_RulesViolation_ReturnsIncompatible()
    {
        var agent = MakeAgent();
        var profile = new DietaryProfile { Restrictions = ["vegan"] };
        var recipe = MakeRecipe("chicken breast", "olive oil", "garlic");
        var context = new AgentContext
        {
            Classified = MakeClassified(profile),
            TraceCtx = TraceContext.None,
            SharedData = new() { ["TargetRecipe"] = recipe },
        };

        var result = await agent.HandleAsync(context);

        Assert.True(result.Success);
        var validation = Assert.IsType<DietaryValidation>(result.Data);
        Assert.False(validation.IsCompatible);
        Assert.Contains(AgentCapabilities.ValidateDiet, result.OutputsForNextAgent!.Keys);
    }

    [Fact]
    public async Task HandleAsync_NoViolations_ReturnsCompatible()
    {
        var agent = MakeAgent();
        var profile = new DietaryProfile { Restrictions = ["vegan"] };
        var recipe = MakeRecipe("lentils", "rice", "spinach");
        var context = new AgentContext
        {
            Classified = MakeClassified(profile),
            TraceCtx = TraceContext.None,
            SharedData = new() { ["TargetRecipe"] = recipe },
        };

        var result = await agent.HandleAsync(context);

        Assert.True(result.Success);
        var validation = Assert.IsType<DietaryValidation>(result.Data);
        Assert.True(validation.IsCompatible);
    }
}
