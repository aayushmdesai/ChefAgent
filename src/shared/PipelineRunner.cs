using Microsoft.Extensions.Logging;

namespace ChefAgent.Shared;

/// <summary>
/// Outcome of one step. For a fan-out step, FanOutResults holds one entry per
/// item and Result is a synthetic roll-up (Success = every item succeeded).
/// </summary>
public record StepResult
{
    public required string Capability { get; init; }
    public required bool Skipped { get; init; }
    public AgentResult? Result { get; init; }
    public IReadOnlyList<(object Item, AgentResult Result)>? FanOutResults { get; init; }
}

public record PipelineResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<StepResult> Steps { get; init; }
    public required Dictionary<string, object> SharedData { get; init; }
    public string? AbortedAt { get; init; }
}

public class PipelineRunner(AgentRegistry registry, ILogger<PipelineRunner> logger)
{
    private readonly AgentRegistry _registry = registry;
    private readonly ILogger<PipelineRunner> _logger = logger;

    public async Task<PipelineResult> RunAsync(
        AgentPipeline pipeline,
        AgentContext context,
        CancellationToken ct = default
    )
    {
        var steps = new List<StepResult>();
        var shared = context.SharedData;

        foreach (var step in pipeline.Steps)
        {
            var current = context with { SharedData = shared };

            if (step.RunIf is not null && !step.RunIf(current))
            {
                _logger.LogDebug(
                    "[{CorrelationId}] Pipeline '{Intent}': step '{Capability}' skipped by RunIf",
                    context.TraceCtx.CorrelationId,
                    pipeline.Intent,
                    step.Capability
                );
                steps.Add(new StepResult { Capability = step.Capability, Skipped = true });
                continue;
            }

            // Build() already guarantees this resolves.
            var agent = _registry.FindByCapability(step.Capability)!;

            var stepResult = step.FanOutFrom is null
                ? await RunSingleAsync(agent, step, current, shared, ct)
                : await RunFanOutAsync(agent, step, current, shared, ct);

            steps.Add(stepResult);

            var failed = stepResult.Result is { Success: false };
            if (failed && !step.ContinueOnFailure)
            {
                _logger.LogWarning(
                    "[{CorrelationId}] Pipeline '{Intent}' aborted at '{Capability}': {Error}",
                    context.TraceCtx.CorrelationId,
                    pipeline.Intent,
                    step.Capability,
                    stepResult.Result?.ErrorMessage
                );
                return new PipelineResult
                {
                    Success = false,
                    Steps = steps,
                    SharedData = shared,
                    AbortedAt = step.Capability,
                };
            }
        }

        return new PipelineResult
        {
            Success = true,
            Steps = steps,
            SharedData = shared,
        };
    }

    private async Task<StepResult> RunSingleAsync(
        IAgent agent,
        PipelineStep step,
        AgentContext context,
        Dictionary<string, object> shared,
        CancellationToken ct
    )
    {
        var result = await InvokeAsync(agent, context, ct);
        Merge(shared, result);
        return new StepResult
        {
            Capability = step.Capability,
            Skipped = false,
            Result = result,
        };
    }

    private async Task<StepResult> RunFanOutAsync(
        IAgent agent,
        PipelineStep step,
        AgentContext context,
        Dictionary<string, object> shared,
        CancellationToken ct
    )
    {
        if (
            !shared.TryGetValue(step.FanOutFrom!, out var raw)
            || raw is not System.Collections.IEnumerable enumerable
            || raw is string
        )
        {
            var error =
                $"Fan-out step '{step.Capability}' found no enumerable at SharedData key "
                + $"'{step.FanOutFrom}'. The prior step did not produce what this step expects.";
            _logger.LogError("[{CorrelationId}] {Error}", context.TraceCtx.CorrelationId, error);
            return new StepResult
            {
                Capability = step.Capability,
                Skipped = false,
                Result = new AgentResult { Success = false, ErrorMessage = error },
            };
        }

        var perItem = new List<(object Item, AgentResult Result)>();

        foreach (var item in enumerable)
        {
            if (item is null)
                continue;

            // Each branch gets its own SharedData AND its own ClassifiedIntent.
            // ClassifiedIntent has mutable setters (SessionId, TargetDay,
            // TargetSlot, ModifyConstraint) — sharing one instance across N
            // branches would let one item's agent corrupt its siblings.
            var itemShared = new Dictionary<string, object>(shared)
            {
                [step.FanOutItemKey!] = item,
            };

            var itemContext = context with
            {
                Classified = context.Classified with { },
                SharedData = itemShared,
            };

            var result = await InvokeAsync(agent, itemContext, ct);
            perItem.Add((item, result));

            if (!result.Success && !step.ContinueOnFailure)
                break;
        }

        var allSucceeded = perItem.All(r => r.Result.Success);

        // Fan-out outputs are per-item and can't be flattened into SharedData
        // the way a single step's can — the collection itself is the output.
        shared[step.Capability] = perItem;

        return new StepResult
        {
            Capability = step.Capability,
            Skipped = false,
            Result = new AgentResult
            {
                Success = allSucceeded,
                ErrorMessage = allSucceeded
                    ? null
                    : $"{perItem.Count(r => !r.Result.Success)} of {perItem.Count} items failed.",
            },
            FanOutResults = perItem,
        };
    }

    private async Task<AgentResult> InvokeAsync(
        IAgent agent,
        AgentContext context,
        CancellationToken ct
    )
    {
        try
        {
            return await agent.HandleAsync(context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[{CorrelationId}] Agent '{Agent}' threw during pipeline execution",
                context.TraceCtx.CorrelationId,
                agent.Name
            );
            return new AgentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static void Merge(Dictionary<string, object> shared, AgentResult result)
    {
        if (result.OutputsForNextAgent is null)
            return;
        foreach (var (key, value) in result.OutputsForNextAgent)
            shared[key] = value;
    }
}