using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentComponentCollectionTests
{
    private const string AgentFolderName = "Contoso Agent";

    private const string CollectionFolderName = "Shared Tools";

    private static ManagedAgentDeployService CreateDeploy(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces)
    {
        var environments = ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "agent-1" };

        return new ManagedAgentDeployService(
            ManagedAgentFixtures.AgentResolver(store),
            store,
            store,
            environments,
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            ManagedAgentFixtures.Identities(),
            new RecordingAttachPort { OnAttach = () => workspaces.WorkspaceExists = true },
            catalog,
            new CopilotCatalogService(catalog, environments, locator, new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(),
            ManagedAgentFixtures.ActivationPass(),
            new FixedClock(ManagedAgentFixtures.Now));
    }

    private static ManagedAgentPullService CreatePull(JsonAgentConfigStore store, FakeCopilotWorkspacePort workspaces)
        => CreatePull(store, (ICopilotWorkspacePort)workspaces, ManagedAgentFixtures.Resolver());

    private static ManagedAgentPullService CreatePull(JsonAgentConfigStore store, ICopilotWorkspacePort workspaces,
        CopilotEnvironmentResolver environments)
    {
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());

        return new ManagedAgentPullService(
            ManagedAgentFixtures.AgentResolver(store),
            locator,
            new CopilotWorkspaceService(workspaces, locator),
            environments);
    }

    private static (TemporaryWorkspace Workspace, JsonAgentConfigStore Store) NestedProject()
    {
        var workspace = new TemporaryWorkspace(withProjectFile: false);
        workspace.AddFile(Path.Combine(AgentFolderName, AgentConfigLocator.ProjectSettingsFileName), "displayName: \"Contoso Agent\"\nschemaName: \"con_agent\"\n");
        workspace.AddFile(Path.Combine(AgentFolderName, AgentConfigLocator.ReferencesFileName), $"componentCollections:\n  - schemaName:\n    directory: ../{CollectionFolderName}/\n");
        workspace.AddFile(Path.Combine(CollectionFolderName, AgentConfigLocator.CollectionProjectFileName), "schemaName: con_sharedtools\n");

        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentLocation = AgentFolderName });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());

        return (workspace, store);
    }

    [Fact]
    public async Task Deploy_PushesTheCollectionBeforeTheAgent()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal(
            [Path.Combine(workspace.Root, CollectionFolderName), Path.Combine(workspace.Root, AgentFolderName)],
            workspaces.PushedDirectories);
    }

    [Fact]
    public async Task Deploy_WithACollectionInAnotherEnvironment_WarnsAndSkipsIt()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.EnvironmentIdByDirectory[CollectionFolderName] = "env-other";

        var result = await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([Path.Combine(workspace.Root, AgentFolderName)], workspaces.PushedDirectories);
        Assert.Contains(result.Warnings, warning => warning.Contains(CollectionFolderName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_InAFlatProject_PushesOnlyTheProject()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent());
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment());
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal([workspace.Root], workspaces.PushedDirectories);
    }

    [Fact]
    public async Task Pull_TakesTheCollectionAndTheAgent()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        await CreatePull(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal(
            [Path.Combine(workspace.Root, CollectionFolderName), Path.Combine(workspace.Root, AgentFolderName)],
            workspaces.PulledDirectories);
    }

    [Fact]
    public async Task Pull_WithACollectionInAnotherEnvironment_LeavesItAlone()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };
        workspaces.EnvironmentIdByDirectory[CollectionFolderName] = "env-other";

        await CreatePull(store, workspaces).PullAsync(new ManagedAgentPullRequest(), CancellationToken.None);

        Assert.Equal([Path.Combine(workspace.Root, AgentFolderName)], workspaces.PulledDirectories);
    }

    [Fact]
    public async Task Pull_CapturesOneProfileForCollectionAndAgentAcrossSelectionChanges()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        var profiles = InMemoryAuthProfileStore.SignedIn("a", EnvironmentFixtures.ContosoTenant);
        var profileA = (await profiles.GetActiveProfileAsync(default))!;
        var inventory = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1",
            instanceUrl: CopilotTestFixtures.DataverseOrganization));
        var environments = new CopilotEnvironmentResolver(new EnvironmentSelectionService(inventory,
            new InMemoryEnvironmentSelectionStore(), profiles, PowerPlatformCloudMap.ForCloud(CloudInstance.Public),
            new FixedClock(ManagedAgentFixtures.Now)), PowerPlatformCloudMap.ForCloud(CloudInstance.Public));
        var credentials = new ProfileSyncResourceTests.Credentials();
        var legacy = new ActiveProfileTokens(profiles);
        using var accessor = new AgentHostDataverseHttpClientAccessor(new AgentHostSyncAuthProvider(legacy, credentials),
            () => new ScriptedHttpMessageHandler());
        var sdk = new SyncDataverseClient(accessor, "tests");
        var workspaces = new RecordingPullWorkspacePort
        {
            OnRead = async (info, read) =>
            {
                if (Path.GetFileName(info.WorkspacePath) == CollectionFolderName && read == 1)
                    await profiles.UpsertAsync(profileA with { ProfileId = "b", AccountKey = "home-b", Username = "b" }, default);
                return info;
            },
            OnPull = async (info, ct) =>
            {
                using var gate = await DataverseTargeting.EnterAsync(sdk, ct);
                using var target = DataverseTargeting.Connect((ISyncDataverseClient)sdk, accessor, info);
                using var client = accessor.CreateClient();
                using var response = await client.GetAsync(new Uri(info.DataverseOrganization, "api/data/v9.2/bots"), ct);
                await profiles.UpsertAsync(profileA with { ProfileId = "b", AccountKey = "home-b", Username = "b" }, ct);
                var name = Path.GetFileName(info.WorkspacePath);
                return new CopilotPullOutcome
                {
                    Changes = [CopilotTestFixtures.Change(name)],
                    Warnings = [name + " warning"],
                };
            },
        };

        var result = await CreatePull(store, workspaces, environments)
            .PullAsync(new ManagedAgentPullRequest { Project = workspace.Root }, default);

        Assert.Equal([profileA.ProfileId, profileA.ProfileId],
            credentials.Requests.Select(r => r.Profile.ProfileId).Concat(legacy.Profiles.Select(p => p.ProfileId)));
        Assert.Empty(legacy.Profiles);
        var context = workspaces.PulledWorkspaces[0].Context;
        Assert.NotNull(context);
        Assert.Equal(profileA.Revision, context.Profile.Revision);
        Assert.All(workspaces.PulledWorkspaces, info =>
        {
            Assert.Same(context, info.Context);
            Assert.Equal(EnvironmentFixtures.ContosoTenant, info.TenantId);
        });
        Assert.All(credentials.Requests, acquisition =>
        {
            Assert.Same(context.Profile, acquisition.Profile);
            Assert.Equal(EnvironmentFixtures.ContosoTenant, acquisition.Tenant);
            Assert.Equal([CopilotTestFixtures.DataverseOrganization.TrimEnd('/') + "//.default"], acquisition.Scopes);
        });
        Assert.Equal([CollectionFolderName, AgentFolderName], result.Changes.Select(change => change.SchemaName));
        Assert.Equal([CollectionFolderName + " warning", AgentFolderName + " warning"], result.Warnings);
        Assert.Equal("agent-pulled", result.Status);
        Assert.Equal(2, result.ChangeCount);
        Assert.Equal(1, inventory.ListCalls);
    }

    [Theory]
    [InlineData(CollectionFolderName, "environment")]
    [InlineData(CollectionFolderName, "tenant")]
    [InlineData(CollectionFolderName, "origin")]
    [InlineData(AgentFolderName, "environment")]
    [InlineData(AgentFolderName, "tenant")]
    [InlineData(AgentFolderName, "origin")]
    public async Task Pull_RejectsWorkspaceRetargetedOnRereadBeforePullingIt(string folder, string mismatch)
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        var workspaces = new RecordingPullWorkspacePort
        {
            OnRead = (info, read) => Task.FromResult(Path.GetFileName(info.WorkspacePath) != folder || read != 2
                ? info
                : mismatch switch
                {
                    "environment" => info with { EnvironmentId = "env-other" },
                    "tenant" => info with { TenantId = EnvironmentFixtures.FabrikamTenant },
                    _ => info with { DataverseOrganization = new Uri("https://other.crm.dynamics.com/") },
                }),
        };

        var failure = await Assert.ThrowsAnyAsync<AgentHostingException>(() =>
            CreatePull(store, workspaces, ManagedAgentFixtures.Resolver())
                .PullAsync(new ManagedAgentPullRequest { Project = workspace.Root }, default));

        if (mismatch == "origin")
            Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, Assert.IsType<CopilotStudioException>(failure).ErrorKind);
        else
            Assert.IsType<CloudRetargetedException>(failure);
        Assert.DoesNotContain(workspaces.PulledWorkspaces, info => Path.GetFileName(info.WorkspacePath) == folder);
        Assert.Equal(folder == CollectionFolderName ? 0 : 1, workspaces.PulledWorkspaces.Count);
    }

    [Fact]
    public async Task Pull_PropagatesCollectionCancellationWithoutStartingAgentPull()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspaces = new RecordingPullWorkspacePort
        {
            OnPull = async (_, ct) =>
            {
                Assert.Equal(cancellation.Token, ct);
                entered.SetResult();
                await release.Task.WaitAsync(ct);
                return new CopilotPullOutcome { Changes = [] };
            },
        };
        var pull = CreatePull(store, workspaces, ManagedAgentFixtures.Resolver())
            .PullAsync(new ManagedAgentPullRequest { Project = workspace.Root }, cancellation.Token);

        await entered.Task;
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pull);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(CollectionFolderName, Path.GetFileName(Assert.Single(workspaces.PulledWorkspaces).WorkspacePath));
    }

    [Fact]
    public async Task Deploy_ReadsTheSchemaNameFromTheNestedAgentWorkspace()
    {
        var (workspace, store) = NestedProject();
        using var _ = workspace;
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = true };

        var result = await CreateDeploy(store, workspaces).DeployAsync(
            new ManagedAgentDeployRequest { AgentId = "agent-1", DeploymentName = "dev" },
            CancellationToken.None);

        Assert.Equal("con_agent", result.SchemaName);
    }

    private sealed class ActiveProfileTokens(InMemoryAuthProfileStore profiles) : ITokenProvider
    {
        public List<AuthProfile> Profiles { get; } = [];

        public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId,
            CancellationToken cancellationToken)
        {
            var profile = (await profiles.GetActiveProfileAsync(cancellationToken))!;
            Profiles.Add(profile);
            return new TokenAcquisitionResult
            {
                AccessToken = "legacy-" + profile.ProfileId,
                TenantId = targetTenantId ?? profile.TenantId,
                ExpiresOn = DateTimeOffset.MaxValue,
            };
        }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes,
            string? targetTenantId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

internal sealed class RecordingPullWorkspacePort : ICopilotWorkspacePort
{
    private readonly FakeCopilotWorkspacePort _inner = new();
    private readonly Dictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);

    public Func<CopilotWorkspaceInfo, int, Task<CopilotWorkspaceInfo>>? OnRead { get; init; }
    public Func<CopilotWorkspaceInfo, CancellationToken, Task<CopilotPullOutcome>>? OnPull { get; init; }
    public List<CopilotWorkspaceInfo> PulledWorkspaces { get; } = [];

    public bool IsWorkspace(string directory) => _inner.IsWorkspace(directory);
    public string ProjectFolderName(string displayName) => _inner.ProjectFolderName(displayName);

    public async Task<CopilotWorkspaceInfo> ReadWorkspaceInfoAsync(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = await _inner.ReadWorkspaceInfoAsync(directory, cancellationToken);
        _reads.TryGetValue(directory, out var count);
        _reads[directory] = ++count;
        return OnRead is null ? info : await OnRead(info, count);
    }

    public Task<CopilotCloneOutcome> CloneAsync(CopilotCloneCommand command, CancellationToken cancellationToken) =>
        _inner.CloneAsync(command, cancellationToken);

    public Task<CopilotChangeSnapshot> GetChangesAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken) =>
        _inner.GetChangesAsync(workspace, cancellationToken);

    public Task<CopilotPullOutcome> PullAsync(CopilotWorkspaceInfo workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PulledWorkspaces.Add(workspace);
        return OnPull is null ? _inner.PullAsync(workspace, cancellationToken) : OnPull(workspace, cancellationToken);
    }

    public Task<CopilotPushOutcome> PushAsync(CopilotWorkspaceInfo workspace, bool overwrite, CancellationToken cancellationToken) =>
        _inner.PushAsync(workspace, overwrite, cancellationToken);
}
