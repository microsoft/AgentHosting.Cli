using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class ConnectionReferenceFailureTranslationTests : IDisposable
{
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), "ah-reference-translation-" + Guid.NewGuid().ToString("N"));

    public ConnectionReferenceFailureTranslationTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
        {
            Directory.Delete(_workspacePath, recursive: true);
        }
    }

    private string Declare(string relativePath, string logicalName, string connectorInternalId)
    {
        var path = Path.Combine(_workspacePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"connectionReferences:\n  - connectionReferenceLogicalName: {logicalName}\n    connectorId: /providers/Microsoft.PowerApps/apis/{connectorInternalId}\n");

        return path;
    }

    private Task<CopilotPushOutcome> ProvisionAsync(RecordingConnectionReferenceClient client) =>
        CopilotTestFixtures.WorkspaceAdapter(new RecordingWorkspaceSynchronizer(), dataverseClient: client).PushAsync(
            new CopilotWorkspaceInfo
            {
                WorkspacePath = _workspacePath,
                AgentId = "11111111-2222-3333-4444-555555555555",
                EnvironmentId = "env-1",
                DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
            },
            overwrite: false,
            CancellationToken.None);

    [Fact]
    public async Task Provision_AConflictingDeclaration_NamesBothFilesByTheirPathInTheWorkspace()
    {
        var first = Declare("connectionreferences.mcs.yml", "cr_teams", "shared_teams");
        var second = Declare(Path.Combine("infrastructure", "connections", "cr_teams.sync.yaml"), "cr_teams", "shared_office365");
        var client = new RecordingConnectionReferenceClient();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(client));

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesConflict, failure.ErrorKind);
        Assert.Equal($"Connection reference 'cr_teams' is declared for two connectors: 'shared_teams' at '{first}' and 'shared_office365' at '{second}'.", failure.Message);
        Assert.Empty(client.EnsuredLogicalNames);
    }

    [Fact]
    public async Task Provision_ADeclarationWithoutALogicalName_NamesTheFileAndTheReason()
    {
        var path = Declare("connectionreferences.mcs.yml", "''", "shared_teams");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(new RecordingConnectionReferenceClient()));

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesUnreadable, failure.ErrorKind);
        Assert.Equal($"The connection references declared at '{path}' could not be read: a declared reference has no connection reference logical name", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task Provision_ADeclarationThatCannotBeOpened_KeepsTheCause()
    {
        var path = Declare(Path.Combine("infrastructure", "connections", "cr_teams.sync.yaml"), "cr_teams", "shared_teams");
        CopilotStudioException failure;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(new RecordingConnectionReferenceClient()));
        }

        Assert.Equal(CopilotErrorKinds.ConnectionReferencesUnreadable, failure.ErrorKind);
        Assert.StartsWith($"The connection references declared at '{path}' could not be read: ", failure.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task Provision_AReferenceTheTargetDidNotConfirm_IsAServiceError()
    {
        Declare("connectionreferences.mcs.yml", "classic.shared_teams", "shared_teams");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => ProvisionAsync(new RecordingConnectionReferenceClient { OmitProvisionedReferences = true }));

        Assert.Equal(CopilotErrorKinds.ServiceError, failure.ErrorKind);
        Assert.Equal("The Copilot Studio service reported a failure during 'provision connection references'. The target did not confirm these connection references as the workspace declares them: classic.shared_teams. Nothing was recorded as deployed.", failure.Message);
    }

    [Fact]
    public void TranslateAny_LeavesAnyOtherFailureUnchanged()
    {
        var failure = new InvalidOperationException("the service is unavailable");

        Assert.Same(failure, ConnectionReferenceFailureTranslation.TranslateAny(failure, _workspacePath));
    }
}