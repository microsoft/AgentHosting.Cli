using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentDeployServiceTests
{
    private static ManagedAgentDeployService Create(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces, RecordingAttachPort attach, FakeCopilotCatalogPort? catalog = null, WorkflowActivationPass? activation = null, IProgressStatus? progress = null)
    {
        var resolvedCatalog = catalog ?? new FakeCopilotCatalogPort();
        var environments = ManagedAgentFixtures.Resolver();
        var fileSystem = new SystemCopilotFileSystem();
        var locator = CopilotTestFixtures.Locator(workspaces, fileSystem);

        return new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            ManagedAgentFixtures.Identities(),
            attach,
            resolvedCatalog,
            new CopilotCatalogService(resolvedCatalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            activation ?? ManagedAgentFixtures.ActivationPass(),
            new FixedClock(ManagedAgentFixtures.Now),
            progress);
    }

    [Fact]
    public async Task Deploy_ActivatesTheWorkflowsWhoseConnectionsAreBound()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });
        connections.ActivatedWorkflows.Add("SubmitExpense");
        var states = ManagedAgentFixtures.WorkflowStates();

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, states)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var observed = states.Read(workspace.Root, "env-1");

        Assert.Equal("SubmitExpense", Assert.Single(connections.StateChanges).WorkflowId is not null ? "SubmitExpense" : string.Empty);
        Assert.Equal(WorkflowStateCache.Activated, observed["11111111-1111-1111-1111-111111111111"]);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", observed.Keys);
        Assert.Contains(result.Warnings, warning => warning == "1 workflow is not running. Run `msagent deployment connection update` to see and fix them.");
    }

    [Fact]
    public async Task Deploy_AWorkflowTheServiceWasNeverAskedAbout_IsNotRecordedAsADraft()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "Blocked", CanEnable = false, ConnectionReferences = ["cr_teams"] });
        var states = ManagedAgentFixtures.WorkflowStates();

        await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, states)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(connections.StateChanges);
        Assert.Empty(states.Read(workspace.Root, "env-1"));
    }

    [Fact]
    public async Task Deploy_AWarningAboutAnotherComponentSharingAWorkflowName_IsNotSuppressed()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("Workflow 'SubmitExpense' is not running: the service left it disabled.");
        workspaces.PushWarnings.Add("AI prompt 'SubmitExpense' was not saved: the service refused it.");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.ActivatedWorkflows.Add("SubmitExpense");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() =>
            Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
                new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
                CancellationToken.None));

        Assert.Contains("AI prompt 'SubmitExpense'", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Workflow 'SubmitExpense' is not running", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WhenTheConnectionsCouldNotBeListed_StillReportsTheReferenceAsUnbound()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { CatalogUnavailable = true });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("not bound", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("could not be checked", StringComparison.Ordinal));
        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Deploy_WhenAReferenceIsVerifiedBound_SaysNothingAboutIt()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams", "conn-teams"));

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("connection reference", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_DoesNotClaimConnectionsAreBoundWhenTheServiceDeclinesToActivate()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("connections are bound", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning == "1 workflow is not running. Run `msagent deployment connection update` to see and fix them.");
    }

    [Fact]
    public async Task Deploy_BindsAReferenceThatExactlyOneConnectedConnectionSatisfies()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView
        {
            LogicalName = "cr_teams",
            ConnectorId = "shared_teams",
            Candidates = [new CopilotConnectionCandidate { ConnectionId = "conn-1", DisplayName = "op@contoso.com", Status = "Connected", Owner = ManagedAgentFixtures.Principal }],
        });

        await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var binding = Assert.Single(connections.Bound);
        Assert.Equal("cr_teams", binding.LogicalName);
        Assert.Equal("conn-1", binding.ConnectionId);
    }

    [Fact]
    public async Task Deploy_BindsOnlyTheConnectionTheOperatorOwns()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView
        {
            LogicalName = "cr_teams",
            ConnectorId = "shared_teams",
            Candidates =
            [
                new CopilotConnectionCandidate { ConnectionId = "conn-1", DisplayName = "op@contoso.com", Status = "Connected", Owner = ManagedAgentFixtures.Principal },
                new CopilotConnectionCandidate { ConnectionId = "conn-2", DisplayName = "svc@contoso.com", Status = "Connected", Owner = "svc@contoso.com" },
                new CopilotConnectionCandidate { ConnectionId = "conn-3", DisplayName = "svc@contoso.com", Status = "Connected", Owner = "svc@contoso.com" },
            ],
        });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("conn-1", Assert.Single(connections.Bound).ConnectionId);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("cr_teams", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_BindsNothingWhenEveryConnectionBelongsToSomebodyElse()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView
        {
            LogicalName = "cr_teams",
            ConnectorId = "shared_teams",
            Candidates =
            [
                new CopilotConnectionCandidate { ConnectionId = "conn-2", DisplayName = "svc@contoso.com", Status = "Connected", Owner = "svc@contoso.com" },
                new CopilotConnectionCandidate { ConnectionId = "conn-3", DisplayName = "alice@contoso.com", Status = "Connected", Owner = "alice@contoso.com" },
            ],
        });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
        Assert.Contains(result.Warnings, warning => warning.Contains("not connected", StringComparison.Ordinal) || warning.Contains("connection reference", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_DoesNotRebindAReferenceThatIsAlreadyBound()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(new CopilotConnectionView
        {
            LogicalName = "cr_teams",
            ConnectorId = "shared_teams",
            BoundConnectionId = "conn-1",
            BoundConnectionExists = true,
            Candidates = [new CopilotConnectionCandidate { ConnectionId = "conn-1", DisplayName = "op@contoso.com", Status = "Connected" }],
        });

        await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, connections.BindCalls);
    }

    [Fact]
    public async Task Deploy_DropsThePushWarningForAWorkflowTheActivationPassStarted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("Workflow 'SubmitExpense' is not running: the service left it disabled.");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = true });
        connections.ActivatedWorkflows.Add("SubmitExpense");

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("left it disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_DropsThePushWarningForAWorkflowThePassAlsoReported()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("Workflow 'NotifyOnReject' is not running: the service left it disabled.");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("left it disabled", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning == "1 workflow is not running. Run `msagent deployment connection update` to see and fix them.");
    }

    [Fact]
    public async Task Deploy_DropsTheAttachWarningForAWorkflowThePassAlsoReported()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort();
        var attach = Connecting(workspaces);
        attach.AttachWarnings.Add("Workflow 'NotifyOnReject' is not running: the service left it disabled.");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        var result = await Create(store, workspaces, attach, activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("left it disabled", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning == "1 workflow is not running. Run `msagent deployment connection update` to see and fix them.");
    }

    [Fact]
    public async Task Deploy_KeepsAWorkflowWarningThatCarriesAServiceError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add(DeployWarningText.WorkflowNotActivated("NotifyOnReject", "Dataverse request failed (400)."));
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("Dataverse request failed (400)", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning == "1 workflow is not running. Run `msagent deployment connection update` to see and fix them.");
    }

    [Fact]
    public async Task Deploy_KeepsAPushWarningTheCountsDoNotRestate()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("A component was refused: ExportKeyInvalidCreate on cre98_AgentC1.cr.WX3p-EQ4");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("ExportKeyInvalidCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WithPublish_WithholdsItWhenAPushWarningSurvived()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("A component was refused: ExportKeyInvalidCreate on cre98_AgentC1.cr.WX3p-EQ4");
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() =>
            Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
                new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
                CancellationToken.None));

        Assert.Contains("ExportKeyInvalidCreate", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WhenTheActivationPassCannotRead_KeepsTheDetailedWarnings()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add(DeployWarningText.WorkflowNotActivated("NotifyOnReject", "Dataverse request failed (400)."));
        workspaces.PushWarnings.Add("A component was refused: ExportKeyInvalidCreate");
        var connections = new FakeConnectionPort { ReadThrows = new InvalidOperationException("the catalog is unreachable") };

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("Dataverse request failed (400)", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("ExportKeyInvalidCreate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WhenTheActivationPassCannotRead_KeepsThePushWarnings()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("Workflow 'NotifyOnReject' is not running: the service left it disabled.");
        var connections = new FakeConnectionPort { ReadThrows = new InvalidOperationException("the catalog is unreachable") };

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("left it disabled", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be activated after the push", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WhenOneReferenceIsUnbound_ReadsAsASentence()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams") with { CatalogUnavailable = true });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.StartsWith("1 connection reference is not bound.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_CountsUnboundReferencesAndNotRunningWorkflowsInOneWarning()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_teams", "shared_teams"));
        connections.Views.Add(ManagedAgentFixtures.Reference("cr_spo", "shared_sharepointonline"));
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "11111111-1111-1111-1111-111111111111", DisplayName = "SubmitExpense", CanEnable = false, ConnectionReferences = ["cr_teams"] });
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_spo"] });

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections, ManagedAgentFixtures.WorkflowStates())).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(
            "2 workflows are not running and 2 connection references are not bound. Run `msagent deployment connection update` to see and fix them.",
            Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task Deploy_NeverAttemptsToActivateAWorkflowThatCannotBeEnabled()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort();
        connections.Workflows.Add(new CopilotWorkflowView { WorkflowId = "22222222-2222-2222-2222-222222222222", DisplayName = "NotifyOnReject", CanEnable = false, ConnectionReferences = ["cr_teams"] });

        await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Empty(connections.StateChanges);
    }

    [Fact]
    public async Task Deploy_WhenActivationFails_ReportsItWithoutFailingTheDeploy()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var connections = new FakeConnectionPort { ReadThrows = new InvalidOperationException("the service is unavailable") };

        var result = await Create(store, workspaces, Connecting(workspaces), activation: ManagedAgentFixtures.ActivationPass(connections)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("could not be activated after the push", StringComparison.Ordinal));
    }

    private static RecordingAttachPort Connecting(FakeCopilotWorkspacePort workspaces) =>
        new()
        {
            OnAttachCommand = command =>
            {
                workspaces.WorkspaceExists = true;
                workspaces.EnvironmentIdByDirectory[Path.GetFileName(command.ProjectDirectory)] = command.Environment.EnvironmentId;
            },
        };

    private static FakeCopilotWorkspacePort Fresh(out FakeCopilotWorkspacePort workspaces, bool connected = false)
    {
        workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = connected };
        return workspaces;
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) Registered(string stage = DeploymentStages.Dev, string environmentId = "env-1", bool withProjectFile = true, bool withDeployment = true)
    {
        var workspace = new TemporaryWorkspace(withProjectFile);
        var store = ManagedAgentFixtures.Store();

        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        if (withDeployment)
        {
            store.Add(workspace.Root, ManagedAgentFixtures.Deployment(name: stage, stage: stage, environmentId: environmentId));
        }

        return (workspace, store);
    }

    [Fact]
    public async Task Deploy_WithNoCloudBaseline_Reattaches()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
                var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = Connecting(workspaces);

        var result = await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.True(result.TargetAgentCreated);
        Assert.False(attach.LastCommand!.AllowRetarget);
    }

    [Fact]
    public async Task Deploy_WhenItCreatesTheCloudAgent_Overwrites()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = Connecting(workspaces);

        var result = await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.True(result.TargetAgentCreated);
        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WhenItAdoptsAnExistingCloudAgent_KeepsConflictDetection()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        var result = await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.False(result.TargetAgentCreated);
        Assert.False(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WhenItAdoptsAnExistingCloudAgent_StillHonoursAnExplicitOverwrite()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = new RecordingAttachPort { AgentCreated = false, OnAttach = () => workspaces.WorkspaceExists = true };

        await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Overwrite = true },
            CancellationToken.None);

        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_WithABaselineInTheTargetEnvironment_Pushes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var attach = new RecordingAttachPort();
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("push", result.Action);
        Assert.Equal(0, attach.Calls);
        Assert.Equal("agent-1", result.TargetMcsAgentId);
    }

    [Fact]
    public async Task Deploy_WithABaselineInAnotherEnvironment_Retargets()
    {
        var (workspace, store) = Registered(DeploymentStages.Prod, "env-prod");
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        var attach = Connecting(workspaces);
        var environments = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-prod", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var service = new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            new CopilotWorkspaceService(workspaces, CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem())),
            ManagedAgentFixtures.Identities(),
            attach,
            new FakeCopilotCatalogPort(),
            new CopilotCatalogService(new FakeCopilotCatalogPort(), environments, CopilotTestFixtures.Locator(new FakeCopilotWorkspacePort(), new SystemCopilotFileSystem()), new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationPass(),
            new FixedClock(ManagedAgentFixtures.Now));

        var result = await service.DeployAsync(new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "prod" }, CancellationToken.None);

        Assert.Equal("retarget", result.Action);
        Assert.True(attach.LastCommand!.AllowRetarget);
    }

    [Fact]
    public async Task Deploy_RecordsTheDeployTimeOnTheSlot()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Create(store, Fresh(out var workspaces), Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(ManagedAgentFixtures.Now, store.Read(workspace.Root).AgentDeployments[0].LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_WhenTheBoundAgentWasDeleted_RebindsInsteadOfPushingAtNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        var attach = Connecting(workspaces);
        var catalog = new FakeCopilotCatalogPort { GetAgentThrows = CopilotStudioException.AgentNotFound("agent-1") };

        var result = await Create(store, workspaces, attach, catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.Equal(1, attach.Calls);
        Assert.True(attach.LastCommand!.AllowRetarget);
        Assert.Contains(result.Warnings, warning => warning.Contains("no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_WhenTheBoundAgentStillExists_PushesWithoutReattaching()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        var attach = Connecting(workspaces);

        var result = await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("push", result.Action);
        Assert.Equal(0, attach.Calls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Deploy_WhenThePushFails_NamesTheStageAndWhatExistsRemotely()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false, PushThrows = new InvalidOperationException("Improper response, not implemented") };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("deploy-incomplete", failure.ErrorKind);
        Assert.Contains("push", failure.Message, StringComparison.Ordinal);
        Assert.Contains("con_agent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("was created", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Improper response, not implemented", failure.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Re-running is safe", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WhenThePushFails_DoesNotRecordTheDeployTime()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false, PushThrows = new InvalidOperationException("boom") };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Null(store.Read(workspace.Root).AgentDeployments[0].LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_AConflictStaysAConflictRatherThanBecomingADeployFailure()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, failure.ErrorKind);
    }

    [Fact]
    public async Task Deploy_AnUnreadableWorkspaceStaysActionableRatherThanBecomingADeployFailure()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.PushThrowsSequence.Enqueue(CopilotStudioException.WorkspaceFilesUnreadable(
            "1 workspace file could not be read:\n  tools/lookup_order.tool.yml(12,1): The file still contains merge conflict markers.",
            new InvalidOperationException("unreadable")));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceFilesUnreadable, failure.ErrorKind);
        Assert.Contains("tools/lookup_order.tool.yml(12,1)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_AWorkflowToolWithAnEmptyIdStaysActionableRatherThanBecomingADeployFailure()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.PushThrowsSequence.Enqueue(CopilotStudioException.WorkflowToolIdEmpty(
            ["Empty Workflow Id Tool (cli_Agent.tool.EmptyWorkflowIdTool)"]));
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces), catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkflowToolIdEmpty, failure.ErrorKind);
        Assert.Contains("cli_Agent.tool.EmptyWorkflowIdTool", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.PublishCalls);
        Assert.Null(store.Read(workspace.Root).AgentDeployments[0].LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_AMissingConnectionReferenceStaysActionableRatherThanBecomingADeployFailure()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.PushThrowsSequence.Enqueue(CopilotStudioException.MissingConnectionReference("env-1", [], CopilotTestFixtures.MissingEntity()));
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces), catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, failure.ErrorKind);
        Assert.Contains("msagent deployment connection add", failure.Remediation!, StringComparison.Ordinal);
        Assert.Equal(0, catalog.PublishCalls);
        Assert.Null(store.Read(workspace.Root).AgentDeployments[0].LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_AReattach_NeverAppliesTheConflictGate()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("reattach", result.Action);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_ARetarget_NeverAppliesTheConflictGate()
    {
        var (workspace, store) = Registered(DeploymentStages.Dev);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { EnvironmentIdReturns = "env-elsewhere" };
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("retarget", result.Action);
        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_ADevelopmentSlot_RefusesOnConflict()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal(0, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenTheCloudChangedATrackedComponent_RefusesEvenWithNoLocalChanges()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting", kind: CopilotChangeKinds.Updated));
        var catalog = new FakeCopilotCatalogPort();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(store, workspaces, Connecting(workspaces), catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.PushConflictsRequirePull, failure.ErrorKind);
        Assert.Contains("con_greeting", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, catalog.PublishCalls);
        Assert.Null(store.Read(workspace.Root).AgentDeployments[0].LastDeployedTime);
    }

    [Fact]
    public async Task Deploy_WhenTheCloudOnlyAddedAComponent_StillDeploys()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("cat-new", kind: CopilotChangeKinds.Created));

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_WhenTheCloudChangedATrackedComponentWithOverwrite_Deploys()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting", kind: CopilotChangeKinds.Updated));

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Overwrite = true },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_ANonDevelopmentSlot_StillDeploysDespiteCloudDrift()
    {
        var (workspace, store) = Registered(DeploymentStages.Prod, "env-1");
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting", kind: CopilotChangeKinds.Updated));

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "prod" },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
    }

    [Fact]
    public async Task Deploy_ADevelopmentSlot_WithOverwrite_Pushes()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Overwrite = true },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
        Assert.True(workspaces.LastPushOverwrite);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Deploy_ADevelopmentSlot_WithOverwriteAndNoConflict_WarnsAboutNothing()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Overwrite = true },
            CancellationToken.None);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Deploy_ADevelopmentSlot_WithOverwrite_AsksThePortToOverwrite()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Overwrite = true },
            CancellationToken.None);

        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_ADevelopmentSlot_WithoutOverwrite_DoesNotAskThePortToOverwrite()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_AProductionSlot_ClobbersOnConflict()
    {
        var (workspace, store) = Registered(DeploymentStages.Prod, "env-1");
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "prod" },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
        Assert.Equal(1, result.PushedComponentCount);
        Assert.True(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Deploy_ReportsTheFilesThePushUploaded()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true, UploadedFileNamesReturns = ["SKILL.md"] };

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, result.PushedComponentCount);
        Assert.Equal(1, result.UploadedFileCount);
    }

    [Fact]
    public async Task Deploy_RecordsWhoDeployedOnTheDeployment()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var deployment = store.Read(workspace.Root).AgentDeployments.Single();

        Assert.NotNull(deployment.LastDeployedBy);
        Assert.Equal(PrincipalObjectTypes.User, deployment.LastDeployedBy!.ObjectType);
    }

    [Fact]
    public async Task Deploy_RecordsWhoDeployedOnTheAgent()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        var agent = store.List(workspace.Root).Single();

        Assert.NotNull(agent.LastDeployedBy);
        Assert.Equal(agent.CreatedBy?.ObjectId ?? agent.LastDeployedBy!.ObjectId, agent.LastDeployedBy!.ObjectId);
    }

    [Fact]
    public async Task Deploy_BeforeItRuns_LeavesTheDeployerUnset()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;

        Assert.Null(store.List(workspace.Root).Single().LastDeployedBy);
        Assert.Null(store.Read(workspace.Root).AgentDeployments.Single().LastDeployedBy);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Deploy_AProductionSlot_NeedsNoOverwriteFlag()
    {
        var (workspace, store) = Registered(DeploymentStages.Prod, "env-1");
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort();
        workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        workspaces.RemoteChanges.Add(CopilotTestFixtures.Change("con_greeting"));

        var result = await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "prod", Overwrite = false },
            CancellationToken.None);

        Assert.Equal(1, workspaces.PushCalls);
        Assert.Equal("push", result.Action);
    }

    [Fact]
    public async Task Deploy_NeverClones()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };

        await Create(store, workspaces, Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(0, workspaces.CloneCalls);
    }

    [Fact]
    public async Task Deploy_WithoutPublish_ReportsTheContentAsADraft()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort();

        var result = await Create(store, Fresh(out var workspaces), Connecting(workspaces), catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.False(result.Published);
        Assert.Equal(0, catalog.PublishCalls);
    }

    [Fact]
    public async Task Deploy_WithPublish_PublishesAfterThePush()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "11111111-1111-1111-1111-111111111111" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "11111111-1111-1111-1111-111111111111" };
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(ManagedAgentFixtures.Now);

        var result = await Create(store, Fresh(out var workspaces), Connecting(workspaces), catalog).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
            CancellationToken.None);

        Assert.True(result.Published);
        Assert.Equal(1, catalog.PublishCalls);
    }

    [Theory]
    [InlineData(false, new[] { "Pushing changes" })]
    [InlineData(true, new[] { "Pushing changes", "Publishing" })]
    public async Task Deploy_ReportsEachStepOnTheStatusLine(bool publish, string[] expected)
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        store.Update(workspace.Root, store.List(workspace.Root).Single() with { McsAgentId = "11111111-1111-1111-1111-111111111111" });
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "11111111-1111-1111-1111-111111111111" };
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(ManagedAgentFixtures.Now);
        var progress = new RecordingProgressStatus(shown: true);

        await Create(store, Fresh(out var workspaces), Connecting(workspaces), catalog, progress: progress).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = publish },
            CancellationToken.None);

        Assert.Equal(expected, progress.Reports);
    }

    [Fact]
    public async Task Deploy_AWithheldPublishIsNeverReportedAsAStep()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.PushWarnings.Add("A component was refused: ExportKeyInvalidCreate on cre98_AgentC1.cr.WX3p-EQ4");
        var progress = new RecordingProgressStatus(shown: true);

        await Assert.ThrowsAsync<ManagedAgentException>(() =>
            Create(store, workspaces, Connecting(workspaces), progress: progress).DeployAsync(
                new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", Publish = true },
                CancellationToken.None));

        Assert.Equal(["Pushing changes"], progress.Reports);
    }

    [Fact]
    public async Task Deploy_OverridesTheSchemaName()
    {
        var (workspace, store) = Registered();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
                var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var attach = Connecting(workspaces);

        await Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev", SchemaName = "foo_other" },
            CancellationToken.None);

        Assert.Equal("foo_other", attach.LastCommand!.SchemaName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Deploy_WithoutAResolvableDeployment_StopsBeforeCloudOrCacheChanges(bool hasDeployment, bool withSelector)
    {
        var (workspace, store) = Registered(withDeployment: hasDeployment);
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        workspace.AddFile(Path.Combine(".mcs", "botdefinition.json"), "{\"source\":\"cloud\"}");
        var configuration = File.ReadAllBytes(workspace.ConfigPath);
        var cachePath = Path.Combine(workspace.Root, ".mcs", "botdefinition.json");
        var cache = File.ReadAllBytes(cachePath);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var attach = Connecting(workspaces);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, workspaces, attach).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = withSelector ? "no-such-slot" : null },
            CancellationToken.None));

        Assert.Equal(withSelector ? "deployment-not-found" : hasDeployment ? "deployment-selector-required" : "deployment-records-empty", failure.ErrorKind);
        Assert.Contains(hasDeployment ? withSelector ? "no-such-slot" : "Available deployments: dev" : "no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, attach.Calls);
        Assert.Equal(configuration, File.ReadAllBytes(workspace.ConfigPath));
        Assert.Equal(cache, File.ReadAllBytes(cachePath));
    }

    [Fact]
    public async Task Deploy_ForANonMcsRecord_Fails()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.CodeAgent });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment() with { AgentType = AgentTypes.CodeAgent });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, Fresh(out var workspaces, connected: true), Connecting(workspaces)).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None));

        Assert.Equal("not-an-mcs-agent", failure.ErrorKind);
    }
}
