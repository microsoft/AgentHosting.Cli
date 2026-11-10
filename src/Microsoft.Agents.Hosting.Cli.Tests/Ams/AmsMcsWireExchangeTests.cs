using System.Net;
using System.Web;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsMcsWireExchangeTests
{
    internal const string Repository = "cccccccc-3333-4333-8333-000000000003";

    private const string Agent = AmsMcsOperationAdapterTests.Agent;
    private const string Home = AmsMcsOperationAdapterTests.Home;
    private const string EmptyRepository = "cccccccc-3333-4333-8333-000000000013";
    private const string WorkflowAgent = "aaaaaaaa-1111-4111-8111-000000000011";
    private const string WorkflowDev = "eeeeeeee-5555-4555-8555-000000000015";
    private const string Bot = "b0b0b0b0-4444-4444-8444-000000000014";
    private const string Flow = "0a1b2c3d-1111-4111-8111-00000000000a";
    private const string Captured = "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";

    private static readonly string[] RouteHeaders = ["Prefer", AmsAgentCreationAdapter.IdempotencyKeyHeaderName, "If-Match", AmsMcsOperationAdapter.AgentTypeHeaderName];

    private static readonly string[] ExchangeNames =
    [
        "cmp-deployment-create.exchange.json",
        "cmp-deployment-create.replay.exchange.json",
        "cmp-deployment-create.id-conflict.exchange.json",
        "cmp-deployment-create.environment-taken.exchange.json",
        "cmp-deployment-delete.exchange.json",
        "mcs-agent-delete.exchange.json",
        "mcs-agent-register.exchange.json",
        "mcs-agent-register.replay.exchange.json",
        "mcs-workflow-register.exchange.json",
        "mcs-deploy-record.exchange.json",
        "mcs-deploy-record.workflow.exchange.json",
        "mcs-repository.exchange.json",
        "mcs-repository.empty.exchange.json",
        "mcs-repository.with-deployments.exchange.json",
    ];

    public static TheoryData<string> Exchanges => new(ExchangeNames);

    public static TheoryData<string, int, string, string?> ExchangeErrors()
    {
        var data = new TheoryData<string, int, string, string?>();
        foreach (var name in ExchangeNames)
        {
            foreach (var error in WireFixture.Exchange(name).Errors)
            {
                data.Add(name, error.Status, error.Code, error.BodyFixture);
            }
        }

        return data;
    }

    [Fact]
    public void EveryExchangeFixture_IsCovered()
    {
        Assert.Equal(ExchangeNames.Order(StringComparer.Ordinal), Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Ams", "WireFixtures"), "*.exchange.json").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Exchanges))]
    public async Task Msagent_SendsExactlyTheRequestOfTheExchange(string name)
    {
        var exchange = WireFixture.Exchange(name);
        var handler = new CapturingPackageHandler(_ => exchange.Answer());

        try
        {
            await SendAsync(AmsMcsOperationAdapterTests.Adapter(handler), name);
        }
        catch (AgentHostingException) when (exchange.Status >= 400)
        {
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(exchange.Method, request.Method.Method);
        Assert.Equal($"/agenthosting/{exchange.Path}", request.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string>(exchange.Query) { ["api-version"] = AmsRoutes.ApiVersion }.OrderBy(pair => pair.Key, StringComparer.Ordinal), Query(request.Uri).OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(RouteHeaders, header => Assert.Equal(exchange.Headers.GetValueOrDefault(header), request.Headers.GetValueOrDefault(header)));
        Assert.Equal(exchange.Headers.GetValueOrDefault("Content-Type"), request.ContentType);
        if (exchange.RequestBody is null)
        {
            Assert.Empty(request.Body);
        }
        else
        {
            WireFixture.AssertSameJson(exchange.RequestBody, request.Body);
        }
    }

    [Theory]
    [MemberData(nameof(Exchanges))]
    public async Task Msagent_ReadsTheAnswerOfTheExchange_WithoutAnOperation(string name)
    {
        var exchange = WireFixture.Exchange(name);
        var adapter = AmsMcsOperationAdapterTests.Adapter(new CapturingPackageHandler(_ => exchange.Answer()));

        Assert.False(exchange.StartsOperation);
        if (exchange.Status >= 400)
        {
            AssertActsOn(exchange.Status, AmsMcsWire.ReadErrorBody(WireFixture.Read(exchange.ResponseBody!))!.Code!, await Assert.ThrowsAnyAsync<AgentHostingException>(() => SendAsync(adapter, name)));
            return;
        }

        switch (await SendAsync(adapter, name))
        {
            case AmsMcsAgentRecord agent:
                WireFixture.AssertReadsAgent(WireFixture.Node(exchange.ResponseBody!), agent);
                break;
            case AmsMcsRepository repository:
                AssertReadsRepository(WireFixture.Node(exchange.ResponseBody!), repository);
                break;
            case AmsMcsDeploymentRecord deployment:
                WireFixture.AssertReadsDeployment(WireFixture.Node(exchange.ResponseBody!), deployment);
                break;
            default:
                Assert.Equal((204, null), (exchange.Status, exchange.ResponseBody));
                break;
        }
    }

    [Theory]
    [MemberData(nameof(ExchangeErrors))]
    public async Task AnErrorTheExchangeLists_IsTheFailureMsagentActsOn(string name, int status, string code, string? bodyFixture)
    {
        var body = new WireExchangeError(status, code, bodyFixture, "The service refused the request.").Body();
        var adapter = AmsMcsOperationAdapterTests.Adapter(new CapturingPackageHandler(_ => WireExchange.Respond(status, body)));

        AssertActsOn(status, code, await Assert.ThrowsAnyAsync<AgentHostingException>(() => SendAsync(adapter, name)));
    }

    [Theory]
    [InlineData("new agent", "mcs-agent-register.request.json", "mcs-agent-register.response.json")]
    [InlineData("existing repository", "mcs-agent-register.existing-repository.request.json", "mcs-agent-register.existing-repository.response.json")]
    [InlineData("clone agent", "mcs-agent-register.clone-agent.request.json", "mcs-agent-register.clone-agent.response.json")]
    [InlineData("new workflow", "mcs-workflow-register.request.json", "mcs-workflow-register.response.json")]
    [InlineData("clone workflow", "mcs-workflow-register.clone.request.json", "mcs-workflow-register.clone.response.json")]
    public async Task Register_SendsTheWireContract_WithExplicitNullsForUnsetMembers_AndReadsTheAnswer(string scenario, string requestFixture, string responseFixture)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Created, WireFixture.Read(responseFixture)));

        var registered = await AmsMcsOperationAdapterTests.Adapter(handler).RegisterAsync(Registration(scenario, AmsMcsOperationAdapterTests.FrontDoor), default);

        WireFixture.AssertSameJson(requestFixture, Assert.Single(handler.Requests).Body);
        WireFixture.AssertReadsAgent(WireFixture.Node(responseFixture), registered);
    }

    [Fact]
    public async Task ARepeatedAgentDelete_AnsweredNotFound_CountsAsDeleted()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.NotFound, WireFixture.Read("mcs-agent-delete.not-found.error.json")));
        var adapter = AmsMcsOperationAdapterTests.Adapter(handler);

        await Deletion(adapter).DeleteAsync(McsRecords.Home(), StoredAgent(), McsRecords.ETag, default);

        Assert.Equal(("DELETE", McsRecords.ETag), (Assert.Single(handler.Requests).Method.Method, handler.Requests[0].IfMatch));
    }

    [Fact]
    public async Task AnAgentDelete_ThatAmsRefusesBecauseTheAgentChanged_IsAConflictToRetry()
    {
        var adapter = AmsMcsOperationAdapterTests.Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.PreconditionFailed, WireFixture.Read("mcs-agent-delete.precondition-failed.error.json"))));

        var failure = await Assert.ThrowsAsync<AmsAgentException>(() => Deletion(adapter).DeleteAsync(McsRecords.Home(), StoredAgent(), McsRecords.ETag, default));

        Assert.Equal((ErrorCodes.AmsAgent.AgentRecordChanged, ExitCodes.Failure, "Run the same command again."), (failure.ErrorCode, failure.ExitCode, failure.Remediation));
    }

    [Fact]
    public async Task AnAgentDelete_WithoutTheETag_ReadsTheAgentFirst_AndSendsItsETag()
    {
        var handler = new CapturingPackageHandler(request => request.Method == HttpMethod.Get ? FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-register.response.json")) : new HttpResponseMessage(HttpStatusCode.NoContent));
        var adapter = AmsMcsOperationAdapterTests.Adapter(handler);

        await Deletion(adapter).DeleteAsync(McsRecords.Home(), StoredAgent(), null, default);

        Assert.Equal([$"GET /agenthosting/agents/{Agent}", $"DELETE /agenthosting/agents/{Agent}/mcs"], handler.Requests.Select(request => $"{request.Method.Method} {request.Uri.AbsolutePath}"));
        Assert.Equal("\"etag-1\"", handler.Requests[1].IfMatch);
    }

    [Fact]
    public async Task ARepeatedDeploymentDelete_AnsweredNotFound_CountsAsDeleted_AndDotConfigIsWritten()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.NotFound, WireFixture.Read("cmp-deployment-delete.not-found.error.json")));
        var adapter = AmsMcsOperationAdapterTests.Adapter(handler);
        var mirror = new AmsRecordMirror(adapter, Deletion(adapter), McsRecords.Targets());
        var written = false;

        var mirrored = await mirror.ApplyAsync(Path.GetTempPath(), new DeploymentDeleted(StoredAgent(), ManagedAgentFixtures.Deployment(McsRecords.TestDeploymentId, Agent, "contoso-test", DeploymentStages.Test, "env-test")), () => written = true, default);

        Assert.True(written && mirrored.Mirrored);
        Assert.Equal(("DELETE", "MCSAgent"), (Assert.Single(handler.Requests).Method.Method, handler.Requests[0].Headers[AmsMcsOperationAdapter.AgentTypeHeaderName]));
    }

    internal static AmsMcsRegistration NewAgent(AmsServiceTarget target) => Registration("new agent", target);

    private static AmsAgentDeletion Deletion(AmsMcsOperationAdapter adapter) => new(adapter, adapter, McsRecords.Targets(), new AmsCloudCacheShelf(new FakeGitPort(), new FixedClock(DateTimeOffset.UnixEpoch)), new AgentConfigLocator(), new SyncComponentCollectionAdapter());

    internal static AmsMcsDeployRecordRequest AgentDeployRecord(AmsServiceTarget target) => new() { Target = target, AgentId = Agent, DeploymentId = AmsMcsOperationAdapterTests.DevDeployment, BotId = Bot, CommitSha = AmsMcsOperationAdapterTests.Head, Exact = true, Published = true, DeployedTime = new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero) };

    internal static AmsMcsDeploymentCreateTransportRequest TestCreate(AmsServiceTarget target, string deploymentId, string deploymentName) => new() { Target = target, AgentId = Agent, DeploymentId = deploymentId, AgentType = "MCSAgent", DeploymentType = "Test", DeploymentName = deploymentName, TargetEnvironmentId = AmsMcsOperationAdapterTests.TestEnvironment };

    private static AmsMcsRegistration Registration(string scenario, AmsServiceTarget target)
    {
        var agent = new AmsMcsRegistration { Target = target, AgentId = Agent, DisplayName = "Contoso HR", Description = "HR helper", AgentType = "MCSAgent", SchemaName = "cr1a2_contosoHr", AuthoringShape = "cliagent", Branch = "main", AgentFolder = "ContosoHR", Deployments = [new AmsMcsRegistrationDeployment(AmsMcsOperationAdapterTests.DevDeployment, "Dev", "contoso-dev", Home, null, null)] };
        var workflow = agent with { AgentId = WorkflowAgent, DisplayName = "Expense approvals", Description = null, AgentType = "MCSWorkflow", SchemaName = null, AuthoringShape = null, AgentFolder = "ExpenseApprovals", Deployments = [new AmsMcsRegistrationDeployment(WorkflowDev, "Dev", "approvals-dev", Home, null, null)] };
        return scenario switch
        {
            "existing repository" => agent with { RepositoryId = Repository, Deployments = [] },
            "clone agent" => agent with { Description = null, AuthoringShape = "empty", Deployments = [new AmsMcsRegistrationDeployment(AmsMcsOperationAdapterTests.DevDeployment, "Dev", "contoso-dev", Home, Bot, null)] },
            "new workflow" => workflow,
            "clone workflow" => workflow with { RepositoryId = Repository, Deployments = [new AmsMcsRegistrationDeployment(WorkflowDev, "Dev", "approvals-dev", Home, null, Flow)] },
            _ => agent,
        };
    }

    private static async Task<object?> SendAsync(AmsMcsOperationAdapter adapter, string exchange)
    {
        var target = AmsMcsOperationAdapterTests.FrontDoor;
        switch (exchange)
        {
            case "cmp-deployment-create.exchange.json" or "cmp-deployment-create.replay.exchange.json":
                return await adapter.CreateMcsDeploymentAsync(TestCreate(target, AmsMcsOperationAdapterTests.TestDeployment, "contoso-test"), default);
            case "cmp-deployment-create.id-conflict.exchange.json":
                return await adapter.CreateMcsDeploymentAsync(TestCreate(target, AmsMcsOperationAdapterTests.TestDeployment, "contoso-uat"), default);
            case "cmp-deployment-create.environment-taken.exchange.json":
                return await adapter.CreateMcsDeploymentAsync(TestCreate(target, "ffffffff-6666-4666-8666-000000000026", "contoso-qa"), default);
            case "cmp-deployment-delete.exchange.json":
                await adapter.DeleteDeploymentAsync(target, Agent, AmsMcsOperationAdapterTests.TestDeployment, "MCSAgent", default);
                return null;
            case "mcs-agent-delete.exchange.json":
                await adapter.DeleteAgentAsync(target, Agent, McsRecords.ETag, default);
                return null;
            case "mcs-agent-register.exchange.json" or "mcs-agent-register.replay.exchange.json":
                return await adapter.RegisterAsync(NewAgent(target), default);
            case "mcs-workflow-register.exchange.json":
                return await adapter.RegisterAsync(Registration("clone workflow", target), default);
            case "mcs-deploy-record.exchange.json":
                return await adapter.RecordDeployAsync(AgentDeployRecord(target), default);
            case "mcs-deploy-record.workflow.exchange.json":
                return await adapter.RecordDeployAsync(new AmsMcsDeployRecordRequest { Target = target, AgentId = WorkflowAgent, DeploymentId = WorkflowDev, WorkflowId = Flow, CommitSha = Captured, Exact = false, Published = false, DeployedTime = new DateTimeOffset(2026, 8, 2, 7, 45, 0, TimeSpan.Zero) }, default);
            case "mcs-repository.exchange.json":
                return await adapter.ReadRepositoryAsync(target, Repository, "ContosoHR", includeDeployments: false, default);
            case "mcs-repository.empty.exchange.json":
                return await adapter.ReadRepositoryAsync(target, EmptyRepository, "ContosoHR", includeDeployments: false, default);
            case "mcs-repository.with-deployments.exchange.json":
                return await adapter.ReadRepositoryAsync(target, Repository, null, includeDeployments: true, default);
            default:
                throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "The exchange has no call.");
        }
    }

    private static void AssertActsOn(int status, string code, AgentHostingException failure)
    {
        if (AmsMcsOperationErrorMapTests.ContractCodes.FirstOrDefault(row => (string)row[0] == code) is { } row)
        {
            Assert.Equal((int)row[2], Assert.IsType<AmsAgentException>(failure).ErrorCode);
        }
        else
        {
            Assert.Equal(status, Assert.IsType<TargetRequestException>(failure).Status);
        }
    }

    private static void AssertReadsRepository(System.Text.Json.Nodes.JsonNode wire, AmsMcsRepository read)
    {
        Assert.Equal(((string?)wire["repositoryId"], (string?)wire["name"], (string?)wire["gitUrl"], (string?)wire["branch"], (string?)wire["headCommitSha"], (bool?)wire["agentFolderExists"] ?? false), (read.RepositoryId, read.Name, read.GitUrl, read.Branch, read.HeadCommitSha, read.AgentFolderExists));
        var agents = wire["agents"]!.AsArray();
        Assert.Equal(agents.Count, read.Agents.Count);
        for (var index = 0; index < agents.Count; index++)
        {
            WireFixture.AssertReadsListItem(agents[index]!, read.Agents[index]);
        }
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query.AllKeys.ToDictionary(key => key!, key => query[key]!, StringComparer.Ordinal);
    }

    private static ManagedAgentRecord StoredAgent() => ManagedAgentFixtures.Agent(Agent, "Contoso HR") with { AgentLocation = "ContosoHR", SourceControlMetadata = new AgentSourceControlMetadata(Repository, "main", "ContosoHR") };
}
