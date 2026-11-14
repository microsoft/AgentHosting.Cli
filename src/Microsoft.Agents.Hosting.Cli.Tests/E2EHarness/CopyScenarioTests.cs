using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class CopyScenarioTests : IDisposable
{
    private const string SourceFolder = "MCS Classic Agent";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "msagent-copy-scenario-" + Guid.NewGuid().ToString("N"));

    public CopyScenarioTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(SourceFolder)]
    [InlineData(AgentConfigLocator.CurrentDirectoryLocation)]
    public void TheLocalSourceIsTheWorkspaceItsRecordLocates(string agentLocation)
    {
        var source = RegisteredSource(agentLocation);

        Assert.Equal(Path.GetFullPath(Path.Combine(source.ProjectDirectory, agentLocation)), CopySteps.LocalSourceDirectory(source));
    }

    [Fact]
    public void ACopyRegisteredBesideTheSourceInItsProjectLeavesTheLocalSourceIntact()
    {
        var source = RegisteredSource(SourceFolder);
        var before = CopySteps.SnapshotLocalSource(source);

        Write(Path.Combine(source.ProjectDirectory, "ahe2e copy", AgentConfigLocator.ProjectSettingsFileName), "displayName: ahe2e copy");
        ManagedAgentFixtures.Store().Add(source.ProjectDirectory, ManagedAgentFixtures.Agent("copy-1", "ahe2e copy") with { AgentLocation = "ahe2e copy" });

        Assert.Empty(CopySteps.SnapshotLocalSource(source).DifferencesFrom(before));
    }

    [Fact]
    public void AnEditInsideTheLocalSourceIsReported()
    {
        var source = RegisteredSource(SourceFolder);
        var before = CopySteps.SnapshotLocalSource(source);

        Write(Path.Combine(source.ProjectDirectory, SourceFolder, "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog\nedited: true");

        Assert.Equal([$"changed {Path.Combine("topics", "Greeting.mcs.yml")}"], CopySteps.SnapshotLocalSource(source).DifferencesFrom(before));
    }

    [Fact]
    public void ASourceItsProjectDoesNotRegisterIsRefused()
    {
        var source = RegisteredSource(SourceFolder) with { RecordId = "unregistered" };

        Assert.Throws<E2EException>(() => CopySteps.LocalSourceDirectory(source));
    }

    [Theory]
    [InlineData("MC-33")]
    [InlineData("MC-104")]
    public void AScenarioThatDeploysTheSessionSeedCountsAsCreatingWhenItRunsAlone(string scenarioId)
    {
        Assert.Equal(SafetyTier.Creating, Assert.Single(ScenarioCatalogue.Select(new RunFilters { Packs = ["copy"], Only = [scenarioId] })).Tier);
    }

    [Fact]
    public void ConnectorsSharingAnInternalIdCountAsOneConnector()
    {
        var source = Path.Combine(_root, "baseline");
        Connector(source, "Weather-a1ac675b", "shared_weather-5f566c");
        Connector(source, "Weather-8b261c24", "SHARED_WEATHER-5f566c");
        Write(Path.Combine(source, "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog");
        var copy = Path.Combine(_root, "copy");
        Connector(copy, "Weather-8a392148", "shared_weather-20416699-5f566c");
        Write(Path.Combine(copy, "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog");

        Assert.Equal("component=1, connector=1", McsCopyScenarios.Shape(source));
        Assert.Equal(McsCopyScenarios.Shape(source), McsCopyScenarios.Shape(copy));
    }

    [Fact]
    public void ConnectorsWithDistinctInternalIdsCountSeparately()
    {
        var directory = Path.Combine(_root, "agent");
        Connector(directory, "Weather", "shared_weather");
        Connector(directory, "Traffic", "shared_traffic");

        Assert.Equal("connector=2", McsCopyScenarios.Shape(directory));
    }

    private Workspace RegisteredSource(string agentLocation)
    {
        var project = Path.Combine(_root, "MC-37-clone");
        Write(Path.Combine(project, agentLocation, AgentConfigLocator.ProjectSettingsFileName), "displayName: MCS Classic Agent");
        Write(Path.Combine(project, agentLocation, "topics", "Greeting.mcs.yml"), "kind: AdaptiveDialog");
        ManagedAgentFixtures.Store().Add(project, ManagedAgentFixtures.Agent("source-1", "MCS Classic Agent") with { AgentLocation = agentLocation });

        return new Workspace { ScenarioId = "MC-37", Root = _root, ProjectDirectory = project, AgentName = "MCS Classic Agent", SchemaName = "cr1_classic", RecordId = "source-1" };
    }

    private static void Connector(string directory, string folder, string internalId)
    {
        Write(Path.Combine(directory, "connectors", folder, "metadata.yml"), $$"""{"connectorinternalid":"{{internalId}}"}""");
        Write(Path.Combine(directory, "connectors", folder, "openapidefinition.json"), "{}");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
