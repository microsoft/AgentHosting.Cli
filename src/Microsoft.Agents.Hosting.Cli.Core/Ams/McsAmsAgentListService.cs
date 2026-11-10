using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The <c>msagent agent list --view ams</c> application service, which lists the environment's AMS agent records through the scope rules of the cloud listing.</summary>
public sealed class McsAmsAgentListService : ICommandHandler<McsAmsAgentListRequest, ManagedAgentListResult>
{
    /// <summary>The page size the AMS view asks for.</summary>
    public const int RecordPageSize = 100;

    private readonly ManagedAgentListService _list;
    private readonly AmsEndpointProfile _endpoints;
    private readonly IAmsMcsAgentReadTransport _agents;

    /// <summary>Creates the service.</summary>
    /// <param name="list">Applies the scope rules, tenant fan-out, truncation and projection of the cloud listing.</param>
    /// <param name="endpoints">Resolves the data-plane endpoints.</param>
    /// <param name="agents">Reads the agent records.</param>
    public McsAmsAgentListService(ManagedAgentListService list, AmsEndpointProfile endpoints, IAmsMcsAgentReadTransport agents)
    {
        _list = list ?? throw new ArgumentNullException(nameof(list));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    /// <inheritdoc />
    public Task<ManagedAgentListResult> HandleAsync(McsAmsAgentListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _list.ListAsync(request.Options with { View = ManagedAgentListViews.Ams }, ReaderFor, cancellationToken);
    }

    private ManagedAgentListReader ReaderFor(ManagedAgentListRequest request)
    {
        var repositoryId = request.RepositoryId is null ? null : GrsRemote.RequireRepositoryId(request.RepositoryId);
        return (environment, maximumCount, cancellationToken) => ReadRecordsAsync(_endpoints.ResolveDataPlane(environment), request.AgentType, repositoryId, maximumCount, cancellationToken);
    }

    private async Task<ManagedAgentListPage> ReadRecordsAsync(AmsServiceTarget target, string? agentType, string? repositoryId, int maximumCount, CancellationToken cancellationToken)
    {
        var rows = new List<ManagedAgentListItem>();
        string? skipToken = null;
        do
        {
            var page = await _agents.ListAgentsAsync(target, Math.Min(RecordPageSize, maximumCount - rows.Count), agentType, repositoryId, skipToken, cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Agents.Select(McsAmsViews.ListItem));
            skipToken = page.NextSkipToken;
        }
        while (skipToken is not null && rows.Count < maximumCount);

        return new ManagedAgentListPage { Agents = [.. rows.Take(maximumCount)] };
    }
}