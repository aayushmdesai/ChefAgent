using ChefAgent.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared;

/// <summary>
/// Maps a classified intent to the pipeline that serves it. Built once at DI
/// startup, same as AgentRegistry.
///
/// Not every intent has a pipeline, and that is a valid state — not an error:
///   GetMealPlan      reads straight from SessionStore, no agent involved
///   GeneralQuestion  is a plain LLM response, no agent involved
///   Unknown          never routes anywhere
/// FindByIntent returns null for these. The caller decides the fallback,
/// exactly as AgentRegistry.FindByCapability does for GetMealPlan.
/// </summary>
public class PipelineRegistry(ILogger<PipelineRegistry> logger)
{
    private readonly Dictionary<UserIntent, AgentPipeline> _pipelines = [];
    private readonly ILogger<PipelineRegistry> _logger = logger;

    public int Count => _pipelines.Count;

    public void Register(AgentPipeline pipeline)
    {
        if (_pipelines.ContainsKey(pipeline.Intent))
            throw new InvalidOperationException(
                $"A pipeline is already registered for intent '{pipeline.Intent}'."
            );

        _pipelines[pipeline.Intent] = pipeline;
        _logger.LogInformation(
            "Registered pipeline for '{Intent}' with {StepCount} step(s)",
            pipeline.Intent,
            pipeline.Steps.Count
        );
    }

    public AgentPipeline? FindByIntent(UserIntent intent) =>
        _pipelines.TryGetValue(intent, out var pipeline) ? pipeline : null;

    public IReadOnlyCollection<UserIntent> RegisteredIntents => _pipelines.Keys;
}
