using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentAdapterTests
{
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string EntityTag = "\"0x8DE2\"";
    private const string AgentRoute = $"https://env-1.api.powerplatform.com/agenthosting/agents/{Agent}";

    private const string AgentBody =
        """
        {
          "id": "44444444-4444-4444-4444-444444444444",
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "environmentId": "22222222-2222-2222-2222-222222222222",
          "eTag": "\"0x8DE2\"",
          "displayName": "Contoso Agent",
          "description": "Answers questions.",
          "agentType": "CodeAgent",
          "language": 1,
          "languageVersion": "10",
          "repositoryId": "55555555-5555-5555-5555-555555555555",
          "repositoryBindingStatus": "Bound",
          "createdTime": "2026-11-01T10:00:00Z",
          "changedTime": "2026-11-02T10:00:00Z",
          "state": "Active",
          "isDeleting": true,
          "tags": ["ignored"]
        }
        """;

    [Fact]
    public async Task Get_ReadsTheAgentOnTheDataPlaneWithTheReadPermission()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, AgentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var result = await Adapter(handler, tokens).GetAsync(AgentRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{AgentRoute}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Null(sent.IfMatch);
        Assert.Equal(
            new AmsAgentReadResult { AgentId = Agent, TenantId = Tenant, EnvironmentId = Home, AgentType = "CodeAgent", DisplayName = "Contoso Agent", Description = "Answers questions.", ETag = EntityTag, State = "Active", IsDeleting = true, RepositoryId = "55555555-5555-5555-5555-555555555555", RepositoryBindingStatus = "Bound", Language = "DotNet", LanguageVersion = "10", CreatedTime = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), ChangedTime = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero) },
            result);
    }

    [Fact]
    public async Task List_ReadsEveryPageOnItsOwnRouteIncludingAgentsOfOtherTypes()
    {
        var handler = new CapturingPackageHandler(request => FakeHttpResponse.Json(HttpStatusCode.OK, request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
            ? $$"""{ "value": [{ "id": "{{Revision}}", "tenantId": "{{Tenant}}", "environmentId": "{{Home}}", "displayName": "Copilot", "agentType": "CopilotStudio" }] }"""
            : $$"""{ "value": [{ "id": "{{Agent}}", "tenantId": "{{Tenant}}", "environmentId": "{{Home}}", "displayName": "Contoso Agent", "agentType": 0, "state": "Active", "changedTime": "2026-11-02T10:00:00Z" }], "nextLink": "https://elsewhere.example/agenthosting/agents?%24skiptoken=next&api-version=1" }"""));

        var result = await Adapter(handler, ReadTokens()).ListAsync(DataPlane(), default);

        Assert.Equal(["https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1&$top=100", "https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1&$top=100&$skiptoken=next"], handler.Requests.Select(request => request.Uri.AbsoluteUri));
        Assert.Equal([("Contoso Agent", "CodeAgent"), ("Copilot", "CopilotStudio")], result.Select(agent => (agent.DisplayName!, agent.AgentType)));
    }

    [Theory]
    [InlineData("Renamed", null, """{"displayName":"Renamed"}""")]
    [InlineData(null, "", """{"description":""}""")]
    [InlineData("Renamed", "New text", """{"displayName":"Renamed","description":"New text"}""")]
    public async Task Update_PatchesOnlyTheGivenFieldsWithTheEntityTagItRead(string? displayName, string? description, string expectedBody)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, AgentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        await Adapter(handler, tokens).UpdateAsync(new AmsAgentUpdateTransportRequest { ServiceTarget = DataPlane(), AgentId = Agent, ETag = EntityTag, DisplayName = displayName, Description = description }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal($"{AgentRoute}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(EntityTag, sent.IfMatch);
        Assert.Equal(expectedBody, JsonNode.Parse(sent.Body)!.ToJsonString());
    }

    [Fact]
    public async Task CascadeDelete_PostsTheDeleteOnTheControlPlaneAndReadsOnlyWhatWasRemoved()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, $$"""
            {
              "agentId": "{{Agent}}",
              "deployments": [{ "deploymentId": "{{Deployment}}", "deploymentName": "dev", "environmentId": "{{Target}}", "deploymentType": "Dev", "botName": "ams-bot-1", "botDeleted": true }, { "deploymentId": "{{Revision}}", "deploymentName": "test", "environmentId": "{{Target}}", "deploymentType": "Test", "botName": null, "botDeleted": false }],
              "keptBots": [{ "deploymentId": "{{Commit}}", "deploymentName": "old", "environmentId": "{{Target}}", "botName": "ams-old", "botDeleted": true }],
              "runtimeCopiesKept": [{ "deploymentId": "{{Deployment}}", "environmentId": "{{Target}}" }],
              "ppmiRegistrationsKept": [{ "deploymentId": "{{Deployment}}", "managedIdentityId": "mi-1" }]
            }
            """));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsDelete };

        var result = await Adapter(handler, tokens).CascadeDeleteAsync(new AmsAgentCascadeDeleteTransportRequest { ServiceTarget = ControlPlane(), AgentId = Agent, ETag = EntityTag }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/cascadeDelete?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(EntityTag, sent.IfMatch);
        Assert.Empty(sent.Body);
        Assert.Equal(Agent, result.AgentId);
        Assert.Equal(
            [new AgentAmsRemovedDeployment { DeploymentId = Deployment, DeploymentName = "dev", EnvironmentId = Target, DeploymentType = "Dev", BotName = "ams-bot-1", BotDeleted = true }, new AgentAmsRemovedDeployment { DeploymentId = Revision, DeploymentName = "test", EnvironmentId = Target, DeploymentType = "Test", BotDeleted = false }],
            result.Deployments);
        Assert.Equal([new AgentAmsRemovedDeployment { DeploymentId = Commit, DeploymentName = "old", EnvironmentId = Target, BotName = "ams-old", BotDeleted = true }], result.KeptBots);
    }

    [Fact]
    public async Task CascadeDelete_KeptListsInAnyShape_DoNotFailTheDelete()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, $$"""{ "agentId": "{{Agent}}", "runtimeCopiesKept": {}, "ppmiRegistrationsKept": "pending" }"""));

        var result = await Adapter(handler, AgentOperationTokens()).CascadeDeleteAsync(new AmsAgentCascadeDeleteTransportRequest { ServiceTarget = ControlPlane(), AgentId = Agent, ETag = EntityTag }, default);

        Assert.Equal(Agent, result.AgentId);
        Assert.Empty(result.Deployments);
        Assert.Empty(result.KeptBots);
    }

    [Fact]
    public async Task ListPackages_ReadsEveryPageOfTheAgentsUploads()
    {
        var handler = new CapturingPackageHandler(request => FakeHttpResponse.Json(HttpStatusCode.OK, request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
            ? $$"""{ "value": [{ "packageRevisionId": "{{Deployment}}" }] }"""
            : $$"""{ "value": [{ "packageRevisionId": "{{Revision}}", "commitId": "{{Commit.ToUpperInvariant()}}", "sha256": "{{new string('A', 64)}}", "sizeInBytes": 2048, "createdTime": "2026-11-03T10:00:00Z" }], "nextLink": "https://env-1.api.powerplatform.com/agenthosting/agents/{{Agent}}/packages?api-version=1&$skiptoken=2" }"""));

        var result = await Adapter(handler, ReadTokens()).ListPackagesAsync(AgentRequest(), default);

        Assert.Equal([$"{AgentRoute}/packages?api-version=1&$top=100", $"{AgentRoute}/packages?api-version=1&$top=100&$skiptoken=2"], handler.Requests.Select(request => request.Uri.AbsoluteUri));
        Assert.Equal(
            [new AmsAgentPackage { PackageRevisionId = Revision, CommitId = Commit, Sha256 = new string('a', 64), SizeInBytes = 2048, CreatedTime = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero) }, new AmsAgentPackage { PackageRevisionId = Deployment }],
            result);
    }

    [Theory]
    [InlineData("get", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("list", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("packages", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("update", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("cascadeDelete", PowerPlatformApiPermissions.AppsRead + " " + PowerPlatformApiPermissions.AppsUpdate)]
    public async Task TokenWithoutTheOperationPermission_SendsNothing(string operation, string scopes)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, AgentBody));

        await Assert.ThrowsAsync<TokenClaimException>(() => Invoke(Adapter(handler, new FakePpapiTokenProvider { Scopes = scopes }), operation));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("cascadeDelete", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.NotFound, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("packages", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("packages", HttpStatusCode.NotFound, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.NotImplemented, "NotImplemented", "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.Conflict, "AgentConcurrencyConflict", "agent-changed", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.Conflict, "AgentConcurrencyConflict", "agent-changed", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.NotFound, "AgentNotFound", "agent-not-found", ExitCodes.Failure)]
    [InlineData("get", HttpStatusCode.NotFound, "AgentNotFound", "agent-not-found", ExitCodes.Failure)]
    [InlineData("packages", HttpStatusCode.NotFound, "AgentNotFound", "agent-not-found", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.Conflict, "AgentBotServiceConflict", "agent-bot-service-conflict", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.BadGateway, "AgentBotServiceDeletionFailed", "agent-bot-service-deletion-failed", ExitCodes.Failure)]
    [InlineData("cascadeDelete", HttpStatusCode.GatewayTimeout, "AgentBotServiceOutcomeUnknown", "agent-bot-service-deletion-failed", ExitCodes.Failure)]
    public async Task KnownRefusals_AreExplainedOnceWithCorrelationAndNoServiceDetail(string operation, HttpStatusCode status, string? code, string errorKind, int exitCode)
    {
        var response = code is null ? new HttpResponseMessage(status) : FakeHttpResponse.Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, AgentOperationTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(exitCode, exception.ExitCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cascadeDelete", "AgentDeployInProgress", "agent-deploy-in-progress", ExitCodes.Failure, "A deploy is still running. Run it again after 10:42 UTC.")]
    [InlineData("update", "AgentNameInUse", "agent-name-in-use", ExitCodes.Usage, $"The name 'Contoso' is already used by agent {Revision} in this environment.")]
    public async Task ConflictsWithAmsOwnMessage_ShowItMadeSafe(string operation, string code, string errorKind, int exitCode, string serviceMessage)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Conflict, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = serviceMessage + "\u001b[2J" } }.ToJsonString()));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, AgentOperationTokens()), operation));

        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(exitCode, exception.ExitCode);
        Assert.Contains(serviceMessage, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("list")]
    public async Task CodeLessNotFoundOnRoutesThatOlderServersServe_KeepsTheGenericMapping(string operation)
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, AgentOperationTokens()), operation));

        Assert.NotEqual(AmsRefusals.LifecycleUnavailableKind, exception.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("get", "{}")]
    [InlineData("get", """{"id":"a","tenantId":"t","environmentId":"e"}""")]
    [InlineData("get", """{"id":"a","tenantId":"t","environmentId":"e","agentType":{}}""")]
    [InlineData("get", """{"id":"a","tenantId":"t","environmentId":"e","agentType":"CodeAgent","isDeleting":"yes"}""")]
    [InlineData("get", """{"id":"a","tenantId":"t","environmentId":"e","agentType":"CodeAgent","createdTime":"private-response-detail"}""")]
    [InlineData("list", """{"value":[{"id":"a"}]}""")]
    [InlineData("list", "[]")]
    [InlineData("packages", """{"value":[{"commitId":"c"}]}""")]
    [InlineData("packages", """{"value":[{"packageRevisionId":"p","sizeInBytes":"big"}]}""")]
    [InlineData("cascadeDelete", "{}")]
    [InlineData("cascadeDelete", """{"agentId":"a","deployments":[{}]}""")]
    public async Task MalformedSuccess_IsRejectedWithoutServiceDetail(string operation, string body)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, AgentOperationTokens()), operation));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    private static Task Invoke(AmsAgentAdapter adapter, string operation) => operation switch
    {
        "get" => adapter.GetAsync(AgentRequest(), default),
        "list" => adapter.ListAsync(DataPlane(), default),
        "packages" => adapter.ListPackagesAsync(AgentRequest(), default),
        "update" => adapter.UpdateAsync(new AmsAgentUpdateTransportRequest { ServiceTarget = DataPlane(), AgentId = Agent, ETag = EntityTag, DisplayName = "Renamed" }, default),
        "cascadeDelete" => adapter.CascadeDeleteAsync(new AmsAgentCascadeDeleteTransportRequest { ServiceTarget = ControlPlane(), AgentId = Agent, ETag = EntityTag }, default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static AmsAgentAdapter Adapter(CapturingPackageHandler handler, FakePpapiTokenProvider tokens) => new(new HttpClient(handler), tokens);

    private static AmsAgentReadTransportRequest AgentRequest() => new() { ServiceTarget = DataPlane(), AgentId = Agent };

    private static AmsServiceTarget DataPlane() => new() { Endpoint = new Uri("https://env-1.api.powerplatform.com"), Audience = Audience, RouteForm = AmsRouteForm.EnvironmentHost, TenantId = Tenant, EnvironmentId = Home };

    private static AmsServiceTarget ControlPlane() => new() { Endpoint = new Uri("https://api.powerplatform.com"), Audience = Audience, RouteForm = AmsRouteForm.TenantHost, TenantId = Tenant, EnvironmentId = Home };

    private static FakePpapiTokenProvider ReadTokens() => new() { Scopes = PowerPlatformApiPermissions.AppsRead };

    private static FakePpapiTokenProvider AgentOperationTokens() => new() { Scopes = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate} {PowerPlatformApiPermissions.AppsDelete}" };
}
