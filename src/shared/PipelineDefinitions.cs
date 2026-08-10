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
    /// when there is no profile OR the profile carries no allergies and no
    /// restrictions. LoadAndMergeProfileAsync returns a non-null profile whenever
    /// either side exists, so a null check alone would run validation against an
    /// empty profile — five pointless calls and a confidence downgrade.
    /// A failed validation still returns the recipe, just without a dietary badge.
    /// </summary>
    private static AgentPipeline SearchRecipe(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.SearchRecipe, registry)
            .Then(AgentCapabilities.SearchRecipe, spanName: "recipe_agent.search")
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: TargetRecipeKey,
                runIf: HasActionableProfile,
                continueOnFailure: true,
                spanName: "diet_agent.validate"
            )
            .Build();

    /// <summary>
    /// The real diet-validation gate. Shared so ValidateDiet and any future
    /// profile-gated step can't drift from SearchRecipe's version.
    /// </summary>
    public static bool HasActionableProfile(AgentContext ctx)
    {
        var profile = ctx.Classified.MergedProfile;
        return profile is not null
            && (profile.Allergies.Count > 0 || profile.Restrictions.Count > 0);
    }

    /// <summary>
    /// The SharedData key RecipeSearchPlugin.HandleAsync reads its result cap from.
    /// ValidateDiet wants exactly one recipe, not the default 5.
    /// </summary>
    public const string MaxResultsKey = "maxResults";

    /// <summary>
    /// Mirrors AgentOrchestrator.HandleValidateDietAsync, which is NOT a bare
    /// validation call: it searches first (maxResults: 1), takes the top result,
    /// then validates it. A single ValidateDiet step would find no TargetRecipe
    /// in SharedData and fail every request.
    ///
    /// Fan-out over a one-element list rather than a plain Then, so the item-key
    /// wiring is shared with SearchRecipe instead of duplicated. The caller must
    /// seed SharedData[MaxResultsKey] = 1 — see MapValidateDietResult.
    /// </summary>
    private static AgentPipeline ValidateDiet(AgentRegistry registry) =>
        PipelineBuilder
            .For(UserIntent.ValidateDiet, registry)
            .Then(AgentCapabilities.SearchRecipe, spanName: "recipe_agent.search")
            .ThenForEach(
                AgentCapabilities.ValidateDiet,
                fanOutFrom: AgentCapabilities.SearchRecipe,
                fanOutItemKey: TargetRecipeKey,
                spanName: "diet_agent.validate"
            )
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
