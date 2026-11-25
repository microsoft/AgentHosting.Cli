using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Ams;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsScenarioTests : IDisposable
{
    private const string Tenant = "5cae182f-63ff-49f9-9f35-3acc2724e738";
    private const string Home = "245ed99c-a1cc-eae7-9ab5-367e3d921f1d";
    private const string Target = "39b8de47-a594-e9f6-bb18-bac57b8ad289";
    private const string AgentId = "E27DCDF7-ECFA-4732-B5AA-B38BB5A3C66C";
    private const string DeploymentId = "746FC116-57D8-4B5F-9BA7-A19D744F884D";
    private const string EntraId = "8A5206D8-2905-44B8-99D8-886B81182882";
    private const string Name = "ahe2e-test-ac99";
    private const string Endpoint = "https://ahe2e-test-ac99.usw2.devtunnels.ms/api/messages";
    private const string Commit = "249bc478d0d641df9d6683b38c5c4a26d5f315a5";

    private static readonly AmsAgent Agent = new(Name, AgentId);

    private static readonly string[] UsageErrorScenarios = ["AC-06", "AC-14"];

    private static readonly (string StandardOutput, int ExitCode) NameIsFree = (Failure(AmsOutput.AgentNotFound, "No code agent named it is in the environment.", 2), 2);

    private static readonly (string StandardOutput, int ExitCode) GateClosed = (Failure(AmsOutput.InvalidFieldValue, "AMS refused a value of the request: The field 'RequestedEndpoint' is invalid.", 2), 2);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "msagent-ams-scenario-" + Guid.NewGuid().ToString("N"));
    private readonly Ledger _ledger;

    public AmsScenarioTests() => _ledger = Ledger.Create(_directory, "test-run");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheUsageErrorScenariosRunWithoutTheConsentToCreate() => Assert.All(AmsCodeAgentScenarios.All(), scenario =>
    {
        var usageError = UsageErrorScenarios.Contains(scenario.Id);

        Assert.Equal(usageError ? SafetyTier.ReadOnly : SafetyTier.Creating, scenario.Tier);
        Assert.Equal(usageError, Assert.Single(scenario.Requires, Capabilities.AmsChain.Contains) == Capability.Ams);
    });

    [Fact]
    public void TheScenariosThatLeaveHostedCopiesRunOnlyInTheHostedPack()
    {
        var scenarios = AmsCodeAgentScenarios.All().ToArray();
        var core = scenarios.Where(scenario => scenario.Pack is null).Select(scenario => scenario.Id);

        Assert.All(scenarios, scenario => Assert.Equal(scenario.Requires.Contains(Capability.AmsHosted) || scenario.Requires.Contains(Capability.AmsProd), scenario.Pack == AmsSteps.HostedPack));
        Assert.Equal(core, ScenarioCatalogue.Select(new RunFilters { Features = [AmsSteps.Feature] }).Select(scenario => scenario.Id));
        Assert.Equal(scenarios.Select(scenario => scenario.Id), ScenarioCatalogue.Select(new RunFilters { Packs = ["core", AmsSteps.HostedPack], Features = [AmsSteps.Feature] }).Select(scenario => scenario.Id));
    }

    [Theory]
    [InlineData("CA-19")]
    [InlineData("CA-20")]
    public void TheCodeAgentScenariosOnTheServiceNeedTheConsentToCreateThere(string scenarioId) =>
        Assert.Contains(Capability.AmsCreate, Assert.Single(ScenarioCatalogue.All(), scenario => scenario.Id == scenarioId).Requires);

    [Fact]
    public async Task ADeployThatMustSucceedFailsTheScenarioNamingItsStep()
    {
        var (context, _) = Scripted((Failure("prod-needs-test", "AMS deploys the newest upload to Prod only after a Test deployment of the agent runs it.", 1), 1));

        var failure = await Assert.ThrowsAsync<AssertionException>(() => AmsSteps.DeployOrFailAsync(context, Agent, Record("Prod", "package"), "to Prod"));

        Assert.Equal("agent deploy to Prod", failure.Check);
    }

    [Fact]
    public void RunUniqueNamesAreLowerCaseAndDevEndpointsAreHttpsDevTunnels()
    {
        var (context, _) = Scripted(("{}", 0));

        Assert.Equal(Name, AmsSteps.Name(context, "AC99"));
        Assert.Equal(Endpoint, AmsSteps.DevEndpoint(context, "AC99"));
    }

    [Fact]
    public async Task AnAgentIsCreatedUnderAFreeRunUniqueNameAndResolvedInTheLedger()
    {
        var (context, fake) = Scripted(NameIsFree, (CreatedAgent(Name), 0));

        var agent = await AmsSteps.CreateAgentAsync(context, "ac99");

        Assert.Equal(Agent, agent);
        Assert.StartsWith($"agent show --agent-name {Name} --environment-id {Home} --json --non-interactive", fake.Invocations[0], StringComparison.Ordinal);
        Assert.StartsWith($"agent create --agent-type CodeAgent --agent-name {Name} --language dotnet --language-version 10 --idempotency-key ", fake.Invocations[1], StringComparison.Ordinal);
        var entry = Assert.Single(_ledger.Outstanding);
        Assert.Equal((ResourceKind.AmsAgent, AgentId, true, Name, Home), (entry.Kind, entry.Id, entry.Resolved, entry.Cleanup.Correlation, entry.Cleanup.Ams?.HomeEnvironmentId));
    }

    [Fact]
    public async Task AnAgentWhoseCreateFailsStaysRecordedByItsNameForCleanup()
    {
        var (context, fake) = Scripted(NameIsFree, (Failure("agent-create-idempotency-conflict", "AMS rejected an Idempotency-Key that was already used.", 2), 2));

        await Assert.ThrowsAsync<AssertionException>(() => AmsSteps.CreateAgentAsync(context, "ac99"));

        Assert.Equal(2, fake.Invocations.Count);
        var entry = Assert.Single(_ledger.Outstanding);
        Assert.Equal((ResourceKind.AmsAgent, Name, false), (entry.Kind, entry.Id, entry.Resolved));
    }

    [Theory]
    [InlineData("agent-create-timeout", "The create outcome may be indeterminate.")]
    [InlineData("agent-create-transport-failed", "The create outcome may be indeterminate.")]
    [InlineData("agent-create-response-unreadable", "The create outcome may be indeterminate.")]
    [InlineData("agent-create-in-progress", "AMS is already processing this idempotent create request.")]
    [InlineData("agent-create-concurrency-conflict", "A concurrent durable write won the race.")]
    [InlineData("request-failed", "Agent Management Service Data Plane request failed (POST https://amsdp.example.test/agents) with HTTP 503.")]
    public async Task ACreateThatMayHaveLandedIsReplayedWithTheSameKey(string errorKind, string message)
    {
        var (context, fake) = Scripted(NameIsFree, (Failure(errorKind, message, 1), 1), (CreatedAgent(Name), 0));

        Assert.Equal(Agent, await AmsSteps.CreateAgentAsync(context, "ac99"));
        Assert.Equal(3, fake.Invocations.Count);
        Assert.Equal(fake.Invocations[1], fake.Invocations[2]);
    }

    [Fact]
    public async Task ANameAnotherAgentHoldsBlocksTheScenarioBeforeAnythingIsCreated()
    {
        var (context, fake) = Scripted((Json(new { success = true, agent = new { agentId = AgentId, displayName = Name } }), 0));

        await Assert.ThrowsAsync<ScenarioBlockedException>(() => AmsSteps.CreateAgentAsync(context, "ac99"));

        Assert.Single(fake.Invocations);
        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public void ACreateExpectedToBeRefusedIsRecordedOnlyWhenItMadeOrMayHaveMadeAnAgent()
    {
        var (context, _) = Scripted(("{}", 0));

        AmsSteps.RecordUnexpectedAgent(context, Result(CreatedAgent(Name), 0), Name);
        AmsSteps.RecordUnexpectedAgent(context, Result(Failure("agent-create-transport-failed", "The create outcome may be indeterminate.", 1), 1), "ahe2e-test-maybe");
        AmsSteps.RecordUnexpectedAgent(context, Result(Failure("agent-name-in-use", "AMS refused the agent name.", 2), 2), "ahe2e-test-refused");

        Assert.Equal(new[] { (AgentId, true), ("ahe2e-test-maybe", false) }, _ledger.Outstanding.Select(entry => (entry.Id, entry.Resolved)));
    }

    [Theory]
    [InlineData("request-failed", "Agent Hosting Deployment deploy request failed (POST https://ams.example.test/deploy) with HTTP 500. Error code: InternalServerError.", true)]
    [InlineData("request-failed", "Agent Hosting Deployment read request failed (GET https://ams.example.test/read) with HTTP 409.", false)]
    [InlineData("deploy-request-timeout", "The outcome may be indeterminate.", true)]
    [InlineData("deploy-transport-failed", "The outcome may be indeterminate.", true)]
    [InlineData("agent-not-found", "AMS has no such agent in the home environment.", false)]
    public void OnlyAServerErrorOrAnOutcomeMsagentCouldNotLearnIsTriedAgain(string errorKind, string message, bool transient) =>
        Assert.Equal(transient, AmsSteps.IsTransient(Result(Failure(errorKind, message, 1), 1)));

    [Theory]
    [InlineData("HttpRequestException: No such host is known. (api.test.powerplatform.com:443) -> SocketException: No such host is known.", true)]
    [InlineData("The request was refused.", false)]
    public void AFailureWithNoKindIsTriedAgainOnlyWhenTheRequestNeverReachedTheService(string message, bool transient) =>
        Assert.Equal(transient, AmsSteps.IsTransient(Result(Json(new { success = false, exitCode = 1, errorCode = 99001, errorMessage = message }), 1)));

    [Fact]
    public async Task ADeploymentCreateThatLandedIsReadInsteadOfMadeTwice()
    {
        var (context, fake) = Scripted((Failure("deploy-request-timeout", "The outcome may be indeterminate.", 1), 1), (Deployment("Dev", "dev-endpoint"), 0));

        var deployment = await AmsSteps.CreateDeploymentAsync(context, Agent, "ac99", Home, "dev", EntraId, Endpoint);

        Assert.Equal(DeploymentId, deployment.DeploymentId);
        Assert.Contains($"--entra-id {EntraId} --entra-id-type app-reg --endpoint-url {Endpoint}", fake.Invocations[0], StringComparison.Ordinal);
        Assert.StartsWith($"deployment show --agent-id {AgentId} --deployment-name {Name}", fake.Invocations[1], StringComparison.Ordinal);
        Assert.Equal(2, fake.Invocations.Count);
    }

    [Fact]
    public async Task ADeploymentCreateThatDidNotLandIsTriedAgain()
    {
        var (context, fake) = Scripted((Failure("deploy-transport-failed", "The outcome may be indeterminate.", 1), 1), (Failure("deployment-not-found", "Agent has no deployment named it.", 2), 2), (Deployment("Dev", "dev-endpoint"), 0));

        await AmsSteps.CreateDeploymentAsync(context, Agent, "ac99", Home, "dev", EntraId, Endpoint);

        Assert.Equal(3, fake.Invocations.Count);
        Assert.Equal(fake.Invocations[0], fake.Invocations[2]);
    }

    [Fact]
    public async Task ADeploymentCreateThatLandedWhileItsRetryWasRefusedIsTheOneThatLanded()
    {
        var (context, fake) = Scripted((Failure("deploy-request-timeout", "The outcome may be indeterminate.", 1), 1), (Failure("deployment-not-found", "Agent has no deployment named it.", 2), 2), (Failure("deployment-name-in-use", $"AMS refused the deployment name: it is already used by deployment '{DeploymentId}'.", 2), 2), (Deployment("Dev", "dev-endpoint"), 0));

        var deployment = await AmsSteps.CreateDeploymentAsync(context, Agent, "ac99", Home, "dev", EntraId, Endpoint);

        Assert.Equal(DeploymentId, deployment.DeploymentId);
        Assert.Equal(4, fake.Invocations.Count);
        Assert.StartsWith($"deployment show --agent-id {AgentId} --deployment-name {Name}", fake.Invocations[3], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Test", "package", false, null, true)]
    [InlineData("Dev", "dev-endpoint", true, Commit, true)]
    [InlineData("Dev", "package", false, null, true)]
    [InlineData("Dev", "package", true, null, false)]
    [InlineData("Dev", "dev-endpoint", true, null, false)]
    public async Task ADeployThatRunsAPackageRecordsItsHostedCopyBeforeItsOutcomeIsKnown(string deploymentType, string kind, bool endpointRequested, string? commitId, bool recorded)
    {
        var (context, _) = Scripted((Failure("deploy-request-timeout", "The outcome may be indeterminate.", 1), 1));

        await AmsSteps.DeployAsync(context, Agent, Record(deploymentType, kind) with { RequestedEndpoint = endpointRequested ? Endpoint : null }, commitId: commitId);

        Assert.Equal(recorded, _ledger.Entries.Any(entry => entry.Kind == ResourceKind.AmsRuntimeCopy && entry.State == ResourceState.Retained));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("deploy")]
    public async Task ARefusedDevTunnelEndpointBlocksTheScenarioBecauseOnlyAClosedGateRefusesOne(string step)
    {
        var (context, _) = Scripted(GateClosed);

        var blocked = await Assert.ThrowsAsync<ScenarioBlockedException>(() => step switch
        {
            "create" => (Task)AmsSteps.CreateDeploymentAsync(context, Agent, "ac99", Home, "dev", EntraId, Endpoint),
            "update" => AmsSteps.UpdateEndpointAsync(context, Agent, Record("Dev", "dev-endpoint"), Endpoint),
            _ => AmsSteps.DeployAsync(context, Agent, Record("Dev", "dev-endpoint")),
        });

        Assert.Contains("EnableCodeAgentCustomDevEndpoint", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedFieldOfARecordWithoutAnEndpointFailsTheScenario()
    {
        var (context, fake) = Scripted(GateClosed);

        await Assert.ThrowsAsync<AssertionException>(() => AmsSteps.CreateDeploymentAsync(context, Agent, "ac99", Target, "test", EntraId, null));

        Assert.DoesNotContain("--endpoint-url", Assert.Single(fake.Invocations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedPackageDeployIsLeftForTheScenarioToJudge()
    {
        var (context, _) = Scripted(GateClosed);

        var refused = await AmsSteps.DeployAsync(context, Agent, Record("Dev", "dev-endpoint"), commitId: Commit);

        Assert.True(AmsOutput.IsRefused(refused, AmsOutput.InvalidFieldValue));
    }

    [Fact]
    public async Task ADeployHitByAServerErrorIsTriedOnceMoreAndAPackageDeployRecordsItsHostedCopy()
    {
        var (context, fake) = Scripted((Failure("request-failed", "Agent Hosting Deployment deploy request failed (POST https://ams.example.test/deploy) with HTTP 500.", 1), 1), (Deployment("Test", "package", "ams-bot"), 0));

        var deployed = await AmsSteps.DeployAsync(context, Agent, Record("Test", "package"), commitId: Commit);

        Assert.Equal("ams-bot", AmsSteps.Deployment(deployed).BotName);
        Assert.Equal(2, fake.Invocations.Count);
        Assert.Equal(fake.Invocations[0], fake.Invocations[1]);
        Assert.Contains($"--deployment-id {DeploymentId} --commit-id {Commit}", fake.Invocations[1], StringComparison.Ordinal);
        var copy = Assert.Single(_ledger.Entries);
        Assert.Equal((ResourceKind.AmsRuntimeCopy, $"{AgentId}@{Target}", ResourceState.Retained), (copy.Kind, copy.Id, copy.State));
    }

    [Fact]
    public async Task ADevEndpointDeployByNameLeavesNoHostedCopy()
    {
        var (context, fake) = Scripted((Deployment("Dev", "dev-endpoint", "ams-bot"), 0));
        var record = Record("Dev", "dev-endpoint");

        await AmsSteps.DeployAsync(context, Agent, record, AmsSteps.ByName(Agent, record));

        Assert.StartsWith($"agent deploy --agent-name {Name} --deployment-name {Name} --environment-id {Home}", Assert.Single(fake.Invocations), StringComparison.Ordinal);
        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public async Task EachUploadIsACopyOfThePackageWithAnEntryNamingTheRunAgentAndKey()
    {
        var (context, fake) = Scripted((Json(new { success = true, commitId = Commit }), 0));
        var package = AmsSteps.Services(context).Config.PackagePath!;

        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Program.cs");
        }

        var commit = await AmsSteps.UploadAsync(context, Agent, "1");

        var uploaded = Path.Combine(context.Workspaces.EmptyDirectory("AC-99", "ams"), "upload-1.zip");
        using var copy = ZipFile.OpenRead(uploaded);
        using var reader = new StreamReader(copy.GetEntry("ahe2e-upload.txt")!.Open());
        using var original = ZipFile.OpenRead(package);
        Assert.Equal(Commit, commit);
        Assert.Equal($"msagent e2e run test, agent {AgentId}, upload 1", await reader.ReadToEndAsync());
        Assert.NotNull(copy.GetEntry("Program.cs"));
        Assert.Null(original.GetEntry("ahe2e-upload.txt"));
        Assert.Contains($"--package {uploaded}", Assert.Single(fake.Invocations), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePooledRecordHasItsOwnAgentCreatedOnceAndBothAreShared()
    {
        var (context, fake) = Scripted(NameIsFree, (CreatedAgent("ahe2e-test-records"), 0), (Deployment("Dev", "dev-endpoint"), 0));

        var record = await AmsSteps.PooledRecordAsync(context).WaitAsync(TimeSpan.FromSeconds(30));
        var agent = await AmsSteps.RecordAgentAsync(context).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(record, await AmsSteps.PooledRecordAsync(context));
        Assert.Equal(new AmsAgent("ahe2e-test-records", AgentId), agent);
        Assert.Equal(agent, record.Agent);
        Assert.Equal(3, fake.Invocations.Count);
    }

    [Fact]
    public async Task ADeployOutcomeSaysWhetherItNeededASecondAttempt()
    {
        var (context, _) = Scripted((Failure("request-failed", "Agent Hosting Deployment deploy request failed (POST https://ams.example.test/deploy) with HTTP 500.", 1), 1), (Deployment("Test", "package", "ams-bot"), 0));

        var retried = await AmsSteps.DeployOrFailAsync(context, Agent, Record("Test", "package"), "after a server error");
        var single = await AmsSteps.DeployOrFailAsync(context, Agent, Record("Test", "package"), "again");

        Assert.Equal((true, false), (retried.Retried, single.Retried));
    }

    [Fact]
    public void ARefusalPassesOnlyWithTheExpectedErrorKind()
    {
        var assertions = new Assertions(new Transcript(_directory));
        var refused = new MsagentResult(["agent", "show", "--json"], 1, Failure(AmsOutput.AgentNotFound, "AMS has no such agent in the home environment.", 1), string.Empty, TimeSpan.Zero);

        assertions.RefusedWith(refused, AmsOutput.AgentNotFound, "the deleted agent");
        var otherKind = Assert.Throws<AssertionException>(() => assertions.RefusedWith(refused, "deployment-not-found", "the deleted deployment"));
        var success = Assert.Throws<AssertionException>(() => assertions.RefusedWith(Result(CreatedAgent(Name), 0), AmsOutput.AgentNotFound, "an agent that exists"));

        Assert.Contains("the deleted agent: agent-not-found", assertions.Passed);
        Assert.Equal(("deployment-not-found", AmsOutput.AgentNotFound), (otherKind.Expected, otherKind.Actual));
        Assert.Equal("an agent that exists: fails", success.Check);
    }

    [Fact]
    public void ADeploymentWithoutARequiredFieldFailsTheScenario() =>
        Assert.Throws<E2EException>(() => AmsSteps.Deployment(Result(Json(new { success = true, deployment = new { deploymentId = DeploymentId } }), 0)));

    [Fact]
    public void ADeployOutcomeReadsTheBotTheEndpointItLeftAndWhetherItSwitchedKind()
    {
        var outcome = AmsSteps.Deployed(Result(Json(new { success = true, deployment = DeploymentObject("Dev", "package", "ams-bot"), bot = "kept", previousEndpoint = Endpoint, kindSwitched = true }), 0));

        Assert.Equal(("kept", Endpoint, true, "ams-bot"), (outcome.Bot, outcome.PreviousEndpoint, outcome.KindSwitched, outcome.Deployment.BotName));
        Assert.False(AmsSteps.Deployed(Result(Deployment("Dev", "dev-endpoint"), 0)).KindSwitched);
    }

    [Theory]
    [InlineData("https://runtime.example.test/agenthostingruntime/tenants/T/environments/39B8DE47-A594-E9F6-BB18-BAC57B8AD289/agents/e27dcdf7-ecfa-4732-b5aa-b38bb5a3c66c/api/messages", true)]
    [InlineData("https://runtime.example.test/agenthostingruntime/tenants/T/environments/245ed99c-a1cc-eae7-9ab5-367e3d921f1d/agents/E27DCDF7-ECFA-4732-B5AA-B38BB5A3C66C/api/messages", false)]
    [InlineData(Endpoint, false)]
    [InlineData(null, false)]
    public void TheHostedRuntimeEndpointNamesTheAgentInTheTargetEnvironment(string? endpoint, bool hosted) =>
        Assert.Equal(hosted, AmsSteps.IsHostedRuntimeEndpoint(endpoint, Agent, Target));

    private (ScenarioContext Context, FakeMsagent Fake) Scripted(params (string StandardOutput, int ExitCode)[] responses)
    {
        var fake = new FakeMsagent(Path.Combine(_directory, "msagent"), responses);
        var transcript = new Transcript(_directory);
        var config = new E2EConfig { TenantId = Tenant, Ams = new AmsConfig { ControlPlane = new AmsServiceConfig { Endpoint = "https://ams.example.test" }, DataPlane = new AmsServiceConfig { Endpoint = "https://amsdp.example.test" }, Cloud = "test", DevEnvironmentId = Home, TestEnvironmentId = Target, AllowCreate = true, PackagePath = Path.Combine(_directory, "echo.zip") } };
        var msagent = new MsagentCli(fake.ExecutablePath, transcript, Path.Combine(_directory, "dumps"));
        var azure = new AzureCli(transcript);

        return (new ScenarioContext
        {
            ScenarioId = "AC-99",
            Msagent = msagent,
            Config = config,
            Assert = new Assertions(transcript),
            Ledger = _ledger,
            Workspaces = new Workspaces(_directory, Path.Combine(_directory, "ws"), config, "test"),
            Cloud = new CloudProbe(msagent, azure),
            Session = new SessionResources(),
            Transcript = transcript,
            RunSuffix = "test",
            CancellationToken = CancellationToken.None,
            Ams = new AmsServices(config, msagent, new AmsStepTiming(TimeSpan.Zero, TimeSpan.Zero)),
        }, fake);
    }

    private static AmsDeployment Record(string deploymentType, string kind) => new(DeploymentId, Name, Target, deploymentType, EntraId, "NotDeployed", kind, null, kind == "dev-endpoint" ? Endpoint : null, kind == "dev-endpoint", null, null, null);

    private static MsagentResult Result(string standardOutput, int exitCode) => new([], exitCode, standardOutput, string.Empty, TimeSpan.Zero);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string Failure(string errorKind, string message, int exitCode) => Json(new { success = false, exitCode, errorMessage = message, errorKind });

    private static string CreatedAgent(string name) => Json(new { success = true, status = "agent-created", environmentId = Home, agentId = AgentId, displayName = name });

    private static string Deployment(string deploymentType, string kind, string? botName = null) => Json(new { success = true, deployment = DeploymentObject(deploymentType, kind, botName) });

    private static object DeploymentObject(string deploymentType, string kind, string? botName) =>
        new { deploymentId = DeploymentId, deploymentName = Name, targetEnvironmentId = Target, deploymentType, entraId = EntraId, state = botName is null ? "NotDeployed" : "Deployed", kind, requestedEndpoint = Endpoint, endpointChangePending = botName is null, botService = botName is null ? null : new { name = botName, endpoint = Endpoint } };
}
