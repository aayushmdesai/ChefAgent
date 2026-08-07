using ChefAgent.Shared.Models;

namespace ChefAgent.Shared;

/// <summary>
/// Every agent chain in the system, in one place.
///
/// Intents deliberately absent: GetMealPlan (reads SessionStore directly),
/// GeneralQuestion (plain LLM response), Unknown. PipelineRegistry.FindByIntent
/// returns null for these and the orchestrator falls back.
///
/// Single-step pipelines (ValidateDiet, CreateMealPlan, ModifyMealPlan) are
/// registered even though they add no chaining. The point is one code path at
/// the orchestrator: look up, run or fall back — rather than branching between
/// pipeline-backed and directly-dispatched intents.
/// </summary>
public static class PipelineDefinitions
{
    /// <summary>
    /// The SharedData key DietValidationPlugin.HandleAsync reads its target from.
    /// Must match that plugin — nothing enforces it at compile time.
    /// </summary>
    public const string TargetRecipeKey = "TargetRecipe";

    public static IEnumerable<AgentPipeline> BuildAll(AgentRegistry registry)
    {
        yield return SearchRecipe(registry);
        yield return ValidateDiet(registry);
        yield return CreateMealPlan(registry);
        yield return ModifyMealPlan(registry);
    }

    /// <summary>
    /// Search, then validate each returned recipe against the user's profile.
    /// Mirrors AgentOrchestrator.HandleSearchRecipeAsync: validation is skipped
    /// entirely without a profile, and a failed validation still returns the
    /// recipe — just without a dietary badge.
    /// </summary>
    private static AgentPipeline SearchRecipe(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe)
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: TargetRecipeKey,
                runIf: ctx => ctx.Classified.MergedProfile is not null,
                continueOnFailure: true
            )
            .Build();

    /// <summary>
    /// The orchestrator already holds the target recipe — no search, no fan-out.
    /// </summary>
    private static AgentPipeline ValidateDiet(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.ValidateDiet, registry)
            .Then(AgentCapabilities.ValidateDiet)
            .Build();

    private static AgentPipeline CreateMealPlan(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.CreateMealPlan, registry)
            .Then(AgentCapabilities.CreateMealPlan)
            .Build();

    private static AgentPipeline ModifyMealPlan(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.ModifyMealPlan, registry)
            .Then(AgentCapabilities.ModifyMealPlan)
            .Build();
}
