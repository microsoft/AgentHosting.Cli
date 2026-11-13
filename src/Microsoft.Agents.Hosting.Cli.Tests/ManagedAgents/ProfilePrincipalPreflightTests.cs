using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ProfilePrincipalPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "resource-preflight-" + Guid.NewGuid().ToString("N"));

    public ProfilePrincipalPreflightTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<(CopilotEnvironmentResolver Environments, ManagedAgentPrincipalResolver Principals, RecordingTokenProvider Tokens)> ContextAsync(
        AuthPrincipalType mode, string? oid = "11111111-2222-3333-4444-555555555555")
    {
        var profile = new AuthProfile
        {
            ProfileId = "captured-a", Revision = 17, AccountKey = "other-home-oid.home-tenant",
            Username = "operator@contoso.com", TenantId = EnvironmentFixtures.ContosoTenant,
            PrincipalType = mode, ClientId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
        };
        var profiles = new InMemoryAuthProfileStore();
        await profiles.UpsertAsync(profile, default);
        var selection = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)),
            new InMemoryEnvironmentSelectionStore(), profiles);
        await selection.SelectAsync("env-1", default);
        var environments = new CopilotEnvironmentResolver(selection, PowerPlatformCloudMap.ForCloud(CloudInstance.Public));
        var tokens = new RecordingTokenProvider
        {
            AccessTokenReturns = FakeJwt.Create(tenantId: profile.TenantId, scopes: PowerPlatformApiPermissions.AppsRead, objectId: oid),
        };
        return (environments, new ManagedAgentPrincipalResolver(profiles, tokens), tokens);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WorkloadCreateAndCloneFailBeforeScaffoldDownloadOrRegistration(bool workflow, bool clone)
    {
        var (environments, principals, tokens) = await ContextAsync(AuthPrincipalType.ServicePrincipal);
        var config = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort();
        var workspaces = new FakeCopilotWorkspacePort();
        var scaffold = new RecordingScaffoldPort();
        var workflows = new FakeWorkflowStorePort();
        var workflowScaffold = new FakeWorkflowScaffoldPort();
        var request = new ManagedAgentCreateRequest
        {
            Name = "Example", Description = "Example agent", InitialPrompt = "Help the user.",
            AgentType = workflow ? AgentTypes.McsWorkflow : AgentTypes.McsAgent,
            Project = _root, EnvironmentId = "env-1",
            CloneEnvironmentId = clone ? "env-1" : null,
            CloneAgentId = clone && !workflow ? "11111111-2222-3333-4444-555555555555" : null,
            CloneWorkflowId = clone && workflow ? "11111111-2222-3333-4444-555555555555" : null,
        };
        var registrar = new HomeDeploymentRegistrar(config, config);
        if (workflow)
        {
            var service = new WorkflowCreateService(environments, config, registrar, workflows, workflowScaffold,
                new AgentConfigLocator(), principals, new FakeWorkflowWorkspacePort());
            await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => service.CreateAsync(request, default));
        }
        else
        {
            var service = new ManagedAgentCreateService(
                new CopilotScaffoldService(scaffold, new CopilotSchemaName(), new SystemCopilotFileSystem()),
                environments, catalog, config, registrar, ManagedAgentFixtures.Materialiser(workspaces),
                new AgentConfigLocator(), ManagedAgentFixtures.Identities(), principals, workspaces);
            await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => service.CreateAsync(request, default));
        }
        Assert.Null(scaffold.LastCommand);
        Assert.Empty(workflowScaffold.Calls);
        Assert.Empty(workflows.Calls);
        Assert.Equal(0, workspaces.CloneCalls);
        Assert.Equal(0, catalog.GetAgentCalls);
        Assert.Empty(config.List(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Empty(tokens.Resources);
    }

    [Theory]
    [InlineData(AuthPrincipalType.ServicePrincipal, "11111111-2222-3333-4444-555555555555")]
    [InlineData(AuthPrincipalType.User, null)]
    public async Task AgentDeployFailsBeforeAttachPushCatalogOrRegistration(AuthPrincipalType mode, string? oid)
    {
        var (environments, principals, _) = await ContextAsync(mode, oid);
        var config = ManagedAgentFixtures.Store();
        config.Add(_root, ManagedAgentFixtures.Agent());
        ((IAgentDeploymentRegistry)config).Add(_root, ManagedAgentFixtures.Deployment());
        var before = File.ReadAllText(new AgentConfigLocator().ConfigFilePath(_root));
        var workspaces = new FakeCopilotWorkspacePort();
        var attach = new RecordingAttachPort();
        var catalog = new FakeCopilotCatalogPort();
        var locator = CopilotTestFixtures.Locator(workspaces, new FakeCopilotFileSystem());
        var service = new ManagedAgentDeployService(ManagedAgentFixtures.AgentResolver(config), config, config,
            environments, locator, new CopilotWorkspaceService(workspaces, locator), ManagedAgentFixtures.Identities(),
            attach, catalog, new CopilotCatalogService(catalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now)),
            principals, ManagedAgentFixtures.ActivationPass(), new FixedClock(ManagedAgentFixtures.Now));

        var failure = await Assert.ThrowsAnyAsync<AgentHostingException>(() =>
            service.DeployAsync(new ManagedAgentDeployRequest { Project = _root, AgentId = "agent-1", DeploymentName = "dev" }, default));

        Assert.IsType(mode == AuthPrincipalType.User ? typeof(TokenClaimException) : typeof(UserPrincipalRequiredException), failure);
        Assert.Equal(0, attach.Calls);
        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, catalog.GetAgentCalls);
        Assert.Equal(before, File.ReadAllText(new AgentConfigLocator().ConfigFilePath(_root)));
    }

    [Fact]
    public async Task WorkloadWorkflowDeployFailsBeforeUploadOrStateInvalidation()
    {
        var (environments, principals, _) = await ContextAsync(AuthPrincipalType.ServicePrincipal);
        var config = ManagedAgentFixtures.Store();
        var id = WorkflowFixtures.Write(_root, "Example");
        config.Add(_root, WorkflowFixtures.Record(workflowId: id.ToString()));
        ((IAgentDeploymentRegistry)config).Add(_root, WorkflowFixtures.Deployment());
        var store = new FakeWorkflowStorePort();
        var states = ManagedAgentFixtures.WorkflowStates(new FakeCopilotFileSystem());
        var service = new WorkflowDeployService(new WorkflowProjectResolver(ManagedAgentFixtures.AgentResolver(config),
            new AgentConfigLocator(), environments, new FakeWorkflowWorkspacePort()), config, config, store,
            ManagedAgentFixtures.ActivationPass(), states, principals, new FixedClock(ManagedAgentFixtures.Now));

        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => service.DeployAsync(
            new ManagedAgentDeployRequest { Project = _root, AgentId = "wf-1", DeploymentName = "devwf" }, default));

        Assert.Empty(store.Calls);
        Assert.Empty(states.Read(_root, "env-1"));
    }

    [Fact]
    public async Task WorkloadWorkflowInitFailsBeforeDeclarationOrRegistration()
    {
        var (environments, principals, _) = await ContextAsync(AuthPrincipalType.ServicePrincipal);
        var config = ManagedAgentFixtures.Store();
        WorkflowFixtures.Write(_root, "Example");
        var store = new FakeWorkflowStorePort();
        var service = new WorkflowInitService(config, new AgentConfigLocator(), environments,
            store, principals, new FakeWorkflowWorkspacePort());

        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => service.InitAsync(
            new ManagedAgentInitRequest { McsWorkflowSource = _root, Name = "Example" }, default));

        Assert.Empty(store.DeclarationWrites);
        Assert.Empty(config.List(_root));
    }

    [Fact]
    public async Task WorkloadDeploymentCreateFailsBeforeRegistration()
    {
        var (environments, principals, tokens) = await ContextAsync(AuthPrincipalType.ServicePrincipal);
        var config = ManagedAgentFixtures.Store();
        config.Add(_root, ManagedAgentFixtures.Agent());
        var service = new AgentDeploymentCreateService(ManagedAgentFixtures.AgentResolver(config), config,
            environments, ManagedAgentFixtures.Identities(), principals);

        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => service.CreateAsync(new AgentDeploymentCreateRequest
        {
            Project = _root, AgentId = "agent-1", DeploymentName = "devtest", DeploymentType = DeploymentStages.Dev,
        }, default));

        Assert.Empty(((IAgentDeploymentRegistry)config).List(_root));
        Assert.Empty(tokens.Resources);
    }

    [Fact]
    public async Task WorkloadConsentFailsBeforeCatalogOrBrowserSideEffects()
    {
        var (environments, principals, _) = await ContextAsync(AuthPrincipalType.ServicePrincipal);
        var target = await environments.ResolveAsync("env-1", default);
        var connectors = new FakeConnectorCatalogPort();
        var browser = new RecordingBrowserLauncher();
        var flow = new BrowserConnectionConsentFlow(browser, new RecordingMessagePresenter(), connectors,
            principals, CoreServicesClusterCategory.Prod);

        await Assert.ThrowsAsync<UserPrincipalRequiredException>(() => flow.CreateConnectionAsync(target, "shared_office365", true, default));

        Assert.Empty(browser.Launched);
        Assert.Empty(connectors.ConnectionLookups);
    }

    [Fact]
    public async Task AgentPushCarriesTheCapturedContextToTheWorkspacePort()
    {
        var (environments, principals, _) = await ContextAsync(AuthPrincipalType.User);
        var captured = await environments.ResolveAsync("env-1", default);
        File.WriteAllText(Path.Combine(_root, AgentConfigLocator.ProjectSettingsFileName), "schemaName: con_agent\ndisplayName: Example\n");
        var config = ManagedAgentFixtures.Store();
        config.Add(_root, ManagedAgentFixtures.Agent());
        ((IAgentDeploymentRegistry)config).Add(_root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        var catalog = new FakeCopilotCatalogPort();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var service = new ManagedAgentDeployService(ManagedAgentFixtures.AgentResolver(config), config, config,
            environments, locator, new CopilotWorkspaceService(workspaces, locator), ManagedAgentFixtures.Identities(),
            new RecordingAttachPort(), catalog,
            new CopilotCatalogService(catalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now)),
            principals, ManagedAgentFixtures.ActivationPass(), new FixedClock(ManagedAgentFixtures.Now));

        await service.DeployAsync(new ManagedAgentDeployRequest { Project = _root, AgentId = "agent-1", DeploymentName = "dev" }, default);

        var pushed = Assert.Single(workspaces.PushedWorkspaces);
        Assert.NotNull(pushed.Context);
        Assert.Equal("captured-a", pushed.Context.Profile.ProfileId);
        Assert.Equal(captured.Context, pushed.Context);
    }
}
