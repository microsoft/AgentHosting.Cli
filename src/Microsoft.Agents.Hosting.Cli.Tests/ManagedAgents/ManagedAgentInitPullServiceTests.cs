using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentInitServiceTests
{
    private static ManagedAgentInitService Create(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces) =>
        new(store, new AgentConfigLocator(), CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()), ManagedAgentFixtures.Resolver(), new CopilotProjectIdentityReader(new AgentConfigLocator()));

    [Fact]
    public async Task Init_RegistersAnUnconnectedProject()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var result = await Create(store, new FakeCopilotWorkspacePort { WorkspaceExists = false }).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent" },
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Init_WhenTheProjectFileSuppliesNoSchemaName_Fails()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(
            () => Create(store, new FakeCopilotWorkspacePort { WorkspaceExists = false }).InitAsync(
                new ManagedAgentInitRequest { Name = "Contoso Agent" },
                CancellationToken.None));

        Assert.Equal("workspace-schema-unreadable", failure.ErrorKind);
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Init_RecordsTheSchemaNameTheWorkspaceDeploysAs()
    {
        using var workspace = new TemporaryWorkspace(schemaName: "con_recorded");
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        await Create(store, new FakeCopilotWorkspacePort { WorkspaceExists = false }).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent" },
            CancellationToken.None);

        Assert.Equal("con_recorded", Assert.Single(store.List(workspace.Root)).McsSchemaName);
    }

    [Fact]
    public async Task Init_TakesTheEnvironmentFromAConnectedWorkspace()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent" },
            CancellationToken.None);

        Assert.True(result.Connected);
        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Init_RecordsNoCloudIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        await Create(store, new FakeCopilotWorkspacePort()).InitAsync(new ManagedAgentInitRequest { Name = "Contoso Agent" }, CancellationToken.None);

        Assert.Equal(AgentTypes.McsAgent, store.List(workspace.Root)[0].AgentType);
    }

    [Fact]
    public async Task Init_WritesNoProjectFiles()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var before = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Length;

        await Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(new ManagedAgentInitRequest { Name = "Contoso Agent" }, CancellationToken.None);

        Assert.Equal(before + 1, Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(workspace.ConfigPath));
    }

    [Fact]
    public async Task Init_OnADirectoryWithNoProject_PointsAtCreate()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent" },
            CancellationToken.None));

        Assert.Equal("project-not-found", failure.ErrorKind);
        Assert.Contains("msagent agent create", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_OnAnAlreadyRegisteredProject_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Another Name" },
            CancellationToken.None));

        Assert.Equal("already-registered", failure.ErrorKind);
    }

    [Fact]
    public async Task Init_WritesTheConfigNextToTheSourceDirectory()
    {
        using var workspace = new TemporaryWorkspace();
        using var elsewhere = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(elsewhere.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent", McsAgentSource = workspace.Root },
            CancellationToken.None);

        Assert.Equal(workspace.ConfigPath, result.ConfigPath);
        Assert.False(File.Exists(elsewhere.ConfigPath));
    }

    [Fact]
    public async Task Init_WithANonMcsAgentTypeIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent", AgentType = "CodeAgent" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Init_WithAMissingSourceDirectoryIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest { Name = "Contoso Agent", McsAgentSource = Path.Combine(workspace.Root, "nope") },
            CancellationToken.None));
    }

    [Fact]
    public async Task Init_WithoutANameIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort()).InitAsync(
            new ManagedAgentInitRequest(),
            CancellationToken.None));
    }
}

[Collection("CurrentDirectory")]
public sealed class ManagedAgentPullServiceTests
{
    private static ManagedAgentPullService Create(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces, FakeCopilotCatalogPort? catalog = null)
    {
        var environments = ManagedAgentFixtures.Resolver();
        var fileSystem = new SystemCopilotFileSystem();
        var locator = CopilotTestFixtures.Locator(workspaces, fileSystem);

        return new ManagedAgentPullService(
            ManagedAgentFixtures.AgentResolver(store),
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            environments);
    }

    [Fact]
    public async Task Pull_AConnectedDirectory_Pulls()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal("pull", result.Mode);
        Assert.Equal(1, workspaces.PullCalls);
        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Theory]
    [InlineData("dep-1", null)]
    [InlineData(null, "devwf")]
    public async Task Pull_WhenADeploymentIsNamed_RefusesRatherThanIgnoringIt(string? deploymentId, string? deploymentName)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, workspaces).PullAsync(
            new ManagedAgentPullRequest { DeploymentId = deploymentId, DeploymentName = deploymentName },
            CancellationToken.None));

        Assert.Contains("apply to a workflow only", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.PullCalls);
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    public async Task Pull_WhenTheWorkspaceRecordsAnotherOrigin_Fails(string organization)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort { DataverseOrganizationReturns = organization };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
        Assert.Equal(0, workspaces.PullCalls);
    }

    [Fact]
    public async Task Pull_ReportsTheEnvironmentResolvedFromTheInventory()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort { DataverseOrganizationReturns = CopilotTestFixtures.DataverseOrganization };

        var result = await Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Pull_WithNoRemoteChanges_ReportsUpToDate()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store, new FakeCopilotWorkspacePort()).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal("up-to-date", result.Status);
    }

    [Fact]
    public async Task Pull_WhenTheMergeLeavesConflicts_ReportsThemWithoutFailing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.PullWarnings.Add("1 workspace file has unresolved merge conflicts.");

        var result = await Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal("agent-pulled", result.Status);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Pull_WhenTheMergeLeavesConflicts_ShowsThemInTheTextOutput()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.PullWarnings.Add("1 workspace file has unresolved merge conflicts.");

        var result = await Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Contains(result.ToTextLines(), line => line.Contains("unresolved merge conflicts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_WithoutConflicts_ShowsNoWarningLines()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var result = await Create(store, new FakeCopilotWorkspacePort()).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Single(result.ToTextLines());
    }

    [Fact]
    public async Task Pull_ADirectoryHoldingOnlyTheConfiguration_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None));

        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Pull_ADisconnectedNonEmptyDirectory_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotWorkspacePort { WorkspaceExists = false })
            .PullAsync(new ManagedAgentPullRequest(), CancellationToken.None));

        Assert.Equal("workspace-disconnected", failure.ErrorKind);
    }

    [Fact]
    public async Task Pull_ForANonMcsRecord_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.CodeAgent });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotWorkspacePort())
            .PullAsync(new ManagedAgentPullRequest(), CancellationToken.None));

        Assert.Equal("not-an-mcs-agent", failure.ErrorKind);
    }

    [Fact]
    public async Task Pull_WithASecondRecordInTheProject_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());

        store.Add(workspace.Root, ManagedAgentFixtures.Agent("agent-2", "Other Agent") with { AgentLocation = "Other Agent" });

        Assert.Equal(2, store.List(workspace.Root).Count);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Pull_LeavesTheConfigurationByteIdentical()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        var before = File.ReadAllBytes(workspace.ConfigPath);

        await Create(store, new FakeCopilotWorkspacePort()).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(workspace.ConfigPath));
    }

    [Fact]
    public async Task Pull_WithAMissingProjectDirectoryIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotWorkspacePort())
            .PullAsync(new ManagedAgentPullRequest { Project = Path.Combine(workspace.Root, "nope") }, CancellationToken.None));
    }
}
