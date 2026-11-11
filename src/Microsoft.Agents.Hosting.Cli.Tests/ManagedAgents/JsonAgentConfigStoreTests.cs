using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

internal sealed class StoreClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class TemporaryProject : IDisposable
{
    public TemporaryProject(bool withProjectFile = true)
    {
        Directory.CreateDirectory(Root);

        if (withProjectFile)
        {
            WriteProjectFile();
        }
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"ah-store-{Guid.NewGuid():N}");

    public string ConfigPath => Path.Combine(Root, ".config", "agent.config.json");

    public void WriteProjectFile() => File.WriteAllText(Path.Combine(Root, "settings.mcs.yml"), "displayName: Test\nschemaName: foo_foo\n");

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal static class StoreFixtures
{
    public static JsonAgentConfigStore Create(StoreClock? clock = null) => new(new AgentConfigLocator(), clock ?? new StoreClock());

    public static ManagedAgentRecord Agent(string agentId = "agent-1", string displayName = "Contoso Helpdesk", string environmentId = "env-home") => new()
    {
        AgentId = agentId,
        DisplayName = displayName,
        AgentType = AgentTypes.McsAgent,
        AgentLocation = AgentConfigLocator.CurrentDirectoryLocation,
        TenantId = "tenant-1",
        EnvironmentId = environmentId,
        Origin = "AgentHostCLI",
        EntityTag = "1",
        AgentState = EntityStates.Saved,
    };

    public static AgentDeploymentRecord Deployment(string deploymentId = "slot-1", string agentId = "agent-1", string name = "dev", string stage = DeploymentStages.Dev, string environmentId = "env-home") => new()
    {
        DeploymentId = deploymentId,
        AgentId = agentId,
        AgentType = AgentTypes.McsAgent,
        TenantId = "tenant-1",
        HomeEnvironmentId = "env-home",
        DeploymentName = name,
        DeploymentType = stage,
        EnvironmentId = environmentId,
        EntityTag = "1",
    };
}

public sealed class JsonAgentConfigStoreTests
{
    [Fact]
    public void ReadReturnsEmptyDocumentWhenNoConfigExists()
    {
        using var project = new TemporaryProject();

        var document = StoreFixtures.Create().Read(project.Root);

        Assert.Empty(document.ManagedAgents);
        Assert.Empty(document.AgentDeployments);
    }

    [Fact]
    public void RequireExistingFailsWhenNoConfigExists()
    {
        using var project = new TemporaryProject();

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().RequireExisting(project.Root));

        Assert.Equal("config-not-found", failure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
    }

    [Fact]
    public void AddWritesToDotConfigDirectory()
    {
        using var project = new TemporaryProject();

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.True(File.Exists(project.ConfigPath));
        Assert.False(File.Exists(Path.Combine(project.Root, "agent.config.json")));
    }

    [Fact]
    public void AddStampsTimestampsAndInitialETag()
    {
        using var project = new TemporaryProject();
        var clock = new StoreClock();

        var stored = StoreFixtures.Create(clock).Add(project.Root, StoreFixtures.Agent());

        Assert.Equal("1", stored.EntityTag);
        Assert.Equal(clock.UtcNow, stored.CreatedTime);
        Assert.Equal(clock.UtcNow, stored.ChangedTime);
    }

    [Fact]
    public void UpdateBumpsETagAndChangeTimestamp()
    {
        using var project = new TemporaryProject();
        var clock = new StoreClock();
        var store = StoreFixtures.Create(clock);
        var stored = store.Add(project.Root, StoreFixtures.Agent());

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var updated = store.Update(project.Root, stored with { DisplayName = "Renamed" });

        Assert.Equal("2", updated.EntityTag);
        Assert.Equal(clock.UtcNow, updated.ChangedTime);
        Assert.Equal(stored.CreatedTime, updated.CreatedTime);
    }

    [Fact]
    public void UpdateRejectsStaleETag()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        var stored = store.Add(project.Root, StoreFixtures.Agent());
        store.Update(project.Root, stored with { DisplayName = "First" });

        var failure = Assert.Throws<ManagedAgentException>(() => store.Update(project.Root, stored with { DisplayName = "Second" }));

        Assert.Equal("etag-mismatch", failure.ErrorKind);
    }

    [Fact]
    public void RemoveWithDeploymentsRemovesEveryReferencingRow()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        var agent = store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-dev"));
        store.Add(project.Root, StoreFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        var removed = store.RemoveWithDeployments(project.Root, agent.AgentId, agent.EntityTag);

        Assert.Equal(2, removed);
        Assert.Empty(store.Read(project.Root).ManagedAgents);
        Assert.Empty(store.Read(project.Root).AgentDeployments);
    }

    [Fact]
    public void RemoveWithDeploymentsRejectsStaleETag()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        var agent = store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.RemoveWithDeployments(project.Root, agent.AgentId, "99"));

        Assert.Equal("etag-mismatch", failure.ErrorKind);
        Assert.Single(store.Read(project.Root).ManagedAgents);
    }

    [Fact]
    public void DuplicateAgentIdIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Agent(displayName: "Other")));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void DuplicateAgentNameIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Agent(agentId: "agent-2")));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void AgentLocationOutsideTheProjectIsRejected()
    {
        using var project = new TemporaryProject();

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent() with { AgentLocation = "../elsewhere" }));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void RootedAgentLocationIsRejected()
    {
        using var project = new TemporaryProject();

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent() with { AgentLocation = @"C:\elsewhere" }));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void TwoAgentsSharingALocationAreRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Agent("agent-2", "Another Agent")));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void AgentWhoseWorkspaceIsMissingStillReads()
    {
        using var project = new TemporaryProject(withProjectFile: false);

        var stored = StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.Equal("agent-1", stored.AgentId);
    }

    [Fact]
    public void DuplicateDeploymentIdIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment(name: "other", stage: DeploymentStages.Test, environmentId: "env-test")));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void DeploymentReferencingUnknownAgentIsRejected()
    {
        using var project = new TemporaryProject();

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Add(project.Root, StoreFixtures.Deployment()));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void SecondDeploymentInTheSameEnvironmentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-1"));

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment("slot-2", name: "dev-two")));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
        Assert.Contains("environment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondDeploymentWithTheSameStageInAnotherEnvironmentIsAccepted()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-1", name: "test-one", stage: DeploymentStages.Test, environmentId: "env-test"));

        var stored = store.Add(project.Root, StoreFixtures.Deployment("slot-2", name: "test-two", stage: DeploymentStages.Test, environmentId: "env-test-two"));

        Assert.Equal("slot-2", stored.DeploymentId);
    }

    [Fact]
    public void SecondDeploymentWithSameNameForOneAgentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-1", name: "shared", stage: DeploymentStages.Test, environmentId: "env-test"));

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment("slot-2", name: "SHARED", stage: DeploymentStages.Prod, environmentId: "env-prod")));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
    }

    [Fact]
    public void SecondDeploymentTargetingSameEnvironmentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-shared"));

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment("slot-prod", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-shared")));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
        Assert.Contains("environment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonDevDeploymentTargetingTheHomeEnvironmentCollidesWithTheDevSlot()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-dev"));

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "env-home")));

        Assert.Equal("deployment-already-exists", failure.ErrorKind);
    }

    [Fact]
    public void DevDeploymentMayCarryAnEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var stored = store.Add(project.Root, StoreFixtures.Deployment(environmentId: "env-dev"));

        Assert.Equal("env-dev", stored.EnvironmentId);
    }

    [Fact]
    public void DevDeploymentDefaultsToItsAgentsHomeEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var stored = store.Add(project.Root, StoreFixtures.Deployment());

        Assert.Equal("env-home", stored.EnvironmentId);
    }

    [Fact]
    public void EveryDeploymentRecordsAnEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment("slot-1", name: "dev"));
        store.Add(project.Root, StoreFixtures.Deployment("slot-2", name: "test", stage: DeploymentStages.Test, environmentId: "env-test"));

        Assert.All(store.Read(project.Root).AgentDeployments, deployment => Assert.False(string.IsNullOrWhiteSpace(deployment.EnvironmentId)));
    }

    [Fact]
    public void StoredDocumentWritesTheEnvironmentOnEveryDeployment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        Assert.Contains("\"environmentId\": \"env-home\"", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownStageIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment(name: "staging", stage: "staging", environmentId: "env-staging")));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Fact]
    public void RejectedWriteLeavesThePreviousFileIntact()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        var before = File.ReadAllText(project.ConfigPath);

        Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Agent(agentId: "agent-2")));

        Assert.Equal(before, File.ReadAllText(project.ConfigPath));
    }

    [Fact]
    public void RejectedWriteLeavesNoTemporaryFiles()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Agent(agentId: "agent-2")));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(project.ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void AWriteWhileAnotherCommandIsWritingTheConfigurationFailsAsLockedAndWritesNothing()
    {
        using var project = new TemporaryProject();
        var locator = new AgentConfigLocator();
        using var holder = HeldNamedMutex.OnAnotherThread(JsonAgentConfigStore.WriteLockNamePrefix, locator.ConfigFilePath(project.Root));

        var failure = Assert.Throws<ManagedAgentException>(() => new JsonAgentConfigStore(locator, new StoreClock(), TimeSpan.Zero).Add(project.Root, StoreFixtures.Agent()));

        Assert.Equal("config-locked", failure.ErrorKind);
        Assert.Equal(ExitCodes.Failure, failure.ExitCode);
        Assert.False(File.Exists(project.ConfigPath));
    }

    [Fact]
    public void ReadWriteRoundTripIsByteIdenticalWhenNothingChanges()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());
        var before = File.ReadAllText(project.ConfigPath);

        var reserialized = System.Text.Json.JsonSerializer.Serialize(store.Read(project.Root), AgentConfigJsonContext.Default.AgentConfigDocument);

        Assert.Equal(before, reserialized);
    }

    [Fact]
    public void StoredETagsAreReadableText()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        var agent = store.Add(project.Root, StoreFixtures.Agent());
        store.Update(project.Root, agent with { DisplayName = "Renamed" });

        Assert.Contains("\"entityTag\": \"2\"", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFileIsATypedFailure()
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, "{ not json");

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Read(project.Root));

        Assert.Equal("config-invalid", failure.ErrorKind);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ASupportedSchemaVersionReads(int version)
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, $"{{ \"schemaVersion\": {version}, \"managedAgents\": [], \"agentDeployments\": [] }}");

        var document = StoreFixtures.Create().Read(project.Root);

        Assert.Empty(document.ManagedAgents);
        Assert.Empty(document.AgentDeployments);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void AnUnsupportedSchemaVersionIsRejected(int version)
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, $"{{ \"schemaVersion\": {version}, \"managedAgents\": [], \"agentDeployments\": [] }}");

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Read(project.Root));

        Assert.Equal("config-version-unsupported", failure.ErrorKind);
    }

    [Fact]
    public void AConfigWithoutASchemaVersionIsReadAsTheCurrentSchema()
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, "{ \"managedAgents\": [], \"agentDeployments\": [] }");

        Assert.Empty(StoreFixtures.Create().Read(project.Root).ManagedAgents);
    }

    [Fact]
    public void AConfigWithoutASchemaVersionGainsOneOnRewrite()
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, "{ \"managedAgents\": [], \"agentDeployments\": [] }");

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.Contains($"\"schemaVersion\": {AgentConfigSchema.Version}", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaVersionIsWritten()
    {
        using var project = new TemporaryProject();

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.Contains($"\"schemaVersion\": {AgentConfigSchema.Version}", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderSupportedSchemaIsUpgradedOnRewrite()
    {
        using var project = new TemporaryProject();
        Directory.CreateDirectory(Path.GetDirectoryName(project.ConfigPath)!);
        File.WriteAllText(project.ConfigPath, "{ \"schemaVersion\": 2, \"managedAgents\": [], \"agentDeployments\": [] }");

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.Contains($"\"schemaVersion\": {AgentConfigSchema.Version}", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredDocumentOmitsNullFields()
    {
        using var project = new TemporaryProject();

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.DoesNotContain("description", File.ReadAllText(project.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredDocumentCarriesNoCloneSourceField()
    {
        using var project = new TemporaryProject();

        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        Assert.DoesNotContain("mcsAgentId", File.ReadAllText(project.ConfigPath), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AgentConfigLocatorTests
{
    [Fact]
    public void ConfigPathIsUnderDotConfigOfTheProject()
    {
        using var project = new TemporaryProject();

        Assert.Equal(Path.Combine(project.Root, ".config", "agent.config.json"), new AgentConfigLocator().ConfigFilePath(project.Root));
    }

    [Fact]
    public void ConfigInAParentDirectoryIsNotFound()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        var child = Path.Combine(project.Root, "topics");
        Directory.CreateDirectory(child);

        Assert.False(new AgentConfigLocator().ConfigExists(child));
    }

    [Fact]
    public void ProjectIsDetectedFromSettingsFile()
    {
        using var project = new TemporaryProject();

        Assert.NotNull(new AgentConfigLocator().AgentWorkspaceDirectory(project.Root, AgentConfigLocator.CurrentDirectoryLocation));
    }

    [Fact]
    public void ProjectIsDetectedFromClassicAgentFile()
    {
        using var project = new TemporaryProject(withProjectFile: false);
        File.WriteAllText(Path.Combine(project.Root, "agent.mcs.yml"), "kind: GptComponentMetadata\n");

        Assert.NotNull(new AgentConfigLocator().AgentWorkspaceDirectory(project.Root, AgentConfigLocator.CurrentDirectoryLocation));
    }

    [Fact]
    public void EmptyDirectoryIsNotAProject()
    {
        using var project = new TemporaryProject(withProjectFile: false);

        Assert.Null(new AgentConfigLocator().AgentWorkspaceDirectory(project.Root, AgentConfigLocator.CurrentDirectoryLocation));
    }
}

public sealed class DeploymentStagesTests
{
    [Theory]
    [InlineData("dev", DeploymentStages.Dev)]
    [InlineData("DEV", DeploymentStages.Dev)]
    [InlineData(" Test ", DeploymentStages.Test)]
    [InlineData("PROD", DeploymentStages.Prod)]
    public void KnownStagesParseToCanonicalText(string input, string expected)
    {
        Assert.True(DeploymentStages.TryParse(input, out var stage));
        Assert.Equal(expected, stage);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownStagesDoNotParse(string? input)
    {
        Assert.False(DeploymentStages.TryParse(input, out _));
    }

    [Fact]
    public void StagesRankInPromotionOrder()
    {
        Assert.True(DeploymentStages.Rank(DeploymentStages.Dev) < DeploymentStages.Rank(DeploymentStages.Test));
        Assert.True(DeploymentStages.Rank(DeploymentStages.Test) < DeploymentStages.Rank(DeploymentStages.Prod));
        Assert.Equal(int.MaxValue, DeploymentStages.Rank("staging"));
    }
}
