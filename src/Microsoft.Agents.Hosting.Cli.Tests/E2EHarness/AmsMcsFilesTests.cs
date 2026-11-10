using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsFilesTests : IDisposable
{
    private const string AgentId = "11111111-0000-4000-8000-000000000001";
    private const string RepositoryId = "22222222-aaaa-4000-8000-00000000000b";
    private const string DeploymentId = "33333333-0000-4000-8000-000000000003";
    private const string McsAgentId = "44444444-0000-4000-8000-00000000000a";

    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void TheConfigIsReadAsMsagentWroteIt()
    {
        var document = Document();
        _scratch.Place(Path.Combine("project", ".config", "agent.config.json"), JsonSerializer.Serialize(document, AgentConfigJsonContext.Default.AgentConfigDocument));

        var read = AmsMcsFiles.ReadAgentConfig(Path.Combine(_scratch.Path, "project"));

        Assert.Equal(AgentId, Assert.Single(read!.ManagedAgents).AgentId);
        Assert.Equal(RepositoryId, read.ManagedAgents[0].SourceControlMetadata!.RepositoryId);
        Assert.Equal(DeploymentId, Assert.Single(read.AgentDeployments).DeploymentId);
        Assert.Null(AmsMcsFiles.ReadAgentConfig(_scratch.Folder("empty")));
    }

    [Fact]
    public void TheConnectionIsReadAsSyncWroteIt()
    {
        var written = new AgentSyncInfo { DataverseEndpoint = new Uri("https://dev.example.invalid/"), EnvironmentId = AmsMcsTestKit.Dev, AgentId = Guid.Parse(AgentId) };
        _scratch.Place(Path.Combine("agent", ".mcs", "conn.json"), JsonSerializer.Serialize(written));

        var read = AmsMcsFiles.ReadConnection(Path.Combine(_scratch.Path, "agent"));

        Assert.Equal(written.DataverseEndpoint, read!.DataverseEndpoint);
        Assert.Equal(AmsMcsTestKit.Dev, read.EnvironmentId);
        Assert.Equal(Guid.Parse(AgentId), read.AgentId);
        Assert.Null(AmsMcsFiles.ReadConnection(_scratch.Folder("bare")));
    }

    [Fact]
    public void TheIgnoreFilesAreReadLineByLine()
    {
        _scratch.Place(Path.Combine("project", ".gitignore"), "/.config/agent.config.json\n.mcs/\n");
        _scratch.Place(Path.Combine("project", "agent", ".mcs", ".gitignore"), "*\r\n");

        Assert.Equal(["/.config/agent.config.json", ".mcs/"], AmsMcsFiles.ReadRootIgnore(Path.Combine(_scratch.Path, "project")));
        Assert.Equal(["*"], AmsMcsFiles.ReadCacheIgnore(Path.Combine(_scratch.Path, "project", "agent")));
        Assert.Null(AmsMcsFiles.ReadRootIgnore(_scratch.Folder("nothing")));
        Assert.Null(AmsMcsFiles.ReadCacheIgnore(_scratch.Folder("nothing")));
    }

    [Fact]
    public void AFingerprintListsEveryEntryWithTheHashOfEachFile()
    {
        var folder = _scratch.Folder("cache");
        _scratch.Place(Path.Combine("cache", "b.txt"), "beta");
        _scratch.Place(Path.Combine("cache", "a", "c.txt"), "gamma");
        _scratch.Folder(Path.Combine("cache", "empty"));

        Assert.Equal($"a/;a/c.txt={Sha("gamma")};b.txt={Sha("beta")};empty/", AmsMcsFiles.Fingerprint(folder));
        Assert.Null(AmsMcsFiles.Fingerprint(Path.Combine(_scratch.Path, "missing")));
    }

    [Fact]
    public void AFingerprintChangesWithAByteANameOrAHiddenFileAndOnlyThen()
    {
        var folder = _scratch.Folder("cache");
        var file = _scratch.Place(Path.Combine("cache", "botdefinition.json"), "{}");
        var original = AmsMcsFiles.Fingerprint(folder);
        var unchanged = AmsMcsFiles.Fingerprint(folder);

        File.WriteAllText(file, "{ }");
        var changedByte = AmsMcsFiles.Fingerprint(folder);
        File.Move(file, Path.Combine(folder, "renamed.json"));
        var renamed = AmsMcsFiles.Fingerprint(folder);
        File.SetAttributes(_scratch.Place(Path.Combine("cache", ".hidden"), "h"), FileAttributes.Hidden);
        var withHidden = AmsMcsFiles.Fingerprint(folder);

        Assert.Equal(original, unchanged);
        Assert.Equal(4, new[] { original, changedByte, renamed, withHidden }.Distinct().Count());
        Assert.StartsWith($".hidden={Sha("h")};", withHidden, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ABC", "abc", true)]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("abc", null, false)]
    public void IdsCompareWithoutCase(string? first, string? second, bool same)
    {
        Assert.Equal(same, AmsIds.Same(first, second));
    }

    [Theory]
    [InlineData("11111111-aaaa-4000-8000-00000000000a", true)]
    [InlineData("11111111-AAAA-4000-8000-00000000000A", false)]
    [InlineData("{11111111-aaaa-4000-8000-00000000000a}", false)]
    [InlineData("11111111aaaa4000800000000000000a", false)]
    [InlineData("not-a-guid", false)]
    [InlineData(null, false)]
    public void AnIdIsLowercaseOnlyAsALowercaseHyphenatedGuid(string? id, bool lowercase)
    {
        Assert.Equal(lowercase, AmsIds.IsLowercase(id));
    }

    [Fact]
    public void ARestoreWithLowercaseIdsHasNothingToReport()
    {
        Assert.Empty(AmsIds.NotLowercase(Document(), [AgentId.ToUpperInvariant()]));
    }

    [Fact]
    public void EachIdARestoreWroteInUppercaseIsReported()
    {
        var document = Document();
        var agent = document.ManagedAgents[0] with { SourceControlMetadata = new AgentSourceControlMetadata(RepositoryId.ToUpperInvariant(), "main", "Agent"), McsAgentId = "44444444-0000-4000-8000-00000000000A" };
        var deployment = document.AgentDeployments[0] with { HomeEnvironmentId = AmsMcsTestKit.Dev.ToUpperInvariant(), EnvironmentId = null };

        var reported = AmsIds.NotLowercase(document with { ManagedAgents = [agent], AgentDeployments = [deployment] }, [AgentId]);

        Assert.Equal([$"{AgentId}: repositoryId='{RepositoryId.ToUpperInvariant()}'", $"{AgentId}: mcsAgentId='44444444-0000-4000-8000-00000000000A'", $"{AgentId}: development.homeEnvironmentId='{AmsMcsTestKit.Dev.ToUpperInvariant()}'"], reported);
    }

    [Fact]
    public void AnAgentTheConfigDoesNotHoldIsReported()
    {
        Assert.Equal(["55555555-0000-4000-8000-000000000005: not in .config"], AmsIds.NotLowercase(Document(), ["55555555-0000-4000-8000-000000000005"]));
    }

    [Fact]
    public void AnAgentRestoredWithoutItsRepositoryIsReported()
    {
        var document = Document();

        Assert.Equal([$"{AgentId}: repositoryId=''"], AmsIds.NotLowercase(document with { ManagedAgents = [document.ManagedAgents[0] with { SourceControlMetadata = null }] }, [AgentId]));
    }

    [Fact]
    public void AgentsAndDeploymentsAreFoundByIdWithoutCase()
    {
        var document = Document();
        var project = _scratch.Folder("project");

        Assert.Equal(Path.Combine(project, ".config", "agent.config.json"), AmsMcsFiles.AgentConfigPath(project));
        Assert.Same(document.ManagedAgents[0], AmsMcsFiles.Agent(document, AgentId.ToUpperInvariant()));
        Assert.Null(AmsMcsFiles.Agent(document, DeploymentId));
        Assert.Null(AmsMcsFiles.Agent(null, AgentId));
        Assert.Same(document.AgentDeployments[0], AmsMcsFiles.Deployment(document, DeploymentId.ToUpperInvariant()));
        Assert.Null(AmsMcsFiles.Deployment(document, AgentId));
        Assert.Null(AmsMcsFiles.Deployment(null, DeploymentId));
        Assert.Equal([document.AgentDeployments[0]], AmsMcsFiles.Deployments(document, AgentId.ToUpperInvariant()));
        Assert.Empty(AmsMcsFiles.Deployments(document, DeploymentId));
        Assert.Empty(AmsMcsFiles.Deployments(null, AgentId));
    }

    [Fact]
    public void TheSyncFilesHashNamesEachSyncFileThereWithItsHash()
    {
        var folder = _scratch.Folder("agent");
        var empty = AmsMcsFiles.SyncFilesHash(folder);
        _scratch.Place(Path.Combine("agent", "agent.sync.yaml"), "sync");
        var one = AmsMcsFiles.SyncFilesHash(folder);
        _scratch.Place(Path.Combine("agent", "settings.mcs.yml"), "displayName: A");
        _scratch.Place(Path.Combine("agent", "topics.mcs.yml"), "ignored");

        Assert.Equal(string.Empty, empty);
        Assert.Equal($"agent.sync.yaml={Sha("sync")}", one);
        Assert.Equal($"settings.mcs.yml={Sha("displayName: A")};agent.sync.yaml={Sha("sync")}", AmsMcsFiles.SyncFilesHash(folder));
    }

    [Fact]
    public void AFolderIsMissingOrEmptyUntilItHoldsAnEntry()
    {
        var folder = Path.Combine(_scratch.Path, "target");
        var missing = AmsMcsFiles.IsMissingOrEmpty(folder);
        Directory.CreateDirectory(folder);
        var empty = AmsMcsFiles.IsMissingOrEmpty(folder);
        Directory.CreateDirectory(Path.Combine(folder, ".git"));

        Assert.True(missing);
        Assert.True(empty);
        Assert.False(AmsMcsFiles.IsMissingOrEmpty(folder));
    }

    [Fact]
    public void TheSettingsAreReadWhenTheFolderHasThem()
    {
        _scratch.Place(Path.Combine("agent", "settings.mcs.yml"), "displayName: ahe2e Home A\n");

        Assert.Equal("displayName: ahe2e Home A\n", AmsMcsFiles.ReadSettings(Path.Combine(_scratch.Path, "agent")));
        Assert.Null(AmsMcsFiles.ReadSettings(_scratch.Folder("bare")));
    }

    [Fact]
    public void AnAgentsSettingsLiveInItsFolderAsGitNamesThem()
    {
        Assert.Equal(Path.Combine("project", "Agent A", "settings.mcs.yml"), AmsMcsFiles.SettingsPath("project", "Agent A"));
        Assert.Equal("Agents/Agent A/settings.mcs.yml", AmsMcsFiles.SettingsEntry("Agents/Agent A"));
    }

    [Fact]
    public void AnAgentsTopicLivesInItsTopicsFolderAsGitNamesIt()
    {
        Assert.Equal(Path.Combine("project", "Agent A", "topics", "ThankYou.mcs.yml"), AmsMcsFiles.TopicPath("project", "Agent A", "ThankYou"));
        Assert.Equal("Agent A/topics/ThankYou.mcs.yml", AmsMcsFiles.TopicEntry("Agent A", "ThankYou"));
    }

    [Fact]
    public void TheRecordedBotAndTheCacheAreReadFromTheProject()
    {
        var project = WriteProject(McsAgentId);
        _scratch.Place(Path.Combine("project", "Agent", ".mcs", "conn.json"), "{}");

        Assert.Equal(McsAgentId, AmsMcsFiles.RecordedBot(project, AgentId));
        Assert.Null(AmsMcsFiles.RecordedBot(project, DeploymentId));
        Assert.Null(AmsMcsFiles.RecordedBot(_scratch.Folder("bare"), AgentId));
        Assert.Equal($"conn.json={Sha("{}")}", AmsMcsFiles.CacheFingerprint(project, "Agent"));
        Assert.Null(AmsMcsFiles.CacheFingerprint(project, "Other"));
    }

    [Fact]
    public void TheConnectionFactsGatherConnJsonTheCacheIgnoreAndTheRecordedBot()
    {
        var project = WriteProject(McsAgentId);
        _scratch.Place(Path.Combine("project", "Agent", ".mcs", "conn.json"), JsonSerializer.Serialize(new AgentSyncInfo { DataverseEndpoint = new Uri("https://dev.example.invalid/"), EnvironmentId = AmsMcsTestKit.Dev, AgentId = Guid.Parse(McsAgentId) }));
        _scratch.Place(Path.Combine("project", "Agent", ".mcs", ".gitignore"), "*\n");

        var facts = AmsMcsFiles.Connection(project, "Agent", AgentId);

        Assert.Equal(Guid.Parse(McsAgentId), facts.Connection!.AgentId);
        Assert.Equal(["*"], facts.CacheIgnore);
        Assert.Equal(McsAgentId, facts.RecordedBot);
        Assert.Equal($"conn.json: AgentId={McsAgentId} EnvironmentId={AmsMcsTestKit.Dev} DataverseEndpoint=https://dev.example.invalid/; .mcs\\.gitignore='*'; mcsAgentId={McsAgentId}", facts.Describe());
        Assert.Equal("conn.json: AgentId= EnvironmentId= DataverseEndpoint=; .mcs\\.gitignore=''; mcsAgentId=", new AmsConnectionFacts(null, null, null).Describe());
    }

    [Theory]
    [InlineData(McsAgentId, AmsMcsTestKit.Dev, true)]
    [InlineData("44444444-0000-4000-8000-00000000000A", "AAAAAAAA-0000-4000-8000-000000000001", true)]
    [InlineData("not-a-bot", AmsMcsTestKit.Dev, false)]
    [InlineData(null, AmsMcsTestKit.Dev, false)]
    [InlineData(DeploymentId, AmsMcsTestKit.Dev, false)]
    [InlineData(McsAgentId, AmsMcsTestKit.Test, false)]
    public void AConnectionNamesTheBotAndTheEnvironmentItIsBoundTo(string? botId, string environmentId, bool names)
    {
        Assert.Equal(names, Facts().Names(botId, environmentId));
    }

    [Fact]
    public void NoConnectionOrOneWithoutABotNamesNothing()
    {
        Assert.False(new AmsConnectionFacts(null, ["*"], McsAgentId).Names(McsAgentId, AmsMcsTestKit.Dev));
        Assert.False(new AmsConnectionFacts(new AgentSyncInfo { EnvironmentId = AmsMcsTestKit.Dev }, ["*"], McsAgentId).Names(McsAgentId, AmsMcsTestKit.Dev));
    }

    [Fact]
    public void AConnectionBindsToABotWhenConnJsonTheOrganizationTheCacheIgnoreAndTheRecordedBotAllAgree()
    {
        var organization = new Uri("https://DEV.example.invalid");
        var facts = Facts();

        Assert.True(facts.BindsTo(McsAgentId, AmsMcsTestKit.Dev, organization));
        Assert.False(facts.BindsTo(McsAgentId, AmsMcsTestKit.Test, organization));
        Assert.False(facts.BindsTo(McsAgentId, AmsMcsTestKit.Dev, null));
        Assert.False(facts.BindsTo(McsAgentId, AmsMcsTestKit.Dev, new Uri("https://test.example.invalid")));
        Assert.False((facts with { Connection = new AgentSyncInfo { EnvironmentId = AmsMcsTestKit.Dev, AgentId = Guid.Parse(McsAgentId) } }).BindsTo(McsAgentId, AmsMcsTestKit.Dev, organization));
        Assert.False((facts with { CacheIgnore = ["bin/"] }).BindsTo(McsAgentId, AmsMcsTestKit.Dev, organization));
        Assert.False((facts with { CacheIgnore = null }).BindsTo(McsAgentId, AmsMcsTestKit.Dev, organization));
        Assert.False((facts with { RecordedBot = DeploymentId }).BindsTo(McsAgentId, AmsMcsTestKit.Dev, organization));
    }

    [Fact]
    public void AFolderStateIsItsCacheAndRecordedBot()
    {
        var project = WriteProject(McsAgentId);
        _scratch.Place(Path.Combine("project", "Agent", ".mcs", "conn.json"), "{}");

        Assert.Equal(new AmsFolderState($"conn.json={Sha("{}")}", McsAgentId), AmsFolderState.Read(project, "Agent", AgentId));
        Assert.Equal(new AmsFolderState(null, null), AmsFolderState.Read(_scratch.Folder("bare"), "Agent", AgentId));
    }

    [Fact]
    public void AFolderIsKeptWhenNothingChangedAndItsCacheIsThereExactlyWhenConnected()
    {
        var connected = new AmsFolderState("cache", McsAgentId);
        var absent = new AmsFolderState(null, McsAgentId);

        Assert.True(connected.Kept(connected with { }, connected: true));
        Assert.False(connected.Kept(connected, connected: false));
        Assert.True(absent.Kept(absent with { }, connected: false));
        Assert.False(absent.Kept(absent, connected: true));
        Assert.False(connected.Kept(connected with { Cache = "other" }, connected: true));
        Assert.False(connected.Kept(connected with { Bot = DeploymentId }, connected: true));
    }

    [Fact]
    public void AFolderStateDescribesWhatHappenedToTheCacheAndTheBot()
    {
        var before = new AmsFolderState("cache", "bot-1");

        Assert.Equal(".mcs: unchanged mcsAgentId: bot-1->bot-1", before.Describe(before));
        Assert.Equal(".mcs: changed mcsAgentId: bot-1->bot-2", before.Describe(new AmsFolderState("other", "bot-2")));
        Assert.Equal(".mcs: absent mcsAgentId: bot-1->", before.Describe(new AmsFolderState(null, null)));
    }

    [Fact]
    public void ALineIsHeldWithoutCaseOrTrailingSpaceAndNoLinesHoldNothing()
    {
        Assert.True(AmsMcsFiles.HasLine(["bin/", ".MCS/  "], ".mcs/"));
        Assert.False(AmsMcsFiles.HasLine([" .mcs/"], ".mcs/"));
        Assert.False(AmsMcsFiles.HasLine(null, ".mcs/"));
    }

    [Fact]
    public void AFileHashIsItsSha256AndAMissingFileHasNone()
    {
        Assert.Equal(Sha("content"), AmsMcsFiles.FileHash(_scratch.Place("file.txt", "content")));
        Assert.Null(AmsMcsFiles.FileHash(Path.Combine(_scratch.Path, "missing.txt")));
    }

    [Fact]
    public void TheTopEntriesAreTheOrderedNamesInAFolder()
    {
        _scratch.Place(Path.Combine("project", "b.txt"));
        _scratch.Place(Path.Combine("project", "A", "nested.txt"));
        _scratch.Folder(Path.Combine("project", ".git"));

        Assert.Equal(".git,A,b.txt", AmsMcsFiles.TopEntries(Path.Combine(_scratch.Path, "project")));
        Assert.Equal(string.Empty, AmsMcsFiles.TopEntries(Path.Combine(_scratch.Path, "missing")));
    }

    [Theory]
    [InlineData("kind: Bot\r\ndisplayName: Old\r\n", "kind: Bot\r\ndisplayName: New\r\n")]
    [InlineData("displayName: \"Old\"\n", "displayName: \"New\"\n")]
    public void ANewDisplayNameIsWrittenWithTheFilesLineEndingsAndQuotes(string yaml, string renamed)
    {
        var path = _scratch.Place("settings.mcs.yml", yaml);

        AmsMcsFiles.SetDisplayName(path, "New");

        Assert.Equal(renamed, File.ReadAllText(path));
        Assert.False(HasBom(path));
    }

    [Fact]
    public void ANewDisplayNameKeepsTheFilesBom()
    {
        var path = Path.Combine(_scratch.Path, "settings.mcs.yml");
        File.WriteAllText(path, "displayName: Old\n", new UTF8Encoding(true));

        AmsMcsFiles.SetDisplayName(path, "New");

        Assert.True(HasBom(path));
        Assert.Equal("displayName: New\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("kind: Bot\n")]
    [InlineData("displayName: New\n")]
    public void ADisplayNameThatChangesNothingIsRefused(string yaml)
    {
        var path = _scratch.Place("settings.mcs.yml", yaml);

        var failure = Assert.Throws<E2EException>(() => AmsMcsFiles.SetDisplayName(path, "New"));

        Assert.Equal($"{path} declares no displayName, or already holds 'New'.", failure.Message);
        Assert.Equal(yaml, File.ReadAllText(path));
    }

    [Fact]
    public void ANewDescriptionIsWrittenWithTheFilesLineEndingsAndBom()
    {
        var path = Path.Combine(_scratch.Path, "Greeting.mcs.yml");
        File.WriteAllText(path, "mcs.metadata:\r\n  componentName: Greeting\r\n  description: Greets.\r\nkind: AdaptiveDialog\r\n", new UTF8Encoding(true));

        AmsMcsFiles.SetDescription(path, "ahe2e main edit x");

        Assert.True(HasBom(path));
        Assert.Equal("mcs.metadata:\r\n  componentName: Greeting\r\n  description: ahe2e main edit x\r\nkind: AdaptiveDialog\r\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("mcs.metadata:\n  componentName: Greeting\n")]
    [InlineData("mcs.metadata:\n  description: New\n")]
    public void ADescriptionThatChangesNothingIsRefused(string yaml)
    {
        var path = _scratch.Place("Greeting.mcs.yml", yaml);

        var failure = Assert.Throws<E2EException>(() => AmsMcsFiles.SetDescription(path, "New"));

        Assert.Equal($"{path} declares no description, or already holds 'New'.", failure.Message);
        Assert.Equal(yaml, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("displayName: A\r\n", "displayName: A\r\n# ahe2e note\r\n")]
    [InlineData("displayName: A\n", "displayName: A\n# ahe2e note\n")]
    [InlineData("displayName: A", "displayName: A\n# ahe2e note\n")]
    [InlineData("kind: Bot\r\ndisplayName: A", "kind: Bot\r\ndisplayName: A\r\n# ahe2e note\r\n")]
    [InlineData("", "# ahe2e note\n")]
    public void AYamlCommentIsAppendedOnItsOwnLineWithTheFilesLineEndings(string yaml, string commented)
    {
        var path = _scratch.Place("settings.mcs.yml", yaml);

        AmsMcsFiles.AddYamlComment(path, "ahe2e note");

        Assert.Equal(commented, File.ReadAllText(path));
    }

    [Fact]
    public void AYamlCommentKeepsTheFilesBom()
    {
        var path = Path.Combine(_scratch.Path, "settings.mcs.yml");
        File.WriteAllText(path, "displayName: A\n", new UTF8Encoding(true));

        AmsMcsFiles.AddYamlComment(path, "ahe2e note");

        Assert.True(HasBom(path));
        Assert.Equal("displayName: A\n# ahe2e note\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(null, "connectionReferences:\n  - connectionReferenceLogicalName: cr_new\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_new\n")]
    [InlineData("connectionReferences: []\r\n", "connectionReferences:\r\n  - connectionReferenceLogicalName: cr_new\r\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_new\r\n")]
    [InlineData("connectionReferences:\n  - connectionReferenceLogicalName: cr_old\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_old", "connectionReferences:\n  - connectionReferenceLogicalName: cr_old\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_old\n  - connectionReferenceLogicalName: cr_new\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_new\n")]
    public void AConnectionReferenceIsAddedToTheFlatFileWithItsLineEndings(string? yaml, string added)
    {
        var agent = _scratch.Folder("Agent");
        var path = Path.Combine(agent, ConnectionReferenceStore.FlatFileName);

        if (yaml is not null)
        {
            File.WriteAllText(path, yaml);
        }

        AmsMcsFiles.AddConnectionReference(agent, "cr_new", "/providers/Microsoft.PowerApps/apis/shared_new");

        Assert.Equal(added, File.ReadAllText(path));
        Assert.False(HasBom(path));
    }

    [Fact]
    public void AConnectionReferenceKeepsTheFlatFilesBom()
    {
        var agent = _scratch.Folder("Agent");
        var path = Path.Combine(agent, ConnectionReferenceStore.FlatFileName);
        File.WriteAllText(path, "connectionReferences: []\n", new UTF8Encoding(true));

        AmsMcsFiles.AddConnectionReference(agent, "cr_new", "/providers/Microsoft.PowerApps/apis/shared_new");

        Assert.True(HasBom(path));
        Assert.Equal("connectionReferences:\n  - connectionReferenceLogicalName: cr_new\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_new\n", File.ReadAllText(path));
    }

    [Fact]
    public void ACustomConnectorIsNamedForThePublisherAndTheBotWithFreshIds()
    {
        var connector = AmsCustomConnector.For("CR150", "ahe2e_bot", "ahe2e connector 1a2b");
        var other = AmsCustomConnector.For("CR150", "ahe2e_bot", "ahe2e connector 1a2b");

        Assert.Equal(("CR150_5Fahe2econnector1a2b", "ahe2e connector 1a2b"), (connector.Name, connector.DisplayName));
        Assert.Equal("ahe2e connector 1a2b: a custom connector the MCS-on-AMS E2E checks create and delete.", connector.Description);
        Assert.Matches("^shared_cr150-5fahe2econnector1a2b-5f[0-9a-f]{16}$", connector.InternalId);
        Assert.Matches($"^ahe2e_bot\\.{connector.InternalId}\\.[0-9a-f]{{32}}$", connector.ConnectionReference);
        Assert.Equal(($"connectors/CR150_5Fahe2econnector1a2b-{connector.ConnectorId}", "actions/CR150_5Fahe2econnector1a2b.mcs.yml"), (connector.Folder, connector.ToolEntry));
        Assert.NotEqual(connector.ConnectorId, other.ConnectorId);
        Assert.NotEqual(connector.InternalId, other.InternalId);
        Assert.NotEqual(connector.ConnectionReference, other.ConnectionReference);
    }

    [Fact]
    public void ACustomConnectorIsAddedWithTheFixturesFilesItsMetadataItsReferenceAndATool()
    {
        var id = Guid.Parse("a1ac675b-8b84-416e-9450-add1b0db8624");
        var connector = new AmsCustomConnector(id, "cr150_5Fx", "Connector x", "Connector x: a test.", "shared_cr150-5fx-5f0123456789abcdef", "ahe2e_bot.shared_cr150-5fx-5f0123456789abcdef.0123");
        var agent = _scratch.Folder("Agent");
        File.WriteAllText(Path.Combine(agent, ConnectionReferenceStore.FlatFileName), "connectionReferences: []\n");
        var fixture = Path.Combine(HarnessEnvironment.SourceRoot(AppContext.BaseDirectory), "Microsoft.Agents.Hosting.Cli.E2E", "fixtures", AmsCustomConnector.FixtureFolder);

        connector.AddTo(fixture, agent);

        var folder = Path.Combine(agent, "connectors", $"cr150_5Fx-{id}");
        var metadata = File.ReadAllText(Path.Combine(folder, "metadata.yml"));
        string[] copied = ["connectionparameters.json", "iconblob.png", "openapidefinition.json", "policytemplateinstances.json"];
        Assert.Equal(["connectionparameters.json", "iconblob.png", "metadata.yml", "openapidefinition.json", "policytemplateinstances.json"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(copied, name => Assert.Equal(File.ReadAllBytes(Path.Combine(fixture, name)), File.ReadAllBytes(Path.Combine(folder, name))));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"connectorid":"{{id}}","name":"cr150_5Fx","displayname":"Connector x","description":"Connector x: a test.","connectorinternalid":"shared_cr150-5fx-5f0123456789abcdef","openapidefinition":"connectors/cr150_5Fx-{{id}}/openapidefinition.json","connectionparameters":"connectors/cr150_5Fx-{{id}}/connectionparameters.json","connectionparametersets":null,"policytemplateinstances":"connectors/cr150_5Fx-{{id}}/policytemplateinstances.json","iconbrandcolor":"#007ee5","iconblob":"connectors/cr150_5Fx-{{id}}/iconblob.png","connectortype":1,"statecode":0,"statuscode":1,"ismanaged":false,"componentstate":0}"""), JsonNode.Parse(metadata)), metadata);
        Assert.Equal("Connector x: a test.", AmsCustomConnector.DescriptionOf(metadata));
        Assert.Equal("connectionReferences:\n  - connectionReferenceLogicalName: ahe2e_bot.shared_cr150-5fx-5f0123456789abcdef.0123\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_cr150-5fx-5f0123456789abcdef\n", File.ReadAllText(Path.Combine(agent, ConnectionReferenceStore.FlatFileName)));
        Assert.Equal("mcs.metadata:\n  componentName: Connector x\n  description: 'Connector x: a test.'\nkind: TaskDialog\nmodelDisplayName: Connector x\nmodelDescription: 'Connector x: a test.'\naction:\n  kind: InvokeExternalAgentTaskAction\n  connectionReference: ahe2e_bot.shared_cr150-5fx-5f0123456789abcdef.0123\n  connectionProperties:\n    mode: Invoker\n\n  operationDetails:\n    kind: ActivityProtocolMetadata\n    operationId: SendActivity\n", File.ReadAllText(Path.Combine(agent, "actions", "cr150_5Fx.mcs.yml")));
        Assert.False(HasBom(Path.Combine(folder, "metadata.yml")));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("connectorid: not json", null)]
    [InlineData("[]", null)]
    [InlineData("{}", null)]
    [InlineData("{\"description\":5}", null)]
    [InlineData("{\"description\":\"edited\"}", "edited")]
    public void AConnectorsDescriptionIsReadFromItsMetadataJsonOrIsNone(string? metadata, string? description)
    {
        Assert.Equal(description, AmsCustomConnector.DescriptionOf(metadata));
    }

    [Fact]
    public void AConnectorsDescriptionIsReadFromTheMetadataSyncWritesWithAByteOrderMark()
    {
        const string folder = "connectors/cr150_5Fahe2econnectora049-0b6c2f5e-8d1a-4f2b-9c3d-7e4a5b6c7d8e";
        var metadata = $$"""
            {
              "connectorid": "0b6c2f5e-8d1a-4f2b-9c3d-7e4a5b6c7d8e",
              "name": "cr150_5Fahe2econnectora049",
              "displayname": "ahe2e connector a049",
              "description": "ahe2e Dev edit a049",
              "connectorinternalid": "shared_cr150-5fahe2econnectora049-5f8c1d2e3f4a5b6c7d",
              "openapidefinition": "{{folder}}/openapidefinition.json",
              "connectionparameters": "{{folder}}/connectionparameters.json",
              "connectionparametersets": null,
              "policytemplateinstances": "{{folder}}/policytemplateinstances.json",
              "iconbrandcolor": "#007ee5",
              "iconblob": "{{folder}}/iconblob.png",
              "connectortype": 1,
              "statecode": 0,
              "statuscode": 1,
              "ismanaged": false,
              "componentstate": 0,
              "createdon": "2026-11-09T12:41:07Z",
              "modifiedon": "2026-11-09T12:58:44Z",
              "versionnumber": 4581734
            }
            """;

        Assert.Equal("ahe2e Dev edit a049", AmsCustomConnector.DescriptionOf("\uFEFF" + metadata));
    }

    [Fact]
    public void TheWorkflowStateIsWrittenInTheFoldersCacheWithLfAndNoBom()
    {
        var project = _scratch.Folder("project");

        var entry = AmsMcsFiles.WriteWorkflowState(project, "wf");

        Assert.Equal("wf/.mcs/workflow-state.json", entry);
        Assert.Equal("{}\n", File.ReadAllText(Path.Combine(project, entry)));
        Assert.False(HasBom(Path.Combine(project, entry)));
    }

    [Fact]
    public void AComponentsMetadataIsTheEntryOfItsFolderContainerAndIdWithoutCase()
    {
        const string id = "a1ac675b-8b84-416e-9450-add1b0db8624";
        string[] entries = ["Other/connectors/cr_5Fx-a1ac675b-8b84-416e-9450-add1b0db8624/metadata.yml", "Agent/workflows/x-a1ac675b-8b84-416e-9450-add1b0db8624/metadata.yml", "Agent/connectors/cr_5Fx-A1AC675B-8B84-416E-9450-ADD1B0DB8624/openapidefinition.json", "agent/Connectors/cr_5Fx-A1AC675B-8B84-416E-9450-ADD1B0DB8624/metadata.yml"];

        Assert.Equal(entries[3], AmsMcsFiles.ComponentMetadata(entries, "Agent", "connectors", id));
        Assert.Null(AmsMcsFiles.ComponentMetadata(entries[..3], "Agent", "connectors", id));
    }

    [Fact]
    public void AComponentsMetadataPathIsFoundOnDiskAndIsNoneWithoutItsContainerOrId()
    {
        const string id = "a1ac675b-8b84-416e-9450-add1b0db8624";
        var project = _scratch.Folder("project");
        var metadata = _scratch.Place(Path.Combine("project", "Agent", "connectors", $"cr_5Fx-{id}", "metadata.yml"), "{}");
        _scratch.Place(Path.Combine("project", "Agent", "workflows", $"Flow-{id}", "metadata.yml"), "name: Flow");

        Assert.Equal(Path.GetFullPath(metadata), Path.GetFullPath(AmsMcsFiles.ComponentMetadataPath(project, "Agent", "connectors", id)!));
        Assert.Null(AmsMcsFiles.ComponentMetadataPath(project, "Agent", "connectors", "99999999-0000-4000-8000-000000000009"));
        Assert.Null(AmsMcsFiles.ComponentMetadataPath(project, "Agent", "prompts", id));
    }

    [Fact]
    public void TheRootSettingsAreWrittenWithLfAndNoBom()
    {
        var project = _scratch.Folder("project");

        AmsMcsFiles.SetRootSettings(project, "ahe2e Root x");

        Assert.Equal("displayName: ahe2e Root x\n", File.ReadAllText(Path.Combine(project, "settings.mcs.yml")));
        Assert.False(HasBom(Path.Combine(project, "settings.mcs.yml")));
    }

    [Fact]
    public void ACopyLeavesOutTheExcludedNamesAtEveryDepthAndOverwritesWhatIsThere()
    {
        _scratch.Place(Path.Combine("source", "Agent", "settings.mcs.yml"), "displayName: A");
        _scratch.Place(Path.Combine("source", "Agent", ".MCS", "conn.json"), "{}");
        _scratch.Place(Path.Combine("source", ".git", "HEAD"), "ref");
        _scratch.Place(Path.Combine("source", ".gitignore"), ".mcs/");
        _scratch.Place(Path.Combine("destination", ".gitignore"), "old");
        var destination = Path.Combine(_scratch.Path, "destination");

        AmsMcsFiles.CopyWithout(Path.Combine(_scratch.Path, "source"), destination, [".git", ".mcs"]);

        Assert.Equal($".gitignore={Sha(".mcs/")};Agent/;Agent/settings.mcs.yml={Sha("displayName: A")}", AmsMcsFiles.Fingerprint(destination));
    }

    [Fact]
    public void AKnowledgeFileIsWrittenWithLfBesideItsComponent()
    {
        var project = _scratch.Folder("project");

        var (content, component) = AmsMcsFiles.AddKnowledgeFile(project, "Agent A", "notes.txt", "one\ntwo\n");

        Assert.Equal(("Agent A/capabilities/knowledge/files/notes.txt", "Agent A/capabilities/knowledge/files/notes.txt.mcs.yml"), (content, component));
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(project, content)));
        Assert.Equal("mcs.metadata:\n  componentName: notes.txt\n  description: This knowledge source searches information contained in notes.txt\n", File.ReadAllText(Path.Combine(project, component)));
        Assert.False(HasBom(Path.Combine(project, content)));
    }

    [Fact]
    public void AConnectionReferenceIsRenamedInEveryDefinitionAndItsDeclaration()
    {
        var workspace = _scratch.Folder("workspace");
        var definition = _scratch.Place(Path.Combine("workspace", "workflows", "Flow-1", "workflow.json"), "{\"connectionReferenceLogicalName\":\"new_old\"}");
        var metadata = Path.Combine(workspace, "workflows", "Flow-1", "metadata.yml");
        File.WriteAllText(metadata, "references:\n  - new_old\n", new UTF8Encoding(true));
        var declaration = _scratch.Place(Path.Combine("workspace", "infrastructure", "connections", "new_old.sync.yaml"), "logicalName: new_old\n");
        var notes = _scratch.Place(Path.Combine("workspace", "notes.txt"), "new_old");
        var untouched = _scratch.Place(Path.Combine("workspace", "other.yaml"), "name: other\n");

        AmsMcsFiles.RenameConnectionReference(workspace, "new_old", "new_fresh");

        Assert.Equal("{\"connectionReferenceLogicalName\":\"new_fresh\"}", File.ReadAllText(definition));
        Assert.Equal("references:\n  - new_fresh\n", File.ReadAllText(metadata));
        Assert.True(HasBom(metadata));
        Assert.False(File.Exists(declaration));
        Assert.Equal("logicalName: new_fresh\n", File.ReadAllText(Path.Combine(workspace, "infrastructure", "connections", "new_fresh.sync.yaml")));
        Assert.Equal("new_old", File.ReadAllText(notes));
        Assert.Equal("name: other\n", File.ReadAllText(untouched));
    }

    [Fact]
    public void AReferenceWithoutADeclarationIsRenamedInTheDefinitionsOnly()
    {
        var workspace = _scratch.Folder("workspace");
        var definition = _scratch.Place(Path.Combine("workspace", "workflow.json"), "new_old");

        AmsMcsFiles.RenameConnectionReference(workspace, "new_old", "new_fresh");

        Assert.Equal("new_fresh", File.ReadAllText(definition));
        Assert.False(Directory.Exists(Path.Combine(workspace, "infrastructure")));
    }

    [Fact]
    public void ARestoreSignatureListsTheAgentsAndDeploymentsWithoutCaseOrOrder()
    {
        var document = Document();
        var second = document.ManagedAgents[0] with { AgentId = "55555555-0000-4000-8000-000000000005", SourceControlMetadata = new AgentSourceControlMetadata(RepositoryId, "main", "Agent B"), McsAgentId = null };
        var both = document with { ManagedAgents = [second, document.ManagedAgents[0] with { McsAgentId = McsAgentId.ToUpperInvariant() }] };

        Assert.Equal($"{AgentId}|agent|{McsAgentId};55555555-0000-4000-8000-000000000005|agent b| // {AgentId}|{DeploymentId}|development|dev", AmsMcsFiles.RestoreSignature(both));
        Assert.Null(AmsMcsFiles.RestoreSignature(document with { ManagedAgents = [] }));
        Assert.Null(AmsMcsFiles.RestoreSignature(null));
    }

    [Fact]
    public void ADeploymentSignatureListsOneAgentsDeployments()
    {
        var document = Document();

        Assert.Equal($"{DeploymentId}|development|dev", AmsMcsFiles.DeploymentSignature(document, AgentId.ToUpperInvariant()));
        Assert.Equal(string.Empty, AmsMcsFiles.DeploymentSignature(document, DeploymentId));
        Assert.Equal(string.Empty, AmsMcsFiles.DeploymentSignature(null, AgentId));
    }

    [Fact]
    public void ARowRecordedAsListedIsNoMismatch()
    {
        Assert.Empty(AmsMcsFiles.Mismatched(Document(), [ListRow(AgentId.ToUpperInvariant(), "AGENT", "mcsagent")], RepositoryId.ToUpperInvariant()));
    }

    [Fact]
    public void EachRowTheConfigRecordsDifferentlyIsAMismatch()
    {
        var document = Document();
        var agent = document.ManagedAgents[0];
        ManagedAgentListItem[] rows = [ListRow(AgentId, "Agent", "MCSWorkflow"), ListRow(AgentId, "Other", "MCSAgent"), ListRow("55555555-0000-4000-8000-000000000005", "Agent", "MCSAgent"), ListRow(null, "Agent", "MCSAgent")];
        var described = $"type=MCSAgent repository={RepositoryId} branch=main directory=Agent";

        Assert.Equal([$"Agent ({AgentId}): {described}", $"Agent ({AgentId}): {described}", "Agent (55555555-0000-4000-8000-000000000005): not in .config", "Agent (): not in .config"], AmsMcsFiles.Mismatched(document, rows, RepositoryId));
        Assert.Single(AmsMcsFiles.Mismatched(document, [ListRow(AgentId, "Agent", "MCSAgent")], "66666666-0000-4000-8000-000000000006"));
        Assert.Single(AmsMcsFiles.Mismatched(document with { ManagedAgents = [agent with { SourceControlMetadata = new AgentSourceControlMetadata(RepositoryId, "dev", "Agent") }] }, [ListRow(AgentId, "Agent", "MCSAgent")], RepositoryId));
        Assert.Equal([$"Agent ({AgentId}): type=MCSAgent repository= branch= directory="], AmsMcsFiles.Mismatched(document with { ManagedAgents = [agent with { SourceControlMetadata = null }] }, [ListRow(AgentId, "Agent", "MCSAgent")], RepositoryId));
    }

    [Fact]
    public void AFileHoldsATextWithCrlfOnlyWhenItsLinesEndSoAndItsTextMatches()
    {
        Assert.True(AmsMcsFiles.HoldsWithCrlf(_scratch.Place("crlf.txt", "one\r\ntwo\r\n"), "one\ntwo\n"));
        Assert.False(AmsMcsFiles.HoldsWithCrlf(_scratch.Place("lf.txt", "one\ntwo\n"), "one\ntwo\n"));
        Assert.False(AmsMcsFiles.HoldsWithCrlf(_scratch.Place("other.txt", "one\r\nthree\r\n"), "one\ntwo\n"));
        Assert.False(AmsMcsFiles.HoldsWithCrlf(Path.Combine(_scratch.Path, "missing.txt"), "one\ntwo\n"));
    }

    private static AmsConnectionFacts Facts() => new(new AgentSyncInfo { DataverseEndpoint = new Uri("https://dev.example.invalid/"), EnvironmentId = AmsMcsTestKit.Dev, AgentId = Guid.Parse(McsAgentId) }, ["*"], McsAgentId);

    private static ManagedAgentListItem ListRow(string? agentId, string folder, string agentType) => new() { AgentId = agentId, AgentFolder = folder, AgentType = agentType, DisplayName = "Agent", TenantId = AmsMcsTestKit.Tenant, EnvironmentId = AmsMcsTestKit.Dev };

    private static bool HasBom(string path) => File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..];

    private static string Sha(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static AgentConfigDocument Document() => new()
    {
        ManagedAgents =
        [
            new ManagedAgentRecord
            {
                TenantId = AmsMcsTestKit.Tenant,
                EnvironmentId = AmsMcsTestKit.Dev,
                AgentId = AgentId,
                DisplayName = "Agent",
                AgentType = "MCSAgent",
                AgentLocation = "Agent",
                SourceControlMetadata = new AgentSourceControlMetadata(RepositoryId, "main", "Agent"),
                AgentState = "Active",
                EntityTag = "etag",
            },
        ],
        AgentDeployments =
        [
            new AgentDeploymentRecord
            {
                DeploymentId = DeploymentId,
                AgentId = AgentId,
                AgentType = "MCSAgent",
                DeploymentType = "dev",
                DeploymentName = "development",
                TenantId = AmsMcsTestKit.Tenant,
                HomeEnvironmentId = AmsMcsTestKit.Dev,
                EnvironmentId = AmsMcsTestKit.Dev,
                EntityTag = "etag",
            },
        ],
    };

    private string WriteProject(string? mcsAgentId)
    {
        var document = Document();
        _scratch.Place(Path.Combine("project", ".config", "agent.config.json"), JsonSerializer.Serialize(document with { ManagedAgents = [document.ManagedAgents[0] with { McsAgentId = mcsAgentId }] }, AgentConfigJsonContext.Default.AgentConfigDocument));
        return Path.Combine(_scratch.Path, "project");
    }
}
