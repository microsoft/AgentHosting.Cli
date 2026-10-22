using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[CollectionDefinition("CurrentDirectory", DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
}

internal sealed class CurrentDirectoryScope : IDisposable
{
    private readonly string _previous = Directory.GetCurrentDirectory();

    public CurrentDirectoryScope(string directory) => Directory.SetCurrentDirectory(directory);

    public void Dispose() => Directory.SetCurrentDirectory(_previous);
}

[Collection("CurrentDirectory")]
public sealed class ManagedAgentCreateServiceTests
{
    private static ManagedAgentCreateRequest Scaffold() => new()
    {
        Name = "Contoso Agent",
        Description = "Answers support questions",
        InitialPrompt = "Help users resolve support issues.",
    };

    private static ManagedAgentCreateService Create(JsonAgentConfigStore store, FakeCopilotCatalogPort catalog, ICopilotScaffoldPort? scaffolder = null, CopilotEnvironmentResolver? resolver = null, FakeCopilotWorkspacePort? workspaces = null, IAgentDeploymentRegistry? deployments = null)
    {
        var environments = resolver ?? ManagedAgentFixtures.Resolver();
        var fileSystem = new SystemCopilotFileSystem();
        var workspacePort = workspaces ?? new FakeCopilotWorkspacePort();

        return new ManagedAgentCreateService(
            new CopilotScaffoldService(
                scaffolder ?? new EmbeddedTemplateScaffolder(),
                new CopilotSchemaName(),
                fileSystem),
            environments,
            catalog,
            store,
            deployments ?? store,
            ManagedAgentFixtures.Materialiser(workspacePort),
            new AgentConfigLocator(),
            ManagedAgentFixtures.Identities(),
            new ManagedAgentPrincipalResolver(InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant)));
    }

    [Fact]
    public async Task Scaffold_WithoutADescription_IsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { Description = null },
            CancellationToken.None));

        Assert.Contains("--description", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_WithoutAnInitialPrompt_IsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { InitialPrompt = null },
            CancellationToken.None));

        Assert.Contains("--initial-prompt", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_WithAnInitTemplateAndTheCliAgentType_IsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { InitTemplate = "default" },
            CancellationToken.None));

        Assert.Contains("--mcs-agent-type Classic", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_WithAnUnknownClassicTemplate_IsRejected()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { McsAgentType = "Classic", InitTemplate = "elaborate" },
            CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.TemplateNotFound, failure.ErrorKind);
    }

    [Fact]
    public async Task Scaffold_WithTheDefaultClassicTemplate_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var created = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { McsAgentType = "Classic", InitTemplate = "Default" },
            CancellationToken.None);

        Assert.Equal(CopilotAuthoringModes.DefaultTemplateName, created.Template);
    }

    [Fact]
    public async Task Scaffold_WithClassicAgentType_NeedsNoTemplate()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { McsAgentType = "Classic" },
            CancellationToken.None);

        Assert.Equal("default", result.Template);
    }

    [Fact]
    public async Task Clone_InheritsTheSourceDescription()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var workspaces = DescribedClone(workspace.Root);

        await Create(store, new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() }, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("From the source", store.List(workspace.Root).Single().Description);
    }

    [Fact]
    public async Task Clone_WithAnExplicitDescription_KeepsIt()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var workspaces = DescribedClone(workspace.Root);

        await Create(store, new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() }, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1", Description = "Mine" },
            CancellationToken.None);

        Assert.Equal("Mine", store.List(workspace.Root).Single().Description);
    }

    private static FakeCopilotWorkspacePort DescribedClone(string root) => new()
    {
        OnClone = () => File.WriteAllText(
            Path.Combine(root, "Contoso Agent", AgentConfigLocator.ProjectSettingsFileName),
            "displayName: \"Contoso Agent\"\nschemaName: \"con_agent\"\ndescription: \"From the source\"\n"),
    };

    [Fact]
    public async Task Scaffold_RecordsTheSignedInOperatorAsOwnerAndCreator()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var result = await Create(store, new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { Name = "Owned Agent" },
            CancellationToken.None);

        var stored = store.List(workspace.Root).Single(agent => agent.AgentId == result.AgentId);

        Assert.NotNull(stored.Owner);
        Assert.Equal(PrincipalObjectTypes.User, stored.Owner!.ObjectType);
        Assert.Equal(stored.Owner.ObjectId, stored.CreatedBy?.ObjectId);
    }

    [Fact]
    public async Task Scaffold_RecordsItsWorkspaceLocation()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        await Create(store, new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal("Contoso Agent", store.List(workspace.Root).Single().AgentLocation);
    }

    [Fact]
    public async Task Clone_RecordsItsWorkspaceLocation()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        await Create(store, new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() }).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("Fork", store.List(workspace.Root).Single().AgentLocation);
    }

    [Fact]
    public async Task Scaffold_NestsTheProjectUnderTheName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal(workspace.Root, result.ProjectDirectory);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Contoso Agent", AgentConfigLocator.ProjectSettingsFileName)));
        Assert.False(File.Exists(Path.Combine(workspace.Root, AgentConfigLocator.ProjectSettingsFileName)));
    }

    [Fact]
    public async Task Scaffold_KeepsTheConfigurationAtTheProjectRoot()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal(Path.Combine(workspace.Root, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName), result.ConfigPath);
    }

    [Fact]
    public async Task Scaffold_ReportsTheWorkspaceItCreated()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        var created = Assert.Single(result.ClonedWorkspaces);

        Assert.Equal("agent", created.Kind);
        Assert.Equal("Contoso Agent", created.DisplayName);
        Assert.Equal(Path.Combine(workspace.Root, "Contoso Agent"), created.Path);
    }

    [Fact]
    public async Task Scaffold_PreservesUnrelatedFilesAtTheProjectRoot()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        File.WriteAllText(Path.Combine(workspace.Root, "notes.txt"), "unrelated");

        await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(workspace.Root, "notes.txt")));
    }

    [Fact]
    public async Task Scaffold_IntoAFolderThatAlreadyHasContent_IsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "Contoso Agent"));
        File.WriteAllText(Path.Combine(workspace.Root, "Contoso Agent", "notes.txt"), "taken");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold(),
            CancellationToken.None));

        Assert.Contains("Contoso Agent", failure.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("workspace-not-empty", failure.ErrorKind);
        Assert.Contains("--agent-name", failure.Remediation, StringComparison.Ordinal);
        Assert.Equal("taken", File.ReadAllText(Path.Combine(workspace.Root, "Contoso Agent", "notes.txt")));
    }

    [Fact]
    public async Task Scaffold_WithAnEmptyFolderAlreadyThere_Succeeds()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "Contoso Agent"));

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal("scaffold", result.CreatedFrom);
    }

    [Fact]
    public async Task Scaffold_WithANameHoldingPathCharacters_SanitisesTheFolder()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { Name = "Sales/Support: v2" },
            CancellationToken.None);

        var created = Assert.Single(result.ClonedWorkspaces);

        Assert.Equal(Path.Combine(workspace.Root, "Sales-Support- v2"), created.Path);
        Assert.True(Directory.Exists(created.Path));
    }

    [Fact]
    public async Task Scaffold_IsFoundByTheWorkspaceResolver()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal(Path.Combine(workspace.Root, "Contoso Agent"), new AgentConfigLocator().AgentWorkspaceDirectory(workspace.Root, "Contoso Agent"));
    }

    [Fact]
    public async Task Scaffold_WritesTheProjectAndTheRecord()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var result = await Create(store, new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Equal("scaffold", result.CreatedFrom);
        Assert.Equal("cr_contosoagent", result.SchemaName);
        Assert.Null(result.McsAgentId);
        Assert.Contains("settings.mcs.yml", result.CreatedFiles);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Contoso Agent", "settings.mcs.yml")));
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Scaffold_RejectsADuplicateNameBeforeWritingAnything()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var scaffolder = new RecordingScaffoldPort();

        var existing = store.Add(workspace.Root, ManagedAgentFixtures.Agent(displayName: "Contoso Agent"));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotCatalogPort(), scaffolder).CreateAsync(
            Scaffold() with { Name = "contoso agent" },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("already-registered", failure.ErrorKind);
        Assert.Contains(existing.DisplayName, failure.Message, StringComparison.Ordinal);
        Assert.Contains(existing.AgentId, failure.Message, StringComparison.Ordinal);
        Assert.Contains(new AgentConfigLocator().ConfigFilePath(workspace.Root), failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent agent delete", failure.Remediation, StringComparison.Ordinal);
        Assert.Single(store.List(workspace.Root));
        Assert.Null(scaffolder.LastCommand);
    }

    [Fact]
    public async Task Scaffold_MakesNoCopilotStudioCall()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort();

        await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Empty(catalog.ResolvedValues);
        Assert.Equal(0, catalog.CreateCalls);
    }

    [Fact]
    public async Task Scaffold_WritesNoDeployment()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        await Create(store, new FakeCopilotCatalogPort()).CreateAsync(Scaffold(), CancellationToken.None);

        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Scaffold_HonoursAnExplicitSchemaName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { SchemaName = "foo_foo" },
            CancellationToken.None);

        Assert.Equal("foo_foo", result.SchemaName);
    }

    [Fact]
    public async Task Scaffold_RejectsAnInvalidSchemaName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { SchemaName = "not valid!!" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Scaffold_RejectsAnUnknownAgentShape()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { McsAgentType = "Bogus" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Scaffold_RejectsADuplicateName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var service = Create(store, new FakeCopilotCatalogPort());

        await service.CreateAsync(Scaffold(), CancellationToken.None);

        await Assert.ThrowsAsync<ManagedAgentException>(() => service.CreateAsync(Scaffold(), CancellationToken.None));
    }

    [Fact]
    public async Task Clone_ResolvesTheSourceSchemaNameToAnAgentId()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sourceAgentId = Guid.NewGuid().ToString();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = sourceAgentId };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneSchemaName = "src_agent", CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("clone", result.CreatedFrom);
        Assert.Equal(sourceAgentId, result.McsAgentId);
        Assert.Equal(["src_agent"], catalog.ResolvedValues);
    }

    [Fact]
    public async Task Clone_StoresTheSourceCloudAgentOnTheRecord()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var sourceAgentId = Guid.NewGuid().ToString();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = sourceAgentId };

        await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = sourceAgentId, CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal(sourceAgentId, store.List(workspace.Root).Single().McsAgentId);
    }

    [Fact]
    public async Task Clone_StoresTheCloneEnvironmentAsTheRecordsHome()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Clone_WritesNoProjectFiles()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var scaffolder = new RecordingScaffoldPort();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        await Create(ManagedAgentFixtures.Store(), catalog, scaffolder).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Null(scaffolder.LastCommand);
    }

    [Fact]
    public async Task Clone_WithoutTheCloneEnvironment_UsesTheActiveEnvironment()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneSchemaName = "src_agent" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Equal("env-1", result.SourceEnvironmentId);
    }

    [Fact]
    public async Task Clone_WithASchemaNameIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneSchemaName = "src_agent", CloneEnvironmentId = "env-1", SchemaName = "foo_foo" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Clone_WhenTheSourceIsAbsent_WritesNothing()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeCopilotCatalogPort { ResolveReturns = null }).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneSchemaName = "src_agent", CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Equal("clone-source-not-found", failure.ErrorKind);
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Clone_MaterialisesTheProjectLocally()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var workspaces = new FakeCopilotWorkspacePort();

        var result = await Create(ManagedAgentFixtures.Store(), catalog, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal(1, workspaces.CloneCalls);
        Assert.Equal("con_agent", result.SchemaName);
        Assert.Equal(3, result.ClonedComponentCount);
    }

    [Fact]
    public async Task Clone_TwiceIntoOneProject_RegistersBoth()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Rev A", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);
        await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Rev B", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal(["Rev A", "Rev B"], store.List(workspace.Root).Select(agent => agent.DisplayName));
        Assert.Equal(2, store.Read(workspace.Root).AgentDeployments.Count);
    }

    [Fact]
    public async Task Clone_WhenTheDeploymentCannotBeWritten_RemovesTheAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, deployments: new UnwritableDeploymentRegistry(store)).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Rev A", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Contains("could not be registered", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Clone_RecordsItsHomeDeployment()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        var deployment = Assert.Single(store.Read(workspace.Root).AgentDeployments);

        Assert.Equal(DeploymentStages.Dev, deployment.DeploymentType);
        Assert.Equal(deployment.DeploymentId, result.DeploymentId);
        Assert.Equal("env-1", deployment.EnvironmentId);
    }

    [Fact]
    public async Task Clone_SendsNothingToCopilotStudio()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var workspaces = new FakeCopilotWorkspacePort();

        await Create(ManagedAgentFixtures.Store(), catalog, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal(0, workspaces.PushCalls);
        Assert.Equal(0, catalog.CreateCalls);
        Assert.Null(workspaces.LastPushOverwrite);
    }

    [Fact]
    public async Task Clone_WhenTheContentCannotBeFetched_LeavesNoRecords()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var workspaces = new FakeCopilotWorkspacePort { CloneThrows = new InvalidOperationException("the service is unavailable") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Equal("clone-not-materialised", failure.ErrorKind);
        Assert.Empty(store.List(workspace.Root));
        Assert.Empty(store.Read(workspace.Root).AgentDeployments);
    }

    [Fact]
    public async Task Clone_WhenTheContentCannotBeFetched_SaysNothingWasRegistered()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var workspaces = new FakeCopilotWorkspacePort { CloneThrows = new InvalidOperationException("boom") };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was registered", failure.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_WhenTheContentCannotBeFetched_KeepsTheOriginalFailure()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var cause = new InvalidOperationException("boom");

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, catalog, workspaces: new FakeCopilotWorkspacePort { CloneThrows = cause }).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Same(cause, failure.InnerException);
    }

    [Fact]
    public async Task Clone_ReportsTheSchemaNameTheMaterialisedProjectDeclares()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var workspaces = new FakeCopilotWorkspacePort
        {
            OnClone = () => File.WriteAllText(
                Path.Combine(workspace.Root, "Fork", AgentConfigLocator.ProjectSettingsFileName),
                "displayName: \"Fork\"\nschemaName: \"con_frommaterialised\"\ntemplate: cliagent-1.0.0\n"),
        };

        var result = await Create(ManagedAgentFixtures.Store(), catalog, workspaces: workspaces).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("con_frommaterialised", result.SchemaName);
    }

    [Fact]
    public async Task Clone_WarnsAboutTheScaffoldOptionsItIgnores()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest
            {
                Name = "Fork",
                CloneAgentId = Guid.NewGuid().ToString(),
                CloneEnvironmentId = "env-1",
                InitialPrompt = "Be helpful.",
                McsAgentType = "Classic",
            },
            CancellationToken.None);

        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("--initial-prompt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scaffold_RegistersInTheRequestedEnvironment()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort(), resolver: resolver).CreateAsync(
            Scaffold() with { Name = "Contoso Helpdesk", EnvironmentId = "env-2" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
    }

    [Fact]
    public async Task Scaffold_WithoutAnEnvironment_RegistersInTheActiveOne()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { Name = "Contoso Helpdesk" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Clone_WithAnEnvironmentMatchingTheSource_IsAccepted()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1", EnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Clone_AcrossEnvironments_RegistersInTheTargetAndReadsTheSource()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        var result = await Create(store, catalog, resolver: resolver).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1", EnvironmentId = "env-2" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
        Assert.Equal("env-1", result.SourceEnvironmentId);
        Assert.Equal("env-2", Assert.Single(store.Read(workspace.Root).AgentDeployments).EnvironmentId);
    }

    [Fact]
    public async Task Clone_AcrossEnvironments_DoesNotAdoptTheSourceCloudAgent()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };
        var resolver = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
            EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization));

        await Create(store, catalog, resolver: resolver).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1", EnvironmentId = "env-2" },
            CancellationToken.None);

        Assert.Null(store.List(workspace.Root).Single().McsAgentId);
    }

    [Fact]
    public async Task Clone_WithinOneEnvironment_KeepsTheSourceAsItsDeploymentTarget()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var sourceAgentId = Guid.NewGuid().ToString();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = sourceAgentId };

        await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = sourceAgentId, CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("env-1", Assert.Single(store.Read(workspace.Root).AgentDeployments).EnvironmentId);
        Assert.Equal(sourceAgentId, store.List(workspace.Root).Single().McsAgentId);
    }

    [Fact]
    public async Task Clone_WithBothSelectors_IsAUsageError()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var failure = await Assert.ThrowsAsync<UsageException>(() => Create(store, new FakeCopilotCatalogPort()).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneSchemaName = "src_agent", CloneEnvironmentId = "env-1" },
            CancellationToken.None));

        Assert.Contains("exactly one", failure.Message, StringComparison.Ordinal);
        Assert.Empty(store.List(workspace.Root));
    }

    [Fact]
    public async Task Clone_WithoutAName_InheritsTheSourceAgentsName()
    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(ManagedAgentFixtures.Store(), catalog).CreateAsync(
            new ManagedAgentCreateRequest { CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("Contoso Agent", result.DisplayName);
    }

    [Fact]
    public async Task Clone_BesideAnUnregisteredProject_Succeeds()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = Guid.NewGuid().ToString() };

        var result = await Create(store, catalog).CreateAsync(
            new ManagedAgentCreateRequest { Name = "Fork", CloneAgentId = Guid.NewGuid().ToString(), CloneEnvironmentId = "env-1" },
            CancellationToken.None);

        Assert.Equal("Fork", result.DisplayName);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Scaffold_BesideAnUnregisteredProject_Succeeds()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();

        var result = await Create(store, new FakeCopilotCatalogPort()).CreateAsync(
            Scaffold() with { Name = "Contoso Helpdesk" },
            CancellationToken.None);

        Assert.Equal("Contoso Helpdesk", result.DisplayName);
        Assert.Single(store.List(workspace.Root));
    }

    [Fact]
    public async Task Create_WithoutANameIsAUsageError()    {
        using var workspace = new TemporaryWorkspace(withProjectFile: false);
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<UsageException>(() => Create(ManagedAgentFixtures.Store(), new FakeCopilotCatalogPort()).CreateAsync(
            new ManagedAgentCreateRequest(),
            CancellationToken.None));
    }
}

internal sealed class UnwritableDeploymentRegistry : IAgentDeploymentRegistry
{
    private readonly IAgentDeploymentRegistry _inner;

    public UnwritableDeploymentRegistry(IAgentDeploymentRegistry inner) => _inner = inner;

    public AgentDeploymentRecord Add(string projectDirectory, AgentDeploymentRecord record) =>
        throw new IOException("the configuration file is locked by another process");

    public IReadOnlyList<AgentDeploymentRecord> List(string projectDirectory) => _inner.List(projectDirectory);

    public AgentDeploymentRecord Update(string projectDirectory, AgentDeploymentRecord record) => _inner.Update(projectDirectory, record);

    public void Remove(string projectDirectory, string deploymentId, string expectedETag) => _inner.Remove(projectDirectory, deploymentId, expectedETag);
}
