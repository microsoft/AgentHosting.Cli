using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Publishes agents through the sync library's publish service, reporting anything but a publish Copilot Studio finished as Succeeded as the CLI's typed failure.</summary>
public sealed class SyncAgentPublishAdapter : ICopilotPublishPort
{
    private readonly IAgentPublishDataverseClient _publishClient;
    private readonly IAgentPublishService _publishService;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the adapter.</summary>
    /// <param name="publishClient">The sync library's publish client, pointed at the agent's organization before each publish.</param>
    /// <param name="publishService">The sync library's service that publishes an agent and waits for Copilot Studio to finish.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the target organization.</param>
    public SyncAgentPublishAdapter(IAgentPublishDataverseClient publishClient, IAgentPublishService publishService, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _publishClient = publishClient ?? throw new ArgumentNullException(nameof(publishClient));
        _publishService = publishService ?? throw new ArgumentNullException(nameof(publishService));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> PublishAsync(CopilotEnvironmentContext environment, string agentId, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        DataverseTargeting.Connect(_publishClient, _httpClientAccessor, environment);

        var outcome = await SyncDataverseCall.RunAsync("publish agent", () => _publishService.PublishAsync(Guid.TryParse(agentId, out var parsedAgentId) ? parsedAgentId : throw CopilotStudioException.AgentNotFound(agentId), maximumWait, cancellationToken)).ConfigureAwait(false);

        if (outcome.Kind != AgentPublishOutcomeKind.Succeeded)
        {
            throw outcome switch
            {
                { Kind: AgentPublishOutcomeKind.TimedOut, FailureReason: { } provisioning } => CopilotStudioException.AgentNotProvisioned(provisioning),
                { Kind: AgentPublishOutcomeKind.TimedOut } => CopilotStudioException.PublishTimeout(agentId, maximumWait),
                _ => CopilotStudioException.PublishFailed(agentId, outcome.Status, outcome.FailureReason),
            };
        }

        return outcome.FinishedOn;
    }
}
