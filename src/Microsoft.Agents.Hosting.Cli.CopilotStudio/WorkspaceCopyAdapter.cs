using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Copy;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Prepares copies through the sync library's copy service.</summary>
public sealed class WorkspaceCopyAdapter : ICopilotCopyPort
{
    private readonly IWorkspaceCopyService _copies;
    private readonly ICopyDataverseClient _copyClient;
    private readonly ISyncDataverseClient _dataverseClient;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the adapter.</summary>
    /// <param name="copies">Prepares copies.</param>
    /// <param name="copyClient">Reads which identities an environment already holds.</param>
    /// <param name="dataverseClient">Targets the environment the copy client reads.</param>
    /// <param name="httpClientAccessor">Targets the Dataverse organization.</param>
    public WorkspaceCopyAdapter(IWorkspaceCopyService copies, ICopyDataverseClient copyClient, ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _copies = copies ?? throw new ArgumentNullException(nameof(copies));
        _copyClient = copyClient ?? throw new ArgumentNullException(nameof(copyClient));
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <inheritdoc />
    public async Task<CopilotCopyPreparation> PrepareAsync(CopilotCopyPrepareCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Environment is not null)
        {
            DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, command.Environment);
        }

        try
        {
            var prepared = await _copies.PrepareAsync(ToRequest(command, command.Environment is null ? null : _copyClient), cancellationToken).ConfigureAwait(false);

            return new CopilotCopyPreparation
            {
                Identities = [.. prepared.Identities.Select(ToIdentity)],
                Notices = [.. prepared.Audit.Notices.Select(ToFinding)],
                RewrittenFiles = prepared.RewrittenFiles,
                RenamedPaths = [.. prepared.RenamedPaths.Select(ToIdentity)],
                DraftWorkflows = prepared.DraftWorkflows,
            };
        }
        catch (CopyPreparationException blocked)
        {
            return new CopilotCopyPreparation { Blockers = [.. blocked.Findings.Select(ToFinding)] };
        }
    }

    /// <inheritdoc />
    public Task<string?> ProposeSchemaNameAsync(string workspaceDirectory, string sourceSchemaName, string displayName, CancellationToken cancellationToken) => _copies.ProposeSchemaNameAsync(workspaceDirectory, sourceSchemaName, displayName, cancellationToken);

    private static CopyPreparationRequest ToRequest(CopilotCopyPrepareCommand command, ICopyDataverseClient? targetEnvironment) => new()
    {
        WorkspacePath = command.WorkspaceDirectory,
        SourceSchemaName = command.SourceSchemaName,
        DisplayName = command.DisplayName,
        SchemaName = command.SchemaName,
        SourceDefinitionPath = command.SourceDefinitionPath,
        CapturedInTargetEnvironment = command.CapturedInTargetEnvironment,
        TargetEnvironment = targetEnvironment,
    };

    private static CopilotCopyIdentity ToIdentity(CopyIdentityMapping mapping) => new(mapping.Kind, mapping.Source, mapping.Destination);

    private static CopilotCopyFinding ToFinding(CopyAuditFinding finding) => new(finding.Code, finding.Message, finding.Path);
}