namespace ChefAgent.Shared;

using Microsoft.Extensions.Logging;

/// <summary>
/// Maps capability strings to the IAgent that handles them.
/// Built once at DI startup. Not yet consulted by AgentOrchestrator —
/// runs side-by-side with the old dispatch switch until Week 2's cutover.
/// </summary>
public class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _byCapability = new(StringComparer.Ordinal);
    private readonly ILogger<AgentRegistry> _logger;

    public AgentRegistry(ILogger<AgentRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers an agent for all of its declared capabilities.
    /// Throws if a capability is already claimed by another agent —
    /// this is a startup-time wiring bug, not something a request should
    /// silently route around.
    /// </summary>
    public void Register(IAgent agent)
    {
        foreach (var capability in agent.Capabilities)
        {
            if (_byCapability.TryGetValue(capability, out var existing))
            {
                throw new InvalidOperationException(
                    $"Duplicate capability registration: '{capability}' is already claimed by "
                        + $"'{existing.Name}', cannot also register to '{agent.Name}'. "
                        + "Each capability must map to exactly one agent."
                );
            }

            _byCapability[capability] = agent;
            _logger.LogInformation(
                "[AgentRegistry] Registered '{Capability}' -> {AgentName}",
                capability,
                agent.Name
            );
        }
    }

    /// <summary>
    /// Looks up the agent for a capability. Returns null if none is registered —
    /// e.g. GetMealPlan intentionally has no agent (reads straight from SessionStore).
    /// Callers must handle the null case explicitly.
    /// </summary>
    public IAgent? FindByCapability(string capability) =>
        _byCapability.TryGetValue(capability, out var agent) ? agent : null;

    public int Count => _byCapability.Count;
}
