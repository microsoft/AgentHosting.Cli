using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class PushCloudCacheTests
{
    [Fact]
    public async Task Push_WhenDataverseDoesNotConfirmADeclaredReference_NeverPushesOrRefreshes()
    {
        using var workspace = new ClassicWorkspace();
        var synchronizer = CopilotTestFixtures.FailingOnce(CopilotTestFixtures.MissingEntity());
        var retarget = new RecordingRetargetService();

        await Assert.ThrowsAsync<CopilotStudioException>(
            () => CopilotTestFixtures.WorkspaceAdapter(synchronizer, retarget, new RecordingConnectionReferenceClient { OmitProvisionedReferences = true }).PushAsync(workspace.Info, overwrite: false, CancellationToken.None));

        Assert.Equal(0, synchronizer.PushLocalChangesCalls);
        Assert.Empty(retarget.Calls);
    }
}

internal sealed class ClassicWorkspace : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ah-classic-" + Guid.NewGuid().ToString("N"));

    public ClassicWorkspace()
    {
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, "connectionreferences.mcs.yml"), """
connectionReferences:
  - connectionReferenceLogicalName: crf9a_Classic.shared_teams.0add1954
    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams
""");
    }

    public CopilotWorkspaceInfo Info => new()
    {
        WorkspacePath = _path,
        AgentId = "11111111-2222-3333-4444-555555555555",
        EnvironmentId = "env-1",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    public void Dispose()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, recursive: true);
        }
    }
}
