using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Copy;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Lists and finds an agent's resources through the sync library, and deletes them over the Dataverse Web API.</summary>
public sealed class DataverseAgentResourceAdapter : ICopilotAgentResourcePort
{
    private const string ApiRoot = "api/data/v9.2/";

    private readonly IWorkflowStorePort _workflows;
    private readonly ICopyDataverseClient _copyClient;
    private readonly DataverseWebApiClient _webApi;

    /// <summary>Creates the adapter.</summary>
    /// <param name="workflows">Deletes a flow, turning it off first.</param>
    /// <param name="copyClient">The sync library's client that finds which resources an environment holds.</param>
    /// <param name="webApi">Targets the environment and sends the deletes.</param>
    public DataverseAgentResourceAdapter(IWorkflowStorePort workflows, ICopyDataverseClient copyClient, DataverseWebApiClient webApi)
    {
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _copyClient = copyClient ?? throw new ArgumentNullException(nameof(copyClient));
        _webApi = webApi ?? throw new ArgumentNullException(nameof(webApi));
    }

    /// <inheritdoc />
    public IReadOnlyList<CopilotCopyLink> List(string workspaceDirectory)
    {
        try
        {
            return [.. AgentWorkspaceResources.List(workspaceDirectory).Select(ToLink)];
        }
        catch (InvalidDataException unreadable)
        {
            throw CopilotStudioException.WorkspaceFilesUnreadable(unreadable.Message, unreadable);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CopilotCopyLink>> FindExistingAsync(CopilotEnvironmentContext environment, IReadOnlyList<CopilotCopyLink> resources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resources);

        _webApi.Connect(environment);

        return [.. (await AgentWorkspaceResources.FindExistingAsync(_copyClient, [.. resources.Select(resource => new AgentWorkspaceResource(resource.Kind, resource.Identity))], cancellationToken).ConfigureAwait(false)).Select(ToLink)];
    }

    /// <inheritdoc />
    public Task DeleteAsync(CopilotEnvironmentContext environment, CopilotCopyLink resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(resource);

        return resource.Kind switch
        {
            CopilotCopyIdentityKinds.Workflow => _workflows.DeleteAsync(environment, ResourceId(resource), cancellationToken),
            CopilotCopyIdentityKinds.AIModel => SendDeleteAsync(environment, $"{ApiRoot}msdyn_aimodels({ResourceId(resource)})", cancellationToken),
            CopilotCopyIdentityKinds.Connector => SendDeleteAsync(environment, $"{ApiRoot}connectors({ResourceId(resource)})", cancellationToken),
            CopilotCopyIdentityKinds.ConnectionReference => _webApi.DeleteConnectionReferenceAsync(environment, resource.Identity, cancellationToken),
            _ => throw new ArgumentException($"An agent holds no {resource.Kind} resource.", nameof(resource)),
        };
    }

    private static CopilotCopyLink ToLink(AgentWorkspaceResource resource) => new(resource.Kind, resource.Identity);

    private async Task SendDeleteAsync(CopilotEnvironmentContext environment, string route, CancellationToken cancellationToken) => (await _webApi.SendAsync(environment, HttpMethod.Delete, route, cancellationToken).ConfigureAwait(false))?.Dispose();

    private static Guid ResourceId(CopilotCopyLink resource) => Guid.TryParse(resource.Identity, out var resourceId) ? resourceId : throw new ArgumentException($"The {resource.Kind} {resource.Identity} is not named by an identifier.", nameof(resource));
}
