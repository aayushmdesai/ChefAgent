using ChefAgent.Shared.Models;

namespace ChefAgent.Shared;

/// <summary>
/// A single step in an agent pipeline.
///
/// RunIf              : step is skipped if this returns false
///                       (e.g. skip DietValidation when there's no dietary profile)
/// ContinueOnFailure   : a failed step doesn't abort the pipeline
///                       (matches HandleSearchRecipeAsync — a diet validation
///                       failure still returns the recipe, just without a badge)
/// FanOutFrom          : if set, the runner reads a list out of the parent
///                       context's SharedData at this key and calls this
///                       step's agent once per item, not once for the whole
///                       list. This is required, not optional — DietValidation
///                       only ever validates one recipe at a time (Day 2), so
///                       a SearchRecipe → ValidateDiet chain has no way to
///                       validate N recipes without fan-out at the pipeline
///                       layer.
/// </summary>
public record PipelineStep
{
    public required string Capability { get; init; }
    public Func<AgentContext, bool>? RunIf { get; init; }
    public bool ContinueOnFailure { get; init; } = false;
    public string? FanOutFrom { get; init; }
    public string? FanOutItemKey { get; init; }

    /// <summary>
    /// Langfuse span name for this step. Defaults to "pipeline.{Capability}".
    /// Set explicitly to preserve Phase 1 span names ("recipe_agent.search",
    /// "diet_agent.validate") so traces stay comparable across the cutover.
    /// </summary>
    public string? SpanName { get; init; }
}

public class AgentPipeline
{
    public UserIntent Intent { get; }
    public IReadOnlyList<PipelineStep> Steps { get; }

    internal AgentPipeline(UserIntent intent, IReadOnlyList<PipelineStep> steps)
    {
        Intent = intent;
        Steps = steps;
    }
}

public class PipelineBuilder
{
    private readonly UserIntent _intent;
    private readonly AgentRegistry _registry;
    private readonly List<PipelineStep> _steps = [];

    private PipelineBuilder(UserIntent intent, AgentRegistry registry)
    {
        _intent = intent;
        _registry = registry;
    }

    public static PipelineBuilder For(UserIntent intent, AgentRegistry registry) =>
        new(intent, registry);

    public PipelineBuilder Then(
        string capability,
        Func<AgentContext, bool>? runIf = null,
        bool continueOnFailure = false,
        string? spanName = null
    )
    {
        _steps.Add(
            new PipelineStep
            {
                Capability = capability,
                RunIf = runIf,
                ContinueOnFailure = continueOnFailure,
                SpanName = spanName,
            }
        );
        return this;
    }

    /// <summary>
    /// A step that runs once per item in a prior step's output list, rather
    /// than once for the whole context. fanOutFrom must match the SharedData
    /// key a prior step populated via OutputsForNextAgent (e.g.
    /// AgentCapabilities.SearchRecipe, which RecipeSearchPlugin.HandleAsync
    /// already writes today).
    /// </summary>
    public PipelineBuilder ThenForEach(
        string capability,
        string fanOutFrom,
        string fanOutItemKey,
        Func<AgentContext, bool>? runIf = null,
        bool continueOnFailure = false,
        string? spanName = null
    )
    {
        _steps.Add(
            new PipelineStep
            {
                Capability = capability,
                RunIf = runIf,
                ContinueOnFailure = continueOnFailure,
                FanOutFrom = fanOutFrom,
                FanOutItemKey = fanOutItemKey,
                SpanName = spanName,
            }
        );
        return this;
    }

    public AgentPipeline Build()
    {
        if (_steps.Count == 0)
            throw new InvalidOperationException($"Pipeline for '{_intent}' has no steps.");

        foreach (var step in _steps)
        {
            if (step.FanOutFrom is not null && string.IsNullOrWhiteSpace(step.FanOutItemKey))
                throw new InvalidOperationException(
                    $"Pipeline '{_intent}' step '{step.Capability}' sets FanOutFrom but no FanOutItemKey."
                );
            if (_registry.FindByCapability(step.Capability) is null)
                throw new InvalidOperationException(
                    $"Pipeline '{_intent}' references unregistered capability '{step.Capability}'."
                );
        }

        return new AgentPipeline(_intent, _steps);
    }
}
