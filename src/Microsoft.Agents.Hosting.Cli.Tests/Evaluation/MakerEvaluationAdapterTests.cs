using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class MakerEvaluationAdapterTests
{
    private const string Gateway = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/";
    private const string EnvironmentId = "env-1";
    private const string TenantId = "tenant-1";
    private const string BotId = "bot-1";
    private const string TestSetId = "testset-1";
    private const string RunId = "run-1";
    private const string Base = "api/botmanagement/v2/environments/env-1/bots/bot-1/makerevaluations";

    private static readonly PowerPlatformCloudMap CloudMap = PowerPlatformCloudMap.ForCloud(CloudInstance.Public);

    private static CopilotEnvironmentContext Context(string? gateway = Gateway) => new()
    {
        EnvironmentId = EnvironmentId,
        TenantId = TenantId,
        DataverseOrganization = new Uri("https://org.crm.dynamics.com"),
        AgentManagementUrl = gateway is null ? null : new Uri(gateway),
    };

    private static (MakerEvaluationAdapter Adapter, RecordingHandler Handler, StubTokens Tokens) Create(params string[] responses) => Create(CloudMap, CoreServicesClusterCategory.Prod, responses);

    private static (MakerEvaluationAdapter Adapter, RecordingHandler Handler, StubTokens Tokens) Create(PowerPlatformCloudMap cloudMap, CoreServicesClusterCategory cluster, params string[] responses)
    {
        var handler = new RecordingHandler(responses);
        var tokens = new StubTokens();

        return (new MakerEvaluationAdapter(tokens, cloudMap, cluster, () => handler), handler, tokens);
    }

    [Fact]
    public async Task ListRuns_RefusesANotFoundRatherThanReportingNoRuns()
    {
        var handler = new RecordingHandler(["{}"], HttpStatusCode.NotFound);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"runs\"")]
    [InlineData("")]
    public async Task ListRuns_RefusesAPayloadThatIsNotAListOfRuns(string payload)
    {
        var (adapter, _, _) = Create(payload);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None));
    }

    [Fact]
    public async Task ListTestSets_RefusesANotFoundRatherThanReportingNoTestSets()
    {
        var handler = new RecordingHandler(["{}"], HttpStatusCode.NotFound);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListTestSetsAsync(Context(), BotId, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"testComponents\":\"none\"}")]
    [InlineData("5")]
    [InlineData("")]
    public async Task ListTestSets_RefusesAPayloadThatIsNotAListOfComponents(string payload)
    {
        var (adapter, _, _) = Create(payload);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListTestSetsAsync(Context(), BotId, CancellationToken.None));
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Prod, Gateway, "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation")]
    [InlineData(CoreServicesClusterCategory.Test, "https://powervamg.us-il106.gateway.test.island.powerapps.com/", "api://a522f059-bb65-47c0-8934-7db6e5286414/user_impersonation")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "https://powervamg.cn-il101.gateway.prod.island.powerapps.cn/", "api://96ff4394-9197-43aa-b393-6a41652e21f8/user_impersonation")]
    public async Task ListRuns_RequestsTheDelegatedScopeOfTheClustersControlPlaneForTheEnvironmentTenant(CoreServicesClusterCategory cluster, string gateway, string scope)
    {
        var (adapter, handler, tokens) = Create(CloudMap, cluster, "[]");

        await adapter.ListRunsAsync(Context(gateway), BotId, 20, CancellationToken.None);

        Assert.Equal([scope], tokens.RequestedScopes);
        Assert.Equal(TenantId, tokens.RequestedTenantId);
        Assert.StartsWith($"{gateway}{Base}", Assert.Single(handler.Requests).Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRuns_AsksTheServiceForTheRequestedPageSize()
    {
        var (adapter, handler, _) = Create("[]");

        await adapter.ListRunsAsync(Context(), BotId, 21, CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal($"{Gateway}{Base}?count=21&ApplyV2Migration=true&cli=true", handler.Requests[0].Url);
    }

    [Fact]
    public async Task ListRuns_SendsEveryRoutingHeaderTheGatewayRequires()
    {
        var (adapter, handler, _) = Create("[]");

        await adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None);

        Assert.Equal(TenantId, handler.Requests[0].Header("x-cci-tenantid"));
        Assert.Equal(TenantId, handler.Requests[0].Header("x-cci-routing-tenantid"));
        Assert.Equal(EnvironmentId, handler.Requests[0].Header("x-cci-bapenvironmentid"));
        Assert.Equal(BotId, handler.Requests[0].Header("x-cci-botid"));
        Assert.Equal(BotId, handler.Requests[0].Header("x-cci-cdsbotid"));
        Assert.Equal(BotId, handler.Requests[0].Header("x-cci-routing-botid"));
        Assert.Equal("Web", handler.Requests[0].Header("x-cci-applicationsource"));
    }

    [Fact]
    public async Task ListRuns_SendsTheBearerToken()
    {
        var (adapter, handler, _) = Create("[]");

        await adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None);

        Assert.Equal("Bearer stub-token", handler.Requests[0].Header("Authorization"));
    }

    [Fact]
    public async Task ListRuns_ProjectsTheRunsTheServiceReported()
    {
        var (adapter, _, _) = Create("""
            [{"id":"run-1","name":"Nightly","state":"Completed","testSetId":"testset-1","testCaseCount":13,
              "startTime":"2026-10-04T19:53:36+00:00","endTime":"2026-10-04T20:07:25+00:00","runOnPublishedBot":false,
              "aggregatedGraderResults":[{"name":"totalSucceeded","count":11},{"name":"totalFailed","count":2},
                                         {"name":"totalInvalid","count":0},{"name":"totalErrors","count":0}],
              "consumption":{"evaluators":{"totalCredits":91.224}}}]
            """);

        var runs = await adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None);

        Assert.Equal(RunId, runs[0].RunId);
        Assert.Equal("Nightly", runs[0].Name);
        Assert.Equal(13, runs[0].TestCaseCount);
        Assert.Equal(11, runs[0].Totals!.Succeeded);
        Assert.Equal(2, runs[0].Totals!.Failed);
        Assert.Equal(91.224, runs[0].Credits);
        Assert.True(runs[0].IsTerminal);
    }

    [Fact]
    public async Task ListRuns_LeavesTotalsAbsentWhileTheRunIsStillGoing()
    {
        var (adapter, _, _) = Create("""[{"id":"run-1","state":"InProgress","endTime":null}]""");

        var runs = await adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None);

        Assert.Null(runs[0].Totals);
        Assert.Null(runs[0].Credits);
        Assert.False(runs[0].IsTerminal);
    }

    [Fact]
    public async Task GetRun_ReadsTheDetailsRoute()
    {
        var (adapter, handler, _) = Create("""{"id":"run-1","state":"Completed"}""");

        await adapter.GetRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal($"{Gateway}{Base}/{RunId}/details?ApplyV2Migration=true&cli=true", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GetRun_ProjectsPerCaseGraderDimensions()
    {
        var (adapter, _, _) = Create("""
            {"id":"run-1","state":"Completed","details":{"testCases":[
              {"id":"case-1","executionState":"Evaluated",
               "queries":[{"query":"asked","answer":"replied","executionState":"Evaluated"}],
               "graderMetrics":{"evaluationResult":"Pass","queryResponseMetrics":[
                 {"properties":{"abstention":"No","relevance":"Yes","groundedness":"Yes","completeness":"Yes"}}]}}]}}
            """);

        var detail = await adapter.GetRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal("Pass", detail!.TestCases[0].EvaluationResult);
        Assert.Equal("No", detail.TestCases[0].Dimensions!.Abstention);
        Assert.Equal("Yes", detail.TestCases[0].Dimensions!.Relevance);
        Assert.Equal("asked", detail.TestCases[0].Queries[0].Query);
        Assert.Equal("replied", detail.TestCases[0].Queries[0].Answer);
        Assert.Equal(1, detail.EvaluatedCount);
    }

    [Fact]
    public async Task GetRun_ReportsTheTestSetAndDataTypeTheRunCarries()
    {
        var (adapter, _, _) = Create("""
            {"id":"run-1","state":"Completed","testSetId":"testset-7","evaluationSetType":"MultiTurn","testCaseCount":3}
            """);

        var detail = await adapter.GetRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal("testset-7", detail!.Summary.TestSetId);
        Assert.Equal("MultiTurn", detail.Summary.EvaluationSetType);
    }

    [Fact]
    public async Task GetRun_ReturnsNullWhenTheServiceDoesNotKnowTheRun()
    {
        var handler = new RecordingHandler([], HttpStatusCode.NotFound);

        Assert.Null(await new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler).GetRunAsync(Context(), BotId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task ListTestSets_ReadsComponentsNestedUnderTheWrapper()
    {
        var (adapter, _, _) = Create("""
            {"testComponents":[{"numberOfTestCases":13,"evaluationSetType":"MultiTurn",
              "component":{"id":"testset-1","displayName":"Nightly","state":"Active"}}]}
            """);

        var testSets = await adapter.ListTestSetsAsync(Context(), BotId, CancellationToken.None);

        Assert.Equal(TestSetId, testSets[0].TestSetId);
        Assert.Equal("Nightly", testSets[0].DisplayName);
        Assert.Equal(13, testSets[0].ConversationCount);
        Assert.Equal("MultiTurn", testSets[0].EvaluationSetType);
    }

    [Fact]
    public async Task ListTestSets_ReadsComponentsSuppliedFlat()
    {
        var (adapter, _, _) = Create("""
            {"testComponents":[{"id":"testset-1","displayName":"Nightly","numberOfTestCases":3}]}
            """);

        var testSets = await adapter.ListTestSetsAsync(Context(), BotId, CancellationToken.None);

        Assert.Equal(TestSetId, testSets[0].TestSetId);
        Assert.Equal(3, testSets[0].ConversationCount);
    }

    [Fact]
    public async Task CreateTestSet_PostsAnEvaluationSetCarryingTheGeneralQualityGrader()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.CreateTestSetAsync(Context(), BotId, "Nightly", CancellationToken.None));

        var component = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("testComponents")[0];

        Assert.Equal($"{Gateway}{Base}/testcomponent?ApplyV2Migration=true&cli=true", handler.Requests[0].Url);
        Assert.Equal("MakerEvaluationUpdateTestComponent", component.GetProperty("$kind").GetString());
        Assert.Equal("Add", component.GetProperty("operationType").GetString());
        Assert.Equal("TestCaseComponent", component.GetProperty("component").GetProperty("$kind").GetString());
        Assert.Equal("Nightly", component.GetProperty("component").GetProperty("displayName").GetString());
        Assert.Equal("Testing", component.GetProperty("component").GetProperty("category").GetString());
        Assert.Equal("Active", component.GetProperty("component").GetProperty("state").GetString());
        Assert.StartsWith("mspva_", component.GetProperty("component").GetProperty("schemaName").GetString(), StringComparison.Ordinal);

        var definition = component.GetProperty("component").GetProperty("definition");

        Assert.Equal("EvaluationSet", definition.GetProperty("$kind").GetString());
        Assert.Equal("GeneralQualityGrader", definition.GetProperty("graders")[0].GetProperty("$kind").GetString());
    }

    [Fact]
    public async Task CreateTestSet_ReturnsTheIdTheServiceAssignedToTheSchemaNameItSent()
    {
        var handler = new RecordingHandler([]);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        handler.Respond(request => """{"addedComponentsIdsBySchemaName":{"PLACEHOLDER":"testset-1"}}""".Replace("PLACEHOLDER", SchemaNameOf(request), StringComparison.Ordinal));

        Assert.Equal(TestSetId, await adapter.CreateTestSetAsync(Context(), BotId, "Nightly", CancellationToken.None));
    }

    [Fact]
    public async Task AddConversations_SendsEveryConversationInOneRequest()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first"), Conversation("second"), Conversation("third")], CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(3, JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("testComponents").GetArrayLength());
    }

    [Fact]
    public async Task AddConversations_ParentsEveryCaseToTheTestSet()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first")], CancellationToken.None);

        var component = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("testComponents")[0].GetProperty("component");

        Assert.Equal(TestSetId, component.GetProperty("parentBotComponentId").GetString());
        Assert.Equal("first", component.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task AddConversations_BuildsAlternatingUserAndAgentActivities()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("asked", "replied")], CancellationToken.None);

        var activities = JsonDocument.Parse(handler.Requests[0].Body!).RootElement
            .GetProperty("testComponents")[0].GetProperty("component").GetProperty("definition").GetProperty("activities");

        Assert.Equal(2, activities.GetArrayLength());
        Assert.Equal("user", RoleOf(activities[0]));
        Assert.Equal("agent", RoleOf(activities[1]));
        Assert.Equal("asked", TextOf(activities[0]));
        Assert.Equal("replied", TextOf(activities[1]));
        Assert.Equal("MultiTurnActivity", activities[0].GetProperty("$kind").GetString());
        Assert.Equal("Message", activities[0].GetProperty("activity").GetProperty("$kind").GetString());
    }

    [Fact]
    public async Task AddConversations_OmitsTheAgentActivityWhenNoReferenceReplyWasGiven()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("asked")], CancellationToken.None);

        var activities = JsonDocument.Parse(handler.Requests[0].Body!).RootElement
            .GetProperty("testComponents")[0].GetProperty("component").GetProperty("definition").GetProperty("activities");

        Assert.Equal(1, activities.GetArrayLength());
    }

    [Fact]
    public async Task AddConversations_RecordsWhereEachConversationCameFrom()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(
            Context(),
            BotId,
            TestSetId,
            [Conversation("asked") with { Source = EvaluationConversationSource.Generated }],
            CancellationToken.None);

        var definition = JsonDocument.Parse(handler.Requests[0].Body!).RootElement
            .GetProperty("testComponents")[0].GetProperty("component").GetProperty("definition");

        Assert.Equal("MultiTurnEvaluationCase", definition.GetProperty("$kind").GetString());
        Assert.Equal("Generated", definition.GetProperty("source").GetString());
        Assert.NotNull(definition.GetProperty("extensionData").GetProperty("displayOrder").GetString());
    }

    [Fact]
    public async Task AddConversations_SendsNothingWhenThereAreNoConversations()
    {
        var (adapter, handler, _) = Create();

        Assert.Empty(await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AddConversations_GivesEveryCaseItsOwnSchemaName()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first"), Conversation("second"), Conversation("third")], CancellationToken.None);

        var components = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("testComponents");
        var schemaNames = components.EnumerateArray().Select(entry => entry.GetProperty("component").GetProperty("schemaName").GetString()!).ToArray();

        Assert.Equal(3, schemaNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AddConversations_OrdersCasesByDescendingDisplayOrder()
    {
        var (adapter, handler, _) = Create("""{"addedComponentsIdsBySchemaName":{}}""");

        await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first"), Conversation("second"), Conversation("third")], CancellationToken.None);

        var orders = JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("testComponents").EnumerateArray()
            .Select(entry => long.Parse(entry.GetProperty("component").GetProperty("definition").GetProperty("extensionData").GetProperty("displayOrder").GetString()!, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal([.. orders.OrderByDescending(order => order)], orders);
        Assert.Equal(3, orders.Distinct().Count());
        Assert.Equal([orders[0], orders[0] - 1, orders[0] - 2], orders);
    }

    [Fact]
    public async Task GetTestSet_ReportsAbsenceWhenTheDetailsAreGone()
    {
        var handler = new RecordingHandler(["{}"], HttpStatusCode.NotFound);

        Assert.Null(await new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler).GetTestSetAsync(Context(), BotId, Summary(), CancellationToken.None));
    }

    [Fact]
    public async Task AddConversations_ReturnsOnlyTheIdsTheServiceAcknowledged()
    {
        var (adapter, handler, _) = Create();
        handler.Respond(body => "{\"addedComponentsIdsBySchemaName\":{\"" + SchemaNameOf(body) + "\":\"case-1\"}}");

        var ids = await adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first"), Conversation("second")], CancellationToken.None);

        Assert.Equal(["case-1"], ids);
    }

    [Fact]
    public async Task AddConversations_FailsWhenTheServiceReportsNoAddedComponents()
    {
        var (adapter, _, _) = Create("{}");

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.AddConversationsAsync(Context(), BotId, TestSetId, [Conversation("first")], CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyVerb_MapsEveryServiceFailureToATypedFailure(HttpStatusCode status)
    {
        var handler = new RecordingHandler(["""{"Error":{"Code":"failed","Message":"Evaluation run not found."}}"""], status);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.DeleteRunAsync(Context(), BotId, "run-9", CancellationToken.None));
    }

    [Fact]
    public async Task AnyVerb_KeepsTheTokenOutOfTheFailureMessage()
    {
        var handler = new RecordingHandler(["""{"Error":{"Code":"failed","Message":"Evaluation run not found."}}"""], HttpStatusCode.NotFound);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.DeleteRunAsync(Context(), BotId, "run-9", CancellationToken.None));

        Assert.DoesNotContain("stub-token", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRun_PostsTheTestSetAndName()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Queued","TotalItemCount":3}""");

        var run = await adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly" }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;

        Assert.Equal($"{Gateway}{Base}?ApplyV2Migration=true&cli=true", handler.Requests[0].Url);
        Assert.Equal(TestSetId, body.GetProperty("testSetId").GetString());
        Assert.Equal("Nightly", body.GetProperty("clientRequestedEvaluationRunName").GetString());
        Assert.False(body.GetProperty("runOnPublishedBot").GetBoolean());
        Assert.Equal(RunId, run.RunId);
        Assert.Equal(3, run.TestCaseCount);
    }

    [Fact]
    public async Task StartRun_OmitsTheConnectionWhenNoneWasChosen()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Queued"}""");

        await adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly" }, CancellationToken.None);

        Assert.False(JsonDocument.Parse(handler.Requests[0].Body!).RootElement.TryGetProperty("mcsConnectionId", out _));
    }

    [Fact]
    public async Task StartRun_SendsTheConnectionWhenOneWasChosen()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Queued"}""");

        await adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly", ConnectionId = "connection-1" }, CancellationToken.None);

        Assert.Equal("connection-1", JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("mcsConnectionId").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"runId":null,"state":"Queued"}""")]
    [InlineData("""{"runId":"","state":"Queued"}""")]
    [InlineData("""{"runId":"   ","state":"Queued"}""")]
    [InlineData("""{"runId":7,"state":"Queued"}""")]
    [InlineData("""{"runId":"","id":"","state":"Queued"}""")]
    [InlineData("")]
    public async Task StartRun_RefusesAResponseThatDoesNotNameTheRun(string response)
    {
        var (adapter, _, _) = Create(response);

        await AssertNotIdentifiedAsync(adapter);
    }

    [Theory]
    [InlineData("""{"id":"run-1","state":"Queued"}""")]
    [InlineData("""{"runId":"","id":"run-1","state":"Queued"}""")]
    [InlineData("""{"runId":"   ","id":"run-1","state":"Queued"}""")]
    [InlineData("""{"runId":" run-1 ","state":"Queued"}""")]
    public async Task StartRun_TakesTheIdTheServiceDidSupply(string response)
    {
        var (adapter, _, _) = Create(response);

        var run = await adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly" }, CancellationToken.None);

        Assert.Equal(RunId, run.RunId);
    }

    [Fact]
    public async Task CancelRun_ReportsTheRunItAddressedWhenTheAnswerNamesNothingUsable()
    {
        var (adapter, _, _) = Create("""{"runId":"   ","state":"Cancelled"}""");

        Assert.Equal(RunId, (await adapter.CancelRunAsync(Context(), BotId, RunId, CancellationToken.None)).RunId);
    }

    [Fact]
    public async Task CancelRun_FailsWhenTheServiceAnswersWithNoBody()
    {
        var (adapter, _, _) = Create(string.Empty);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.CancelRunAsync(Context(), BotId, RunId, CancellationToken.None));
    }

    [Fact]
    public async Task StartRun_RefusesWithoutLookingForARunToClaim()
    {
        var (adapter, handler, _) = Create("{}", Runs(("run-7", "testset-1", "Nightly", Recent)));

        await AssertNotIdentifiedAsync(adapter);

        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task StartRun_AsksTheServiceOnceWhenItNamesTheRun()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Queued"}""");

        await adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly" }, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
    }

    private static async Task<EvaluationException> AssertNotIdentifiedAsync(MakerEvaluationAdapter adapter)
    {
        var error = await Assert.ThrowsAsync<EvaluationException>(
            () => adapter.StartRunAsync(Context(), BotId, new EvaluationRunStart { TestSetId = TestSetId, Name = "Nightly" }, CancellationToken.None));

        Assert.Equal(EvaluationErrorKinds.RunNotIdentified, error.ErrorKind);
        Assert.Contains("charged run may be in flight", error.Remediation, StringComparison.Ordinal);

        return error;
    }

    private static string Recent => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Runs(params (string RunId, string TestSetId, string Name, string StartTime)[] runs) =>
        "[" + string.Join(',', runs.Select(run =>
            $$"""{"runId":"{{run.RunId}}","state":"Queued","testSetId":"{{run.TestSetId}}","name":"{{run.Name}}","startTime":"{{run.StartTime}}"}""")) + "]";

    [Fact]
    public async Task CancelRun_ReportsTheRunItAddressedWhenTheAnswerOmitsTheId()
    {
        var (adapter, _, _) = Create("""{"state":"Cancelled"}""");

        var run = await adapter.CancelRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal(RunId, run.RunId);
        Assert.Equal("Cancelled", run.State);
    }

    [Fact]
    public async Task DeleteRun_ReportsTheRunItAddressedWhenTheAnswerOmitsTheId()
    {
        var (adapter, _, _) = Create("{}");

        Assert.Equal(RunId, (await adapter.DeleteRunAsync(Context(), BotId, RunId, CancellationToken.None)).RunId);
    }

    [Fact]
    public async Task CancelRun_PatchesTheCancelRouteWithNoBody()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Cancelled"}""");

        var run = await adapter.CancelRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal($"{Gateway}{Base}/{RunId}/cancel?cli=true", handler.Requests[0].Url);
        Assert.Null(handler.Requests[0].Body);
        Assert.Equal("Cancelled", run.State);
    }

    [Fact]
    public async Task DeleteRun_DeletesTheRunRoute()
    {
        var (adapter, handler, _) = Create("""{"runId":"run-1","state":"Deleted"}""");

        var run = await adapter.DeleteRunAsync(Context(), BotId, RunId, CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal($"{Gateway}{Base}/{RunId}?cli=true", handler.Requests[0].Url);
        Assert.Equal("Deleted", run.State);
    }

    [Fact]
    public async Task GenerateConversations_PostsTheRequestedShapeAndProjectsTheReply()
    {
        var (adapter, handler, _) = Create("""
            {"conversations":[{"queries":[{"query":"asked","answer":"replied"},{"query":"again","answer":"ok"}]}]}
            """);

        var conversations = await adapter.GenerateConversationsAsync(Context(), BotId, 10, 3, "1033", CancellationToken.None);

        var body = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;

        Assert.Equal($"{Gateway}{Base}/conversations/generate?cli=true", handler.Requests[0].Url);
        Assert.Equal("MultiTurn", body.GetProperty("evaluationSetType").GetString());
        Assert.Equal(10, body.GetProperty("conversationCount").GetInt32());
        Assert.Equal(3, body.GetProperty("turnCount").GetInt32());
        Assert.Equal("1033", body.GetProperty("locale").GetString());
        Assert.Equal(2, conversations[0].Exchanges.Count);
        Assert.Equal(EvaluationConversationSource.Generated, conversations[0].Source);
    }

    [Fact]
    public async Task GenerateConversations_FailsWhenTheServiceReturnsNoConversationArray()
    {
        var (adapter, _, _) = Create("{}");

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.GenerateConversationsAsync(Context(), BotId, 10, 3, "1033", CancellationToken.None));
    }

    [Fact]
    public async Task GenerateConversations_FailsWhenTheServiceAnswersNotFound()
    {
        var handler = new RecordingHandler(["{}"], HttpStatusCode.NotFound);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.GenerateConversationsAsync(Context(), BotId, 10, 3, "1033", CancellationToken.None));
    }

    [Fact]
    public async Task GetTestSet_RebuildsConversationsFromTheStoredActivities()
    {
        var (adapter, _, _) = Create(
            "{\"testComponents\":[{\"component\":{\"id\":\"case-1\",\"definition\":{\"$kind\":\"MultiTurnEvaluationCase\",\"source\":\"Imported\",\"activities\":["
                + Activity("asked", "user") + "," + Activity("replied", "agent") + "]}}}]}");

        var detail = await adapter.GetTestSetAsync(Context(), BotId, Summary(), CancellationToken.None);

        Assert.Equal("asked", detail!.Conversations[0].Exchanges[0].Question);
        Assert.Equal("replied", detail.Conversations[0].Exchanges[0].Response);
        Assert.Equal(["case-1"], detail.ConversationComponentIds);
    }

    [Fact]
    public async Task GetTestSet_ReadsTheBareComponentArrayTheServiceReturns()
    {
        var (adapter, _, _) = Create(
            "[{\"$kind\":\"TestCaseComponent\",\"id\":\"case-1\",\"parentBotComponentId\":\"testset-1\",\"definition\":{\"$kind\":\"MultiTurnEvaluationCase\",\"source\":\"Imported\",\"activities\":["
                + Activity("asked", "user") + "," + Activity("replied", "agent") + "]}},"
                + "{\"$kind\":\"TestCaseComponent\",\"id\":\"case-2\",\"parentBotComponentId\":\"testset-1\",\"definition\":{\"$kind\":\"MultiTurnEvaluationCase\",\"source\":\"Generated\",\"activities\":["
                + Activity("second", "user") + "]}},"
                + "{\"$kind\":\"TestCaseComponent\",\"id\":\"testset-1\",\"definition\":{\"$kind\":\"EvaluationSet\",\"graders\":[{\"$kind\":\"GeneralQualityGrader\"}]}}]");

        var detail = await adapter.GetTestSetAsync(Context(), BotId, Summary(), CancellationToken.None);

        Assert.Equal(["case-1", "case-2"], detail!.ConversationComponentIds);
        Assert.Equal([1, 2], detail.Conversations.Select(conversation => conversation.Number));
        Assert.Equal("asked", detail.Conversations[0].Exchanges[0].Question);
        Assert.Equal("replied", detail.Conversations[0].Exchanges[0].Response);
        Assert.Equal(EvaluationConversationSource.Generated, detail.Conversations[1].Source);
        Assert.Null(detail.Conversations[1].Exchanges[0].Response);
    }

    [Fact]
    public async Task GetTestSet_CarriesTheSummaryTheCallerAlreadyResolved()
    {
        var (adapter, _, _) = Create("[]");

        var detail = await adapter.GetTestSetAsync(Context(), BotId, Summary(), CancellationToken.None);

        Assert.Equal("Nightly", detail!.Summary.DisplayName);
        Assert.Equal(TestSetId, detail.Summary.TestSetId);
    }

    [Fact]
    public async Task GetTestSet_ReadsOnlyTheDetailsRoute()
    {
        var (adapter, handler, _) = Create("[]");

        var detail = await adapter.GetTestSetAsync(Context(), BotId, Summary(), CancellationToken.None);

        Assert.Empty(detail!.Conversations);
        Assert.Equal($"{Gateway}{Base}/testsets/{TestSetId}/details?ApplyV2Migration=true&cli=true", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task AnyVerb_SurfacesTheServiceFailureMessage()
    {
        var handler = new RecordingHandler(["""{"Error":{"Code":"internalservererror","Message":"System Error occurred."}}"""], HttpStatusCode.InternalServerError);
        var adapter = new MakerEvaluationAdapter(new StubTokens(), CloudMap, CoreServicesClusterCategory.Prod, () => handler);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListRunsAsync(Context(), BotId, 20, CancellationToken.None));

        Assert.Contains("System Error occurred.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyVerb_FailsWhenTheEnvironmentAdvertisesNoGateway()
    {
        var (adapter, _, _) = Create("[]");

        await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ListRunsAsync(Context(gateway: null), BotId, 20, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com.attacker.example/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://notisland.powerapps.com/", CoreServicesClusterCategory.Prod)]
    [InlineData("http://powervamg.us-il301.gateway.prod.island.powerapps.com/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com:8443/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://island.powerapps.com.attacker.example/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://api.powerplatform.com/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://powervamg.cn-il101.gateway.prod.island.powerapps.cn/", CoreServicesClusterCategory.Prod)]
    [InlineData(Gateway, CoreServicesClusterCategory.Mooncake)]
    [InlineData("https://powervamg.us-il101.gateway.gov.island.powerapps.us/", CoreServicesClusterCategory.Test)]
    public async Task AnyVerb_RefusesAGatewayThisCloudDoesNotServe(string gateway, CoreServicesClusterCategory cluster)
    {
        var (adapter, handler, tokens) = Create(CloudMap, cluster, "[]");

        await Assert.ThrowsAsync<EndpointTrustException>(() => adapter.ListRunsAsync(Context(gateway), BotId, 20, CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Null(tokens.RequestedScopes);
    }

    [Theory]
    [InlineData("https://island.powerapps.com/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://powervamg.us-il106.gateway.test.island.powerapps.com/", CoreServicesClusterCategory.Prod)]
    [InlineData("https://island.powerapps.cn/", CoreServicesClusterCategory.Mooncake)]
    public async Task AnyVerb_SendsToAnyGatewayOnTheDomainOfTheClustersControlPlane(string gateway, CoreServicesClusterCategory cluster)
    {
        var (adapter, handler, _) = Create(CloudMap, cluster, "[]");

        await adapter.ListRunsAsync(Context(gateway), BotId, 20, CancellationToken.None);

        Assert.StartsWith($"{gateway}{Base}", Assert.Single(handler.Requests).Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudInstance.Public, CoreServicesClusterCategory.Prod, "The environment 'env-1' Copilot Studio agent-management endpoint 'https://attacker.example.com' is not a trusted Public Power Platform host. Trusted hosts end with: island.powerapps.com.")]
    [InlineData(CloudInstance.China, CoreServicesClusterCategory.Mooncake, "The environment 'env-1' Copilot Studio agent-management endpoint 'https://attacker.example.com' is not a trusted China Power Platform host. Trusted hosts end with: island.powerapps.cn.")]
    public async Task AnyVerb_NamesTheEnvironmentAndTheTrustedDomainOfARefusedGateway_WithoutEchoingThePath(CloudInstance cloud, CoreServicesClusterCategory cluster, string message)
    {
        var (adapter, _, _) = Create(PowerPlatformCloudMap.ForCloud(cloud), cluster, "[]");

        var failure = await Assert.ThrowsAsync<EndpointTrustException>(() => adapter.ListRunsAsync(Context("https://attacker.example.com/secret-path"), BotId, 20, CancellationToken.None));

        Assert.Equal(message, failure.Message);
        Assert.Equal("Re-run `msagent env list` to refresh the environment inventory; if the endpoint is still untrusted the inventory response cannot be used and the selection must not proceed.", failure.Remediation);
    }

    private static EvaluationConversation Conversation(string question, string? response = null) =>
        new() { Number = 1, Exchanges = [new EvaluationExchange(question, response)] };

    private static EvaluationTestSetSummary Summary() =>
        new() { TestSetId = TestSetId, DisplayName = "Nightly", ConversationCount = 2 };

    private static string Activity(string text, string role) =>
        "{\"$kind\":\"MultiTurnActivity\",\"activity\":{\"$kind\":\"Message\","
        + "\"text\":[{\"$kind\":\"TemplateLine\",\"segments\":[{\"$kind\":\"TextSegment\",\"value\":\"" + text + "\"}]}],"
        + "\"value\":{\"$kind\":\"ValueExpression\",\"literalValue\":{\"from\":{\"role\":\"" + role + "\"}}}}}";

    private static string RoleOf(JsonElement activity) =>
        activity.GetProperty("activity").GetProperty("value").GetProperty("literalValue").GetProperty("from").GetProperty("role").GetString()!;

    private static string TextOf(JsonElement activity) =>
        activity.GetProperty("activity").GetProperty("text")[0].GetProperty("segments")[0].GetProperty("value").GetString()!;

    private static string SchemaNameOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("testComponents")[0].GetProperty("component").GetProperty("schemaName").GetString()!;

    private sealed record RecordedRequest(HttpMethod Method, string Url, string? Body, HttpRequestMessage Message)
    {
        public string? Header(string name) => Message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        private readonly HttpStatusCode _status;

        private Func<string, string>? _responder;

        public RecordingHandler(IEnumerable<string> responses, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses = new Queue<string>(responses);
            _status = status;
        }

        public List<RecordedRequest> Requests { get; } = [];

        public void Respond(Func<string, string> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body, request));

            var payload = _responder is not null && body is not null
                ? _responder(body)
                : _responses.Count > 0 ? _responses.Dequeue() : "{}";

            return new HttpResponseMessage(_status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubTokens : ITokenProvider
    {
        public IReadOnlyList<string>? RequestedScopes { get; private set; }

        public string? RequestedTenantId { get; private set; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken)
        {
            RequestedScopes = scopes;
            RequestedTenantId = targetTenantId;

            return Task.FromResult(new TokenAcquisitionResult { AccessToken = "stub-token", ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) });
        }
    }
}
