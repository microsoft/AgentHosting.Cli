using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class AmsMcsCleanupTests : IDisposable
{
    private const string AgentId = "11111111-0000-4000-8000-000000000001";
    private const string SecondAgentId = "12121212-0000-4000-8000-000000000012";
    private const string RepositoryId = "22222222-0000-4000-8000-000000000002";
    private const string BotId = "66666666-0000-4000-8000-000000000006";
    private const string FlowId = "99999999-0000-4000-8000-000000000009";
    private const string ConnectorId = "cccccccc-1111-4000-8000-00000000000c";
    private const string ReferenceId = "dddddddd-1111-4000-8000-00000000000d";
    private const string GitUrl = $"https://{AmsMcsTestKit.GitHost}/appframework/tenants/{AmsMcsTestKit.Tenant}/environments/{AmsMcsTestKit.Dev}/git/repositories/{RepositoryId}";

    private readonly ScratchDirectory _scratch = new();
    private readonly FakeMsagent _msagent = new();
    private readonly OracleStub _oracle = new();
    private readonly Transcript _transcript;
    private readonly Ledger _ledger;

    public AmsMcsCleanupTests()
    {
        _transcript = new Transcript(_scratch.Folder("run"));
        _ledger = Ledger.Create(_scratch.Folder("run"), "run-1");
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task AnAmsAgentIsDeletedWithAmsOnAfterItsTestAndProdDeployments()
    {
        var project = Project("MCSAgent", false, Deployment("dev-1", DeploymentStages.Dev), Deployment("test-1", DeploymentStages.Test, deployed: true), Deployment("prod-1", DeploymentStages.Prod));
        _ledger.RecordAmsAgent(AgentId.ToUpperInvariant(), "Agent A", project, amsOff: false, "AM-02");

        var orphans = await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Empty(orphans);
        Assert.Equal(ResourceState.Deleted, Assert.Single(_ledger.Entries).State);
        Assert.Equal(
            [
                $"deployment delete --project {project} --deployment-id test-1 --delete-deployed-mcs-agent --json --non-interactive",
                $"deployment delete --project {project} --deployment-id prod-1 --json --non-interactive",
                $"agent delete --project {project} --agent-id {AgentId} --deployment-id dev-1 --json --non-interactive",
            ],
            _msagent.Calls.Select(call => call.Line));
        Assert.All(_msagent.Calls, call => Assert.Equal(project, call.WorkingDirectory));
        Assert.All(_msagent.Calls, call => Assert.Equal("AmsMcsAgents", call.Environment["AH_CLI_FEATURES"]));
        Assert.All(_msagent.Calls, call => Assert.Equal("https://data-plane.example.invalid", call.Environment["MSAGENT_AMS_DATAPLANE_ENDPOINT"]));
    }

    [Fact]
    public async Task AWorkflowsDeployedTestDeploymentIsDeletedWithoutTheAgentOption()
    {
        var project = Project("MCSWorkflow", false, Deployment("test-1", DeploymentStages.Test, deployed: true));
        _ledger.RecordAmsAgent(AgentId, "Flow A", project, amsOff: false, "AM-07");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal([$"deployment delete --project {project} --deployment-id test-1 --json --non-interactive", $"agent delete --project {project} --agent-id {AgentId} --json --non-interactive"], _msagent.Calls.Select(call => call.Line));
    }

    [Fact]
    public async Task AFailedDeleteRemovesTheCloudCacheAndTriesOnceMore()
    {
        var project = Project("MCSAgent", true, Deployment("dev-1", DeploymentStages.Dev));
        _msagent.Answer("agent delete", string.Empty, exitCode: 1).Answer("agent delete", "{}");
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-16");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal(ResourceState.Deleted, Assert.Single(_ledger.Entries).State);
        Assert.Equal(2, _msagent.CallsTo("agent delete").Count);
        Assert.False(Directory.Exists(Path.Combine(project, "Agents", "A", ".mcs")));
        Assert.True(File.Exists(Path.Combine(project, "Agents", "A", "agent.mcs.yml")));
    }

    [Fact]
    public async Task ADeleteThatFailsAgainIsOrphanedAndKeepsItsWorkspace()
    {
        var project = Project("MCSAgent", true, Deployment("dev-1", DeploymentStages.Dev));
        _msagent.Answer("agent delete", string.Empty, exitCode: 1);
        _ledger.RecordLocalWorkspace(project, "AM-02");
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-02");

        var orphans = await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal(2, orphans.Count);
        Assert.Equal("agent delete exited 1: agent delete failed", orphans.Single(entry => entry.Kind == ResourceKind.AmsAgent).Failure);
        Assert.StartsWith("kept, because", orphans.Single(entry => entry.Kind == ResourceKind.LocalWorkspace).Failure, StringComparison.Ordinal);
        Assert.True(Directory.Exists(project));
        Assert.Equal(2, _msagent.CallsTo("agent delete").Count);
    }

    [Fact]
    public async Task ALocalWorkspaceHoldingReadOnlyGitObjectsIsDeleted()
    {
        var workspace = _scratch.Folder("home");
        File.SetAttributes(_scratch.Place(Path.Combine("home", ".git", "objects", "c8", "943f7ac60b8a7ce8433c56ffcebe7a4c626a"), "blob"), FileAttributes.ReadOnly);
        _ledger.RecordLocalWorkspace(workspace, "AM-02");

        Assert.Empty(await NewCleanup().RunAsync(_ledger, CancellationToken.None));
        Assert.Equal(ResourceState.Deleted, Assert.Single(_ledger.Entries).State);
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public async Task AFailedDeleteWithoutACloudCacheIsNotRetried()
    {
        var project = Project("MCSAgent", false, Deployment("dev-1", DeploymentStages.Dev));
        _msagent.Answer("agent delete", string.Empty, exitCode: 1);
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-02");

        Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));
        Assert.Single(_msagent.CallsTo("agent delete"));
    }

    [Fact]
    public async Task AnAgentTheConfigNoLongerHoldsCountsAsDeleted()
    {
        var project = _scratch.Folder("bare-project");
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-16");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal(ResourceState.Deleted, Assert.Single(_ledger.Entries).State);
        Assert.Empty(_msagent.Calls);
    }

    [Fact]
    public async Task AnAgentWhoseProjectIsGoneIsOrphaned()
    {
        var project = Path.Combine(_scratch.Path, "gone");
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-02");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal($"the project directory {project} is gone, so the CLI cannot address the agent", orphan.Failure);
    }

    [Fact]
    public async Task AnAgentMadeWithAmsOffIsDeletedWithAmsOff()
    {
        var project = Project("MCSAgent", false, Deployment("dev-1", DeploymentStages.Dev));
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: true, "AM-01");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        var call = Assert.Single(_msagent.Calls);
        Assert.False(call.Environment.ContainsKey("AH_CLI_FEATURES"));
        Assert.False(call.Environment.ContainsKey("MSAGENT_AMS_DATAPLANE_ENDPOINT"));
        Assert.False(call.Environment.ContainsKey("MSAGENT_GIT_HOST"));
    }

    [Fact]
    public async Task AmsAgentsAreRemovedNewestFirst()
    {
        var project = Project("MCSAgent", false);
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-02");
        _ledger.RecordAmsAgent(SecondAgentId, "Agent B", project, amsOff: false, "AM-03");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal([SecondAgentId, AgentId], _msagent.Calls.Select(call => call.Arguments[5]));
    }

    [Fact]
    public async Task WithoutTheAmsServicesAnAmsEntryIsOrphaned()
    {
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-06");
        var msagent = _msagent.Cli(_transcript, _scratch.Path);
        var azure = new AzureCli(_transcript);

        var orphan = Assert.Single(await new Cleanup(msagent, azure, new CloudProbe(msagent, azure), _transcript).RunAsync(_ledger, CancellationToken.None));

        Assert.Equal("the AMS services aren't available, so cleanup can't reach AMS, GRS or Dataverse", orphan.Failure);
    }

    [Fact]
    public async Task AFlowIsTurnedOffAndDeletedByItsExactName()
    {
        _oracle.Answer(HttpMethod.Get, "test.example.invalid/api/data/v9.2/workflows?", HttpStatusCode.OK, Flow()).Answer(HttpMethod.Get, "test.example.invalid/api/data/v9.2/workflows?", HttpStatusCode.OK, """{"value":[]}""").Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.NoContent).Answer(HttpMethod.Delete, "/workflows(", HttpStatusCode.NoContent);
        var entry = _ledger.RecordAmsFlow("Flow A", AmsMcsTestKit.Test, "AM-15");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal($"Flow A@{AmsMcsTestKit.Test}", entry.Id);
        Assert.Equal(ResourceState.Deleted, entry.State);
        Assert.Equal([HttpMethod.Get, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Get], _oracle.Calls.Select(call => call.Method));
    }

    [Fact]
    public async Task AFlowThatStaysIsOrphaned()
    {
        _oracle.Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flow()).Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.NoContent).Answer(HttpMethod.Delete, "/workflows(", HttpStatusCode.NoContent);
        _ledger.RecordAmsFlow("Flow A", AmsMcsTestKit.Test, "AM-15");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal($"1 flow(s) named exactly 'Flow A' are still in {AmsMcsTestKit.Test}", orphan.Failure);
    }

    [Fact]
    public async Task ALeftoverBotIsDeletedInEachOrganizationAfterItsSchemaNameIsChecked()
    {
        _oracle.Answer(HttpMethod.Get, "dev.example.invalid/api/data/v9.2/bots?", HttpStatusCode.OK, Bots("ahe2e_left"))
            .Answer(HttpMethod.Get, "test.example.invalid/api/data/v9.2/bots?", HttpStatusCode.OK, """{"value":[]}""")
            .Answer(HttpMethod.Get, "prod.example.invalid/api/data/v9.2/bots?", HttpStatusCode.OK, Bots("ahe2e_left"))
            .Answer(HttpMethod.Get, $"/bots({BotId})", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"ahe2e_left"}""")
            .Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, """{"value":[]}""")
            .Answer(HttpMethod.Delete, "/bots(", HttpStatusCode.NoContent);
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-06");

        await NewCleanup().RunAsync(_ledger, CancellationToken.None);

        Assert.Equal(ResourceState.Deleted, Assert.Single(_ledger.Entries).State);
        Assert.Equal(["dev.example.invalid", "prod.example.invalid"], _oracle.CallsTo(HttpMethod.Delete, "/bots(").Select(call => call.Uri.Host));
    }

    [Fact]
    public async Task AConnectionReferenceIsDeletedAfterTheBotsAndBeforeItsConnectorWhateverTheOrderTheyWereRecordedIn()
    {
        _oracle.Answer(HttpMethod.Get, "/bots?", HttpStatusCode.OK, """{"value":[]}""")
            .Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, $$"""{"value":[{"connectionreferenceid":"{{ReferenceId}}","connectionreferencelogicalname":"ahe2e_left.ref"}]}""").Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, """{"value":[]}""").Answer(HttpMethod.Delete, "/connectionreferences(", HttpStatusCode.NoContent)
            .Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, $$"""{"value":[{"connectorid":"{{ConnectorId}}","name":"cr150_5Fx"}]}""").Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, """{"value":[]}""").Answer(HttpMethod.Delete, "/connectors(", HttpStatusCode.NoContent);
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-33");
        _ledger.RecordAmsConnectionReference("ahe2e_left.ref", AmsMcsTestKit.Dev, "AM-33");
        _ledger.RecordAmsConnector("cr150_5Fx", AmsMcsTestKit.Dev, "AM-33");

        Assert.Empty(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.All(_ledger.Entries, entry => Assert.Equal(ResourceState.Deleted, entry.State));
        Assert.Equal(["bots", "connectionreferences", "connectors"], _oracle.Calls.Select(call => call.Uri.AbsolutePath.Split('/')[^1].Split('(')[0]).Distinct());
        Assert.Equal([$"connectionreferences({ReferenceId})", $"connectors({ConnectorId})"], _oracle.Calls.Where(call => call.Method == HttpMethod.Delete).Select(call => call.Uri.AbsolutePath.Split('/')[^1]));
        Assert.All(_oracle.Calls.Where(call => call.Uri.AbsolutePath.Contains("/connector", StringComparison.Ordinal)), call => Assert.Equal("dev.example.invalid", call.Uri.Host));
    }

    [Fact]
    public async Task AConnectorThatStaysIsOrphaned()
    {
        _oracle.Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, $$"""{"value":[{"connectorid":"{{ConnectorId}}","name":"cr150_5Fx"}]}""").Answer(HttpMethod.Delete, "/connectors(", HttpStatusCode.NoContent);
        _ledger.RecordAmsConnector("cr150_5Fx", AmsMcsTestKit.Dev, "AM-33");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal($"1 connector(s) named exactly 'cr150_5Fx' are still in {AmsMcsTestKit.Dev}", orphan.Failure);
    }

    [Fact]
    public void TheConnectorKindsAreRecordedByNameInTheirEnvironmentAndSurviveAReopen()
    {
        var connector = _ledger.RecordAmsConnector("cr150_5Fx", AmsMcsTestKit.Dev, "AM-33");
        var reference = _ledger.RecordAmsConnectionReference("ahe2e_left.ref", AmsMcsTestKit.Dev, "AM-33");

        var entries = Ledger.Open(Path.Combine(_scratch.Path, "run"), "run-1").Entries;

        Assert.Equal(($"cr150_5Fx@{AmsMcsTestKit.Dev}", $"ahe2e_left.ref@{AmsMcsTestKit.Dev}"), (connector.Id, reference.Id));
        Assert.Equal([(ResourceKind.AmsConnector, "cr150_5Fx", AmsMcsTestKit.Dev, "deleted through Dataverse by its exact name, after its connection references"), (ResourceKind.AmsConnectionReference, "ahe2e_left.ref", AmsMcsTestKit.Dev, "deleted through Dataverse by its exact logical name")], entries.Select(entry => (entry.Kind, entry.Cleanup.Correlation, entry.Cleanup.EnvironmentId, entry.Cleanup.Via)));
    }

    [Fact]
    public async Task ALeftoverBotWithAnotherSchemaNameIsOrphaned()
    {
        _oracle.Answer(HttpMethod.Get, "/bots?", HttpStatusCode.OK, Bots("ahe2e_left")).Answer(HttpMethod.Get, $"/bots({BotId})", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"cr123_real"}""");
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-06");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal($"Refused to delete bot {BotId}: its schema name is cr123_real, not ahe2e_left.", orphan.Failure);
        Assert.Empty(_oracle.CallsTo(HttpMethod.Delete, "/"));
    }

    [Fact]
    public async Task ARepositoryNoAgentHoldsIsSoftDeletedAfterTheAgentDeletes()
    {
        var project = Project("MCSAgent", false, Deployment("dev-1", DeploymentStages.Dev));
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList()).Answer("auth git-credential get", "username=msagent\npassword=grs-token\n");
        _oracle.Answer(HttpMethod.Delete, $"/git/repositories/{RepositoryId}", HttpStatusCode.NoContent);
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-02");
        var repository = _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        Assert.Empty(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        var credential = Assert.Single(_msagent.CallsTo("auth git-credential get"));
        Assert.Equal(ResourceState.Deleted, repository.State);
        Assert.Equal(["agent delete", "agent list", "auth git-credential"], _msagent.Calls.Select(call => string.Join(' ', call.Arguments.Take(2))));
        Assert.Equal(["agent", "list", "--view", "ams", "--environment-id", AmsMcsTestKit.Dev, "--repository-id", RepositoryId, "--limit", "1000", "--json", "--non-interactive"], _msagent.CallsTo("agent list")[0].Arguments);
        Assert.Equal($"protocol=https\nhost={AmsMcsTestKit.GitHost}\n", credential.Stdin);
        Assert.Equal("AmsMcsAgents", credential.Environment["AH_CLI_FEATURES"]);
        Assert.Equal(AmsMcsTestKit.GitHost, credential.Environment["MSAGENT_GIT_HOST"]);
        Assert.Equal("Bearer grs-token", Assert.Single(_oracle.Calls).Authorization!.ToString());
        Assert.Equal($"DELETE {GitUrl}", _oracle.Calls[0].Target);
    }

    [Fact]
    public async Task ARepositoryThatStillHoldsAnAgentIsRetained()
    {
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList(AgentId));
        var repository = _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        Assert.Empty(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal(ResourceState.Retained, repository.State);
        Assert.Equal($"AMS still lists Agent 1111 ({AgentId}) in it, so it's kept for them", repository.Failure);
        Assert.Empty(_msagent.CallsTo("auth"));
        Assert.Empty(_oracle.Calls);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(400, true)]
    public async Task ARepositoryGrsRefusesIsOrphanedWithItsManualCommand(int length, bool truncated)
    {
        var body = $"{{\"password\":\"leaked\",\"message\":\"{new string('x', length)}\"}}";
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList()).Answer("auth git-credential get", "password=grs-token\n");
        _oracle.Answer(HttpMethod.Delete, "/git/repositories/", HttpStatusCode.Forbidden, body);
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        var expected = Redaction.Apply(body);
        Assert.Equal($"GRS refused the delete: 403 {(truncated ? expected[..200] + "…" : expected)}", orphan.Failure);
        Assert.DoesNotContain("leaked", orphan.Failure, StringComparison.Ordinal);
        Assert.Equal($"Invoke-WebRequest -Method Delete -Uri '{GitUrl}' -Headers @{{ Authorization = 'Bearer <token>' }}, with <token> the password= line of msagent auth git-credential get, once agent list --view ams --repository-id {RepositoryId} lists no agent", Cleanup.ManualCommand(orphan));
    }

    [Fact]
    public async Task AnUnreadableAgentListingLeavesTheRepositoryOrphaned()
    {
        _msagent.Answer("agent list", string.Empty, exitCode: 1);
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.StartsWith("agent list --view ams exited 1, so whether AMS still lists an agent in the repository is unknown: ", orphan.Failure, StringComparison.Ordinal);
        Assert.Empty(_msagent.CallsTo("auth"));
    }

    [Theory]
    [InlineData("username=msagent\n")]
    [InlineData("username=msagent\npassword=\n")]
    public async Task NoTokenLeavesTheRepositoryOrphaned(string credential)
    {
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList()).Answer("auth git-credential get", credential);
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        var orphan = Assert.Single(await NewCleanup().RunAsync(_ledger, CancellationToken.None));

        Assert.Equal("msagent auth git-credential get gave no token", orphan.Failure);
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task ALedgerEntryWithoutAGitUrlIsOrphaned()
    {
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList());
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");
        var path = Path.Combine(_scratch.Path, "run", "ledger.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"{GitUrl}\"", "null", StringComparison.Ordinal));

        var orphan = Assert.Single(await NewCleanup().RunAsync(Ledger.Open(Path.Combine(_scratch.Path, "run"), "run-1"), CancellationToken.None));

        Assert.Equal("the ledger entry has no git URL", orphan.Failure);
    }

    [Fact]
    public async Task TheGitCredentialTokenNeverReachesTheTranscript()
    {
        var script = AmsMcsTestKit.MsagentScript(_scratch.Folder("bin"), "grs-secret-token", AmsMcsTestKit.AgentList());
        _oracle.Answer(HttpMethod.Delete, "/git/repositories/", HttpStatusCode.NoContent);
        var repository = _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");

        await NewCleanup(Services(new MsagentCli(script, _transcript, _scratch.Folder("dumps")))).RunAsync(_ledger, CancellationToken.None);

        var transcript = File.ReadAllText(Path.Combine(_scratch.Path, "run", "transcript.log"));
        Assert.Equal(ResourceState.Deleted, repository.State);
        Assert.Equal("Bearer grs-secret-token", Assert.Single(_oracle.Calls).Authorization!.ToString());
        Assert.Contains("auth git-credential get", transcript, StringComparison.Ordinal);
        Assert.Contains("password=***", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("grs-secret-token", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewKindsAreRecordedWithTheirCleanupAndSurviveAReopen()
    {
        _ledger.RecordAmsAgent(AgentId, "Agent A", @"C:\ws\project", amsOff: true, "AM-01");
        _ledger.RecordAmsFlow("Flow A", AmsMcsTestKit.Test, "AM-15");
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-06");
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");
        _ledger.MarkRetained(RepositoryId, "kept for its agents");

        var entries = Ledger.Open(Path.Combine(_scratch.Path, "run"), "run-1").Entries;

        Assert.Equal([ResourceKind.AmsAgent, ResourceKind.AmsFlow, ResourceKind.LeftoverBot, ResourceKind.GrsRepository], entries.Select(entry => entry.Kind));
        Assert.True(entries[0].Cleanup.AmsOff);
        Assert.Equal(@"C:\ws\project", entries[0].Cleanup.ProjectDirectory);
        Assert.Equal(("Flow A", AmsMcsTestKit.Test), (entries[1].Cleanup.Correlation, entries[1].Cleanup.EnvironmentId));
        Assert.Equal("ahe2e_left", entries[2].Cleanup.SchemaName);
        Assert.Equal((GitUrl, AmsMcsTestKit.Dev), (entries[3].Cleanup.GitUrl, entries[3].Cleanup.EnvironmentId));
        Assert.Equal((ResourceState.Retained, "kept for its agents"), (entries[3].State, entries[3].Failure));
        Assert.Equal(3, Ledger.Open(Path.Combine(_scratch.Path, "run"), "run-1").Outstanding.Count);
    }

    [Fact]
    public void AnAgentRecordedWithAmsOffIsCleanedWithAmsOnOnceAScenarioRecordsItWithAmsOn()
    {
        var first = _ledger.RecordAmsAgent(AgentId, "Agent A", @"C:\ws\first", amsOff: true, "AM-01");
        var again = _ledger.RecordAmsAgent(AgentId.ToUpperInvariant(), "Agent A", @"C:\ws\first", amsOff: true, "AM-01");
        var on = _ledger.RecordAmsAgent(AgentId, "Agent A", @"C:\ws\second", amsOff: false, "AM-02");
        var later = _ledger.RecordAmsAgent(AgentId, "Agent A", @"C:\ws\first", amsOff: true, "AM-01");

        Assert.Same(first, again);
        Assert.NotSame(first, on);
        Assert.Same(on, later);
        Assert.Equal((false, @"C:\ws\first", first.Sequence, "AM-01"), (on.Cleanup.AmsOff, on.Cleanup.ProjectDirectory, on.Sequence, on.CreatedBy));
        Assert.Same(on, Assert.Single(_ledger.Entries));
        Assert.False(Assert.Single(Ledger.Open(Path.Combine(_scratch.Path, "run"), "run-1").Entries).Cleanup.AmsOff);
    }

    [Fact]
    public void EachNewKindGetsItsManualCommand()
    {
        var on = _ledger.RecordAmsAgent(AgentId, "Agent A", @"C:\ws\project", amsOff: false, "AM-02");
        var off = _ledger.RecordAmsAgent(SecondAgentId, "Agent B", @"C:\ws\project", amsOff: true, "AM-01");

        Assert.Equal($"with AH_CLI_FEATURES=AmsMcsAgents and the amsMcs variables set, msagent deployment delete --project \"C:\\ws\\project\" --deployment-id <each test and prod deployment> [--delete-deployed-mcs-agent], then msagent agent delete --project \"C:\\ws\\project\" --agent-id {AgentId} --deployment-id <its dev deployment>", Cleanup.ManualCommand(on));
        Assert.StartsWith("msagent deployment delete --project ", Cleanup.ManualCommand(off), StringComparison.Ordinal);
        Assert.Equal($"turn off, then delete, the flow named exactly 'Flow A' in environment {AmsMcsTestKit.Test}", Cleanup.ManualCommand(_ledger.RecordAmsFlow("Flow A", AmsMcsTestKit.Test, "AM-15")));
        Assert.Equal("delete the bot whose schema name is ahe2e_left, with its bot components, from the Dev, Test and Prod organizations", Cleanup.ManualCommand(_ledger.RecordLeftoverBot("ahe2e_left", "AM-06")));
        Assert.Equal($"delete the custom connector named exactly 'cr150_5Fx' from environment {AmsMcsTestKit.Dev}, after its connection references", Cleanup.ManualCommand(_ledger.RecordAmsConnector("cr150_5Fx", AmsMcsTestKit.Dev, "AM-33")));
        Assert.Equal($"delete the connection reference named exactly 'ahe2e_left.ref' from environment {AmsMcsTestKit.Dev}", Cleanup.ManualCommand(_ledger.RecordAmsConnectionReference("ahe2e_left.ref", AmsMcsTestKit.Dev, "AM-33")));
        Assert.Equal(
            [
                "with AH_CLI_FEATURES=AmsMcsAgents and the amsMcs variables set, msagent deployment delete --project \"<project>\" --deployment-id <each test and prod deployment> [--delete-deployed-mcs-agent], then msagent agent delete --project \"<project>\" --agent-id <agent id> --deployment-id <its dev deployment>",
                "turn off, then delete, the flow named exactly '<flow name>' in environment <environment id>",
                "delete the bot whose schema name is <schema name>, with its bot components, from the Dev, Test and Prod organizations",
                "Invoke-WebRequest -Method Delete -Uri '<git url>' -Headers @{ Authorization = 'Bearer <token>' }, with <token> the password= line of msagent auth git-credential get, once agent list --view ams --repository-id <repository id> lists no agent",
                "delete the custom connector named exactly '<connector name>' from environment <environment id>, after its connection references",
                "delete the connection reference named exactly '<logical name>' from environment <environment id>",
            ],
            AmsMcsServices.ManualCleanup.Select(Cleanup.ManualCommand));
    }

    [Fact]
    public void ARetainedEntryIsReportedAsKeptWithItsManualCommandAndIsNoOrphan()
    {
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");
        _ledger.MarkRetained(RepositoryId, "kept for its agents");

        var output = AmsMcsTestKit.Capture(() => new Reporter(useColour: false).Summary(Report(), _ledger.Entries));

        Assert.Contains($"  Kept on purpose{Environment.NewLine}    GrsRepository  {RepositoryId}   kept for its agents{Environment.NewLine}    Invoke-WebRequest -Method Delete -Uri '{GitUrl}'", output, StringComparison.Ordinal);
        Assert.Contains("Nothing left behind.", output, StringComparison.Ordinal);
        Assert.Equal(0, Report().ExitCode(_ledger.Entries.Any(entry => entry.State == ResourceState.Orphaned)));
    }

    [Fact]
    public void AnOrphanIsStillReportedBesideARetainedEntry()
    {
        _ledger.RecordGrsRepository(RepositoryId, GitUrl, AmsMcsTestKit.Dev, "AM-02");
        _ledger.MarkRetained(RepositoryId, "kept for its agents");
        _ledger.RecordLeftoverBot("ahe2e_left", "AM-06");
        _ledger.MarkOrphaned("ahe2e_left", "Dataverse refused the bot delete: 409");

        var output = AmsMcsTestKit.Capture(() => new Reporter(useColour: false).Summary(Report(), _ledger.Entries));

        Assert.Contains("Kept on purpose", output, StringComparison.Ordinal);
        Assert.Contains($"1 orphan{Environment.NewLine}    LeftoverBot    ahe2e_left   Dataverse refused the bot delete: 409{Environment.NewLine}    delete the bot whose schema name is ahe2e_left", output, StringComparison.Ordinal);
    }

    private RunReport Report() => RunReport.From("run-1", DateTimeOffset.UtcNow, TimeSpan.Zero, new MsagentBinary("msagent", "test", "test"), [], _ledger.Entries);

    private static string Flow() => $$"""{"value":[{"workflowid":"{{FlowId}}","name":"Flow A","statecode":1,"statuscode":2}]}""";

    private static string Bots(string schemaName) => $$"""{"value":[{"botid":"{{BotId}}","schemaname":"{{schemaName}}"}]}""";

    private static AgentDeploymentRecord Deployment(string id, string stage, bool deployed = false) => new()
    {
        DeploymentId = id,
        AgentId = AgentId,
        AgentType = "MCSAgent",
        DeploymentType = stage,
        DeploymentName = id,
        TenantId = AmsMcsTestKit.Tenant,
        LastDeployedTime = deployed ? DateTimeOffset.UnixEpoch : null,
        EntityTag = "etag",
    };

    private string Project(string agentType, bool withCache, params AgentDeploymentRecord[] deployments)
    {
        var project = _scratch.Folder("project");
        var document = new AgentConfigDocument
        {
            ManagedAgents = [Agent(AgentId, agentType, "Agents/A"), Agent(SecondAgentId, agentType, "Agents/B")],
            AgentDeployments = deployments,
        };

        _scratch.Place(Path.Combine("project", ".config", "agent.config.json"), JsonSerializer.Serialize(document, AgentConfigJsonContext.Default.AgentConfigDocument));
        _scratch.Place(Path.Combine("project", "Agents", "A", "agent.mcs.yml"), "kind: AgentDialog");

        if (withCache)
        {
            _scratch.Place(Path.Combine("project", "Agents", "A", ".mcs", "conn.json"), "{}");
        }

        return project;
    }

    private static ManagedAgentRecord Agent(string agentId, string agentType, string location) => new()
    {
        TenantId = AmsMcsTestKit.Tenant,
        EnvironmentId = AmsMcsTestKit.Dev,
        AgentId = agentId,
        DisplayName = location,
        AgentType = agentType,
        AgentLocation = location,
        AgentState = "Active",
        EntityTag = "etag",
    };

    private AmsMcsServices Services(MsagentCli? msagent = null) => new(AmsMcsTestKit.Config(), "run-1", msagent ?? _msagent.Cli(_transcript, _scratch.Path), _transcript, new FakeTokens(), new HttpMessageInvoker(_oracle));

    private Cleanup NewCleanup(AmsMcsServices? services = null)
    {
        var msagent = _msagent.Cli(_transcript, _scratch.Path);
        var azure = new AzureCli(_transcript);
        return new Cleanup(msagent, azure, new CloudProbe(msagent, azure), _transcript, amsMcs: services ?? Services());
    }
}
