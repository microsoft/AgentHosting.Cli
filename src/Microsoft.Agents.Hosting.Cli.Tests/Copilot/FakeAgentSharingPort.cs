using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class FakeAgentSharingPort : IAgentSharingPort
{
    private readonly Dictionary<string, AgentSharingState> _states = new(StringComparer.OrdinalIgnoreCase);

    public List<(string AgentId, AgentSharingState State)> Writes { get; } = [];

    public int ReadCalls { get; private set; }

    /// <summary>What a write reports back, when the service is to disagree with what was asked for.</summary>
    public AgentSharingState? WriteReturns { get; set; }

    public FakeAgentSharingPort WithState(string agentId, AgentSharingState state)
    {
        _states[agentId] = state;

        return this;
    }

    public FakeAgentSharingPort WithTargets(string agentId, params AgentShareTarget[] targets) =>
        WithState(agentId, new AgentSharingState { Targets = targets });

    public AgentSharingState StateOf(string agentId) => _states.GetValueOrDefault(agentId, new AgentSharingState());

    public Task<AgentSharingState> ReadAsync(CopilotEnvironmentContext environment, string agentId, CancellationToken cancellationToken)
    {
        ReadCalls++;

        return Task.FromResult(StateOf(agentId));
    }

    public Task<AgentSharingState> WriteAsync(CopilotEnvironmentContext environment, string agentId, AgentSharingState state, CancellationToken cancellationToken)
    {
        Writes.Add((agentId, state));

        var applied = WriteReturns ?? state;
        _states[agentId] = applied;

        return Task.FromResult(applied);
    }
}

internal sealed class FakeDirectoryPrincipalPort : IDirectoryPrincipalPort
{
    private readonly Dictionary<string, DirectoryPrincipal> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DirectoryPrincipal> _groups = new(StringComparer.OrdinalIgnoreCase);

    public int ResolveManyCalls { get; private set; }

    public FakeDirectoryPrincipalPort WithUser(string objectId, string displayName, params string[] selectors)
    {
        var principal = new DirectoryPrincipal { ObjectId = objectId, DisplayName = displayName, PrincipalType = "User", SignInAddress = selectors.FirstOrDefault() };

        foreach (var selector in selectors.Append(objectId))
        {
            _users[selector] = principal;
        }

        return this;
    }

    public FakeDirectoryPrincipalPort WithGroup(string objectId, string displayName, params string[] selectors)
    {
        var principal = new DirectoryPrincipal { ObjectId = objectId, DisplayName = displayName, PrincipalType = "Group" };

        foreach (var selector in selectors.Append(objectId))
        {
            _groups[selector] = principal;
        }

        return this;
    }

    public Task<DirectoryPrincipal?> ResolveUserAsync(CopilotEnvironmentContext environment, string selector, CancellationToken cancellationToken) =>
        Task.FromResult(_users.GetValueOrDefault(selector));

    public Task<IReadOnlyDictionary<string, DirectoryPrincipal>> ResolveManyAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken)
    {
        ResolveManyCalls++;

        var known = _users.Values.Concat(_groups.Values).DistinctBy(principal => principal.ObjectId, StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyDictionary<string, DirectoryPrincipal>>(
            known.Where(principal => objectIds.Contains(principal.ObjectId, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(principal => principal.ObjectId, principal => principal, StringComparer.OrdinalIgnoreCase));
    }
}

internal sealed class ThrowingDirectoryPrincipalPort : IDirectoryPrincipalPort
{
    public ThrowingDirectoryPrincipalPort(Exception? failure = null) => _failure = failure ?? new HttpRequestException("directory unreachable");

    private readonly Exception _failure;

    public Task<DirectoryPrincipal?> ResolveUserAsync(CopilotEnvironmentContext environment, string selector, CancellationToken cancellationToken) =>
        throw _failure;

    public Task<IReadOnlyDictionary<string, DirectoryPrincipal>> ResolveManyAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken) =>
        throw _failure;
}

internal sealed class SignedOutDirectoryPrincipalPort : IDirectoryPrincipalPort
{
    public Task<DirectoryPrincipal?> ResolveUserAsync(CopilotEnvironmentContext environment, string selector, CancellationToken cancellationToken) =>
        throw new NotSignedInException("not signed in", remediation: "run msagent auth login");

    public Task<IReadOnlyDictionary<string, DirectoryPrincipal>> ResolveManyAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken) =>
        throw new NotSignedInException("not signed in", remediation: "run msagent auth login");
}
