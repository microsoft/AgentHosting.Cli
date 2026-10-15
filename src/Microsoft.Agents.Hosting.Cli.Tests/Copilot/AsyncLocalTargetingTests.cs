using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

internal sealed class AsyncLocalDataverseTarget
{
    private readonly AsyncLocal<string> _dataverseUrl = new();

    public string RequireUrl() => _dataverseUrl.Value
        ?? throw new InvalidOperationException("Dataverse URL is not set. Call SetDataverseUrl before making API calls.");

    public void SetDataverseUrl(string url) => _dataverseUrl.Value = url;
}

internal sealed class AsyncLocalWorkspacePort : ICopilotWorkspacePort
{
    private readonly AsyncLocalDataverseTarget _target = new();

    public bool IsWorkspace(string directory) => true;

    public Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken) =>
        Task.FromResult(new CopilotWorkspaceInfo
        {
            WorkspacePath = directory,
            AgentId = "agent-1",
            EnvironmentId = "env-1",
            DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
        });

    public async Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken)
    {
        _target.SetDataverseUrl(command.Environment.DataverseOrganization.ToString());
        await Task.Yield();
        _target.RequireUrl();

        return new CopilotCloneOutcome
        {
            SchemaName = "con_agent",
            DisplayName = "Contoso Agent",
            AgentWorkspacePath = command.WorkspacePath,
            Workspaces = [new CopilotClonedWorkspace { Kind = "agent", Path = command.WorkspacePath, DisplayName = "Contoso Agent" }],
        };
    }

    public async Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        await ConnectAsync(workspace).ConfigureAwait(false);

        return new CopilotChangeSnapshot { Local = [], Remote = [] };
    }

    public async Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        await ConnectAsync(workspace).ConfigureAwait(false);

        return new CopilotPullOutcome { Changes = [] };
    }

    public async Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken)
    {
        await ConnectAsync(workspace).ConfigureAwait(false);

        return new CopilotPushOutcome { Changes = [] };
    }


    private async Task ConnectAsync(CopilotWorkspaceInfo workspace)
    {
        _target.SetDataverseUrl(workspace.DataverseOrganization.ToString());
        await Task.Yield();
        _target.RequireUrl();
    }
}

public sealed class AsyncLocalTargetingTests
{
    [Fact]
    public async Task SettingTheTargetInsideANestedAsyncMethod_IsNotVisibleToTheCaller()
    {
        var target = new AsyncLocalDataverseTarget();

        await SetInsideNestedMethodAsync(target);

        Assert.Throws<InvalidOperationException>(target.RequireUrl);
    }

    [Fact]
    public async Task SettingTheTargetInTheCallingMethod_IsVisibleToItsCallees()
    {
        var target = new AsyncLocalDataverseTarget();
        target.SetDataverseUrl("https://contoso.crm.dynamics.com/");

        Assert.Equal("https://contoso.crm.dynamics.com/", await ReadFromNestedMethodAsync(target));
    }

    private static async Task SetInsideNestedMethodAsync(AsyncLocalDataverseTarget target)
    {
        await Task.Yield();
        target.SetDataverseUrl("https://contoso.crm.dynamics.com/");
    }

    private static async Task<string> ReadFromNestedMethodAsync(AsyncLocalDataverseTarget target)
    {
        await Task.Yield();
        return target.RequireUrl();
    }
}
