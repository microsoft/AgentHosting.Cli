using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsDeploymentAdapterTests
{
    private const string ControlPlaneEndpoint = "https://api.powerplatform.com";
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Entra = "88888888-8888-8888-8888-888888888888";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string EntityTag = "\"0x8DE1\"";
    private const string TunnelEndpoint = "https://contoso-3978.usw2.devtunnels.ms/api/messages";
    private const string DeploymentsRoute = $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments";
    private static readonly string Digest = new('a', 64);

    private static readonly string DeploymentBody =
        $$"""
        {
          "id": "{{Deployment}}",
          "agentId": "{{Agent}}",
          "tenantId": "{{Tenant}}",
          "homeEnvironmentId": "{{Home}}",
          "environmentId": "{{Target}}",
          "eTag": "\"0x8DE1\"",
          "state": "Deployed",
          "packageRevisionId": "{{Revision}}",
          "packageSha256": "{{Digest.ToUpperInvariant()}}",
          "packageSizeInBytes": 42,
          "commitId": "{{Commit.ToUpperInvariant()}}",
          "agentType": "CodeAgent",
          "deploymentType": "Dev",
          "deploymentName": "dev-proof",
          "entraAppId": "{{Entra}}",
          "entraAppIdType": "AppReg",
          "language": "DotNet",
          "languageVersion": "10",
          "endpoint": "https://runtime.example/api/messages",
          "isCustomEndpoint": false,
          "isQuarantined": false,
          "generation": 3
        }
        """;

    private static readonly string BotServiceBody =
        $$"""
        {
          "resourceId": "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/ams-bot-1",
          "name": "ams-bot-1",
          "msaAppId": "{{Entra}}",
          "msaAppType": "SingleTenant",
          "msaAppTenantId": "{{Tenant}}",
          "location": "global",
          "sku": "F0",
          "endpoint": "{{TunnelEndpoint}}"
        }
        """;

    private static readonly AgentDeploymentBotService ExpectedBotService = new()
    {
        ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/ams-bot-1",
        Name = "ams-bot-1",
        MsaAppId = Entra,
        MsaAppType = "SingleTenant",
        MsaAppTenantId = Tenant,
        Location = "global",
        Sku = "F0",
        Endpoint = TunnelEndpoint,
    };

    [Fact]
    public async Task Read_GetsTheDeploymentOnTheControlPlaneWithTheReadPermission()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var result = await Adapter(handler, tokens).ReadAsync(Reference(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"{DeploymentsRoute}/{Deployment}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Null(sent.IfMatch);
        Assert.Empty(sent.Body);
        Assert.Equal([Audience], tokens.Resources);
        Assert.Equal(sent.ClientRequestId, result.ClientRequestId);
        Assert.Equal(Deployment, result.DeploymentId);
        Assert.Equal(EntityTag, result.ETag);
        Assert.Equal(AmsDeploymentResult.DeployedState, result.State);
        Assert.Equal(Revision, result.PackageRevisionId);
        Assert.Equal(Digest, result.PackageSha256);
        Assert.Equal(42, result.PackageSizeInBytes);
        Assert.Equal(Commit, result.CommitId);
        Assert.Equal("Dev", result.DeploymentType);
        Assert.Equal("AppReg", result.EntraIdType);
        Assert.Equal("https://runtime.example/api/messages", result.RuntimeEndpoint);
        Assert.Null(result.RequestedEndpoint);
        Assert.False(result.IsCustomEndpoint);
        Assert.Null(result.BotService);
    }

    [Fact]
    public async Task DirectTargets_UseTheTenantAndEnvironmentScopedRoutes()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));

        await Adapter(handler, ReadAndUpdateTokens()).DeployAsync(DeployRequest() with { ServiceTarget = ServiceTargetAt("https://ams-alias.example", AmsRouteForm.Direct) }, default);

        Assert.Equal($"https://ams-alias.example/agenthosting/tenants/{Tenant}/environments/{Home}/agents/{Agent}/deployments/{Deployment}/deploy?api-version=1", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task List_ReadsEveryPageOnItsOwnRouteAndTheKeptBotsOfTheFirstPage()
    {
        var notDeployed = NotDeployedBody("78787878-7878-7878-7878-787878787878", "test-proof");
        var handler = new CapturingPackageHandler(request => FakeHttpResponse.Json(HttpStatusCode.OK, request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
            ? $$"""{ "value": [{{notDeployed}}], "keptBots": [{ "deploymentId": "ignored" }] }"""
            : $$"""{ "value": [{{DeploymentBody}}], "nextLink": "https://elsewhere.example/agenthosting/agents/{{Agent}}/deployments?api-version=1&%24skiptoken=page%202", "keptBots": [{ "deploymentId": "{{Revision}}", "deploymentName": "old", "environmentId": "{{Target}}", "deploymentType": "Test", "entraAppId": "{{Entra}}", "botName": "ams-old", "deletedTime": "2026-11-01T10:00:00Z" }] }"""));

        var result = await Adapter(handler, ReadAndUpdateTokens()).ListAsync(AgentReference(), default);

        Assert.Equal([$"{DeploymentsRoute}?api-version=1&$top=100", $"{DeploymentsRoute}?api-version=1&$top=100&$skiptoken=page%202"], handler.Requests.Select(request => request.Uri.AbsoluteUri));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal([Deployment, "78787878-7878-7878-7878-787878787878"], result.Deployments.Select(deployment => deployment.DeploymentId));
        Assert.Equal(AmsDeploymentResult.NotDeployedState, result.Deployments[1].State);
        var kept = Assert.Single(result.KeptBots);
        Assert.Equal(new AgentAmsKeptBot { DeploymentId = Revision, DeploymentName = "old", EnvironmentId = Target, DeploymentType = "Test", EntraAppId = Entra, BotName = "ams-old", DeletedTime = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero) }, kept);
    }

    [Fact]
    public async Task List_WithoutKeptBots_HasNone()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, """{ "value": [] }"""));

        var result = await Adapter(handler, ReadAndUpdateTokens()).ListAsync(AgentReference(), default);

        Assert.Empty(result.Deployments);
        Assert.Empty(result.KeptBots);
    }

    [Theory]
    [InlineData("https://api.powerplatform.com/deployments?api-version=1&$skiptoken=same")]
    [InlineData("https://api.powerplatform.com/deployments?api-version=1")]
    [InlineData("https://api.powerplatform.com/deployments?api-version=1&$skiptoken=")]
    public async Task List_ANextLinkThatRepeatsOrHasNoSkipToken_IsRejected(string nextLink)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, $$"""{ "value": [], "nextLink": "{{nextLink}}" }"""));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).ListAsync(AgentReference(), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Equal(nextLink.EndsWith("same", StringComparison.Ordinal) ? 2 : 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Create_SendsTheRecordOnlyBodyWithEnumNamesAndNoPackageOrEndpoint()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Created, NotDeployedBody(Deployment, "dev-proof")));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).CreateAsync(CreateRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentsRoute}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Null(sent.IfMatch);
        Assert.Equal(
            JsonNode.Parse($$"""{ "agentType": "CodeAgent", "deploymentType": "Test", "deploymentName": "dev-proof", "environmentId": "{{Target}}", "audience": "{{Entra}}", "entraAppIdType": "Blueprint", "language": "Python", "languageVersion": "3.12" }""")!.ToJsonString(),
            JsonNode.Parse(sent.Body)!.ToJsonString());
        Assert.Equal(AmsDeploymentResult.NotDeployedState, result.State);
        Assert.Null(result.PackageRevisionId);
        Assert.Null(result.RuntimeEndpoint);
        Assert.Null(result.BotService);
        Assert.Equal(sent.ClientRequestId, result.ClientRequestId);
    }

    [Fact]
    public async Task Create_WithARequestedEndpoint_SendsItAndParsesTheDevEndpointRecord()
    {
        var body = JsonNode.Parse(NotDeployedBody(Deployment, "dev-proof"))!.AsObject();
        body["isCustomEndpoint"] = true;
        body["requestedEndpoint"] = TunnelEndpoint;
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Created, body.ToJsonString()));

        var result = await Adapter(handler, ReadAndUpdateTokens()).CreateAsync(CreateRequest() with { RequestedEndpoint = TunnelEndpoint }, default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(TunnelEndpoint, document.RootElement.GetProperty("requestedEndpoint").GetString());
        Assert.False(document.RootElement.TryGetProperty("endpoint", out _));
        Assert.True(result.IsCustomEndpoint);
        Assert.Equal(TunnelEndpoint, result.RequestedEndpoint);
        Assert.Null(result.RuntimeEndpoint);
    }

    [Theory]
    [InlineData("renamed", null, """{"deploymentName":"renamed"}""")]
    [InlineData(null, TunnelEndpoint, $$"""{"requestedEndpoint":"{{TunnelEndpoint}}"}""")]
    [InlineData("renamed", TunnelEndpoint, $$"""{"deploymentName":"renamed","requestedEndpoint":"{{TunnelEndpoint}}"}""")]
    public async Task Update_PatchesOnlyTheGivenFieldsWithTheEntityTagItRead(string? deploymentName, string? requestedEndpoint, string expectedBody)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        await Adapter(handler, tokens).UpdateAsync(new AmsDeploymentUpdateTransportRequest { ServiceTarget = ControlPlane(), AgentId = Agent, DeploymentId = Deployment, ETag = EntityTag, DeploymentName = deploymentName, RequestedEndpoint = requestedEndpoint }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal($"{DeploymentsRoute}/{Deployment}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(EntityTag, sent.IfMatch);
        Assert.Equal(expectedBody, JsonNode.Parse(sent.Body)!.ToJsonString());
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData(Commit, $$"""{"commitId":"{{Commit}}"}""")]
    public async Task Deploy_PostsTheDeployActionWithTheEntityTagItRead(string? commitId, string expectedBody)
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        body["botService"] = JsonNode.Parse(BotServiceBody);
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).DeployAsync(DeployRequest() with { CommitId = commitId }, default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{DeploymentsRoute}/{Deployment}/deploy?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(EntityTag, sent.IfMatch);
        Assert.Equal(expectedBody, JsonNode.Parse(sent.Body)!.ToJsonString());
        Assert.Equal(ExpectedBotService, result.BotService);
    }

    [Fact]
    public async Task Delete_SendsOneBodylessDeleteWithTheEntityTagItRead()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        await Adapter(handler, tokens).DeleteAsync(DeleteRequest(), default);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal($"{DeploymentsRoute}/{Deployment}?api-version=1", sent.Uri.AbsoluteUri);
        Assert.Equal(EntityTag, sent.IfMatch);
        Assert.Empty(sent.Body);
        Assert.Null(sent.ContentType);
        Assert.Equal([Tenant], tokens.Tenants);
    }

    [Theory]
    [InlineData("read", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("list", PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("create", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("update", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("deploy", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("delete", PowerPlatformApiPermissions.AppsRead)]
    [InlineData("deploy", "")]
    public async Task TokenWithoutTheOperationPermission_SendsNothing(string operation, string scopes)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));

        await Assert.ThrowsAsync<TokenClaimException>(() => Invoke(Adapter(handler, new FakePpapiTokenProvider { Scopes = scopes }), operation));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("deploy", HttpStatusCode.NotImplemented, "NotImplemented", "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.NotFound, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("list", HttpStatusCode.NotFound, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("list", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.NotImplemented, null, "deployment-lifecycle-unavailable", ExitCodes.Failure)]
    [InlineData("delete", HttpStatusCode.NotImplemented, null, "deployment-delete-unavailable", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "AgentConcurrencyConflict", "deployment-changed", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.Conflict, "agentconcurrencyconflict", "deployment-changed", ExitCodes.Failure)]
    [InlineData("delete", HttpStatusCode.Conflict, "AgentConcurrencyConflict", "deployment-changed", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "AgentPackageRequired", "package-required", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.NotFound, "AgentPackageCommitNotFound", "commit-not-found", ExitCodes.Usage)]
    [InlineData("deploy", HttpStatusCode.Conflict, "AgentPackageNotDeployedToTest", "prod-needs-test", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "AgentDeleting", "agent-deleting", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.Conflict, "AgentDeleting", "agent-deleting", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "PreconditionFailed", "agent-deployment-conflict", ExitCodes.Failure)]
    [InlineData("create", HttpStatusCode.Conflict, "PreconditionFailed", "agent-deployment-conflict", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "AgentBotServiceAppIdInUse", "agent-bot-service-app-id-in-use", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.Conflict, "DeploymentDeleting", "deployment-deleting", ExitCodes.Failure)]
    [InlineData("update", HttpStatusCode.Conflict, "DeploymentDeleting", "deployment-deleting", ExitCodes.Failure)]
    [InlineData("read", HttpStatusCode.NotFound, "DeploymentNotFound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.NotFound, "DeploymentNotFound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("delete", HttpStatusCode.NotFound, "DeploymentNotFound", "deployment-not-found", ExitCodes.Failure)]
    [InlineData("list", HttpStatusCode.NotFound, "AgentNotFound", "agent-not-found", ExitCodes.Failure)]
    [InlineData("deploy", HttpStatusCode.NotFound, "AgentNotFound", "agent-not-found", ExitCodes.Failure)]
    public async Task KnownRefusals_AreExplainedOnceWithCorrelationAndNoServiceDetail(string operation, HttpStatusCode status, string? code, string errorKind, int exitCode)
    {
        var response = code is null ? new HttpResponseMessage(status) : FakeHttpResponse.Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(exitCode, exception.ExitCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create", HttpStatusCode.Conflict, "DeploymentNameInUse", "deployment-name-in-use")]
    [InlineData("update", HttpStatusCode.Conflict, "DeploymentNameInUse", "deployment-name-in-use")]
    [InlineData("create", HttpStatusCode.BadRequest, "InvalidAgentFieldValue", "invalid-field-value")]
    [InlineData("deploy", HttpStatusCode.BadRequest, "InvalidAgentFieldValue", "invalid-field-value")]
    public async Task NameAndFieldRefusals_ShowAmsOwnMessageMadeSafe(string operation, HttpStatusCode status, string code, string errorKind)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(status, $$$"""{"error":{"code":"{{{code}}}","message":"The name 'dev' is used by deployment {{{Deployment}}}.\u001b[31m"}}"""));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        Assert.Equal(errorKind, exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Contains($"The name 'dev' is used by deployment {Deployment}.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("read", HttpStatusCode.NotFound)]
    [InlineData("create", HttpStatusCode.NotFound)]
    [InlineData("update", HttpStatusCode.NotFound)]
    [InlineData("delete", HttpStatusCode.NotFound)]
    [InlineData("deploy", HttpStatusCode.ServiceUnavailable)]
    public async Task CodeLessFailuresOnRoutesThatOlderServersServe_KeepTheGenericMapping(string operation, HttpStatusCode status)
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(status));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.NotEqual(AmsRefusals.LifecycleUnavailableKind, exception.ErrorKind);
        Assert.Contains(sent.Method.Method, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false, AmsDeploymentResult.DeployedState)]
    [InlineData(null, true, AmsDeploymentResult.DeletingState)]
    [InlineData("\"Deploying\"", false, AmsDeploymentResult.DeployingState)]
    [InlineData("3", true, AmsDeploymentResult.DeletingState)]
    [InlineData("\"notdeployed\"", false, AmsDeploymentResult.NotDeployedState)]
    public async Task Read_TakesTheStateOrReadsAnOlderRecordAsDeployedOrDeleting(string? state, bool isDeleting, string expected)
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        body.Remove("state");
        if (state is not null)
        {
            body["state"] = JsonNode.Parse(state);
        }

        body["isDeleting"] = isDeleting;
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));

        var result = await Adapter(handler, ReadAndUpdateTokens()).ReadAsync(Reference(), default);

        Assert.Equal(expected, result.State);
        Assert.Equal(isDeleting, result.IsDeleting);
    }

    [Fact]
    public async Task Read_ParsesTheDevEndpointFieldsAndNumberedEnums()
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        body.Remove("packageRevisionId");
        body.Remove("packageSha256");
        body.Remove("packageSizeInBytes");
        body.Remove("commitId");
        body["isCustomEndpoint"] = true;
        body["requestedEndpoint"] = TunnelEndpoint;
        body["managedIdentityId"] = "mi-1";
        body["deploymentType"] = 2;
        body["entraAppIdType"] = 1;
        body["language"] = 3;
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));

        var result = await Adapter(handler, ReadAndUpdateTokens()).ReadAsync(Reference(), default);

        Assert.True(result.IsCustomEndpoint);
        Assert.Null(result.PackageRevisionId);
        Assert.Equal("https://runtime.example/api/messages", result.RuntimeEndpoint);
        Assert.Equal(TunnelEndpoint, result.RequestedEndpoint);
        Assert.Equal("mi-1", result.ManagedIdentityId);
        Assert.Equal("Prod", result.DeploymentType);
        Assert.Equal("Blueprint", result.EntraIdType);
        Assert.Equal("Node", result.Language);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("absent")]
    [InlineData("full")]
    [InlineData("required-only")]
    public async Task Read_ParsesTheBotServiceBinding(string shape)
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        switch (shape)
        {
            case "null":
                body["botService"] = null;
                break;
            case "full":
                body["botService"] = JsonNode.Parse(BotServiceBody);
                break;
            case "required-only":
                body["botService"] = new JsonObject { ["resourceId"] = ExpectedBotService.ResourceId, ["name"] = ExpectedBotService.Name, ["msaAppId"] = Entra, ["msaAppType"] = null, ["msaAppTenantId"] = "", ["location"] = " " };
                break;
        }

        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));

        var result = await Adapter(handler, ReadAndUpdateTokens()).ReadAsync(Reference(), default);

        var expected = shape switch
        {
            "full" => ExpectedBotService,
            "required-only" => new AgentDeploymentBotService { ResourceId = ExpectedBotService.ResourceId, Name = ExpectedBotService.Name, MsaAppId = Entra },
            _ => null,
        };
        Assert.Equal(expected, result.BotService);
    }

    [Theory]
    [InlineData("\"private-response-detail\"")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"name":"private-response-detail","msaAppId":"a"}""")]
    [InlineData("""{"resourceId":"private-response-detail","name":"n"}""")]
    [InlineData("""{"resourceId":"private-response-detail","name":"n","msaAppId":" "}""")]
    [InlineData("""{"resourceId":"private-response-detail","name":"n","msaAppId":"a","endpoint":{}}""")]
    public async Task Deploy_AMalformedBotServiceBinding_IsRejected(string binding)
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        body["botService"] = JsonNode.Parse(binding);
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).DeployAsync(DeployRequest(), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_IsRejectedWithoutASecondCredentialBearingRequest()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Redirect(HttpStatusCode.TemporaryRedirect, "https://attacker.example/collect"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).DeployAsync(DeployRequest(), default));

        Assert.Equal("redirect-rejected", exception.ErrorKind);
        Assert.Equal("api.powerplatform.com", Assert.Single(handler.Requests).Uri.Host);
        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("delete")]
    public async Task TransportFailure_IsIndeterminateAndSafe(string operation)
    {
        var handler = new CapturingPackageHandler(_ => throw new HttpRequestException("private-transport-detail"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("deploy-transport-failed", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-transport-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseBodyConnectionFailure_IsIndeterminateAndSafe()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new ThrowingReadContent(new HttpRequestException("private-response-detail")) });

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, ReadAndUpdateTokens()).CreateAsync(CreateRequest(), default));

        Assert.Equal("deploy-transport-failed", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> MalformedSuccessResponses
    {
        get
        {
            var corpus = new TheoryData<string, string>();
            foreach (var body in MalformedResponses())
            {
                corpus.Add("read", body);
                corpus.Add("deploy", body);
            }

            corpus.Add("list", "{}");
            corpus.Add("list", """{"value":{}}""");
            corpus.Add("list", """{"value":[{}]}""");
            corpus.Add("list", """{"value":[],"keptBots":[{}]}""");
            corpus.Add("list", """{"value":[],"keptBots":{}}""");
            return corpus;
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSuccessResponses))]
    public async Task MalformedSuccess_IsRejectedWithCorrelationAndNoServiceDetail(string operation, string body)
    {
        var response = FakeHttpResponse.Json(HttpStatusCode.OK, body);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Invoke(Adapter(handler, ReadAndUpdateTokens()), operation));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
    }

    private static IEnumerable<string> MalformedResponses()
    {
        string[] invalidRoots = ["", " ", "{", "null", "[]", "42", "true", "\"private-response-detail\"", "{}", """{"id":"private-response-detail"}"""];
        foreach (var body in invalidRoots)
        {
            yield return body;
        }

        yield return DeploymentBody[..(DeploymentBody.Length / 2)];
        yield return DeploymentBody + " private-response-detail";

        string[] required = ["id", "agentId", "tenantId", "homeEnvironmentId", "environmentId", "packageRevisionId", "packageSha256", "packageSizeInBytes", "agentType", "deploymentType", "deploymentName", "entraAppId", "entraAppIdType", "language", "languageVersion", "isCustomEndpoint", "isQuarantined"];
        var seed = JsonNode.Parse(DeploymentBody)!.AsObject();
        foreach (var field in required)
        {
            var missing = seed.DeepClone().AsObject();
            missing.Remove(field);
            missing["untrustedDetail"] = "private-response-detail";
            yield return missing.ToJsonString();

            string[] invalidValues = field switch
            {
                "deploymentType" or "entraAppIdType" or "language" => ["null", "{}", "-1", "0.5", "\"\"", "\"private-response-detail\""],
                "agentType" => ["null", "{}", "[]", "true", "0.5", "\"\""],
                "packageSizeInBytes" => ["null", "{}", "\"42\"", "0.5", "9223372036854775808"],
                "isCustomEndpoint" or "isQuarantined" => ["null", "0", "\"false\""],
                _ => ["null", "{}", "[]", "false", "42", "\"\"", "\" \\t \""],
            };
            foreach (var invalidValue in invalidValues)
            {
                var malformed = seed.DeepClone().AsObject();
                malformed[field] = JsonNode.Parse(invalidValue);
                malformed["untrustedDetail"] = "private-response-detail";
                yield return malformed.ToJsonString();
            }
        }
    }

    private static Task Invoke(AmsDeploymentAdapter adapter, string operation) => operation switch
    {
        "read" => adapter.ReadAsync(Reference(), default),
        "list" => adapter.ListAsync(AgentReference(), default),
        "create" => adapter.CreateAsync(CreateRequest(), default),
        "update" => adapter.UpdateAsync(new AmsDeploymentUpdateTransportRequest { ServiceTarget = ControlPlane(), AgentId = Agent, DeploymentId = Deployment, ETag = EntityTag, DeploymentName = "renamed" }, default),
        "deploy" => adapter.DeployAsync(DeployRequest(), default),
        "delete" => adapter.DeleteAsync(DeleteRequest(), default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string NotDeployedBody(string deploymentId, string deploymentName)
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        body["id"] = deploymentId;
        body["deploymentName"] = deploymentName;
        body["state"] = "NotDeployed";
        body.Remove("packageRevisionId");
        body.Remove("packageSha256");
        body.Remove("packageSizeInBytes");
        body.Remove("commitId");
        body.Remove("endpoint");
        return body.ToJsonString();
    }

    private static AmsDeploymentAdapter Adapter(CapturingPackageHandler handler, FakePpapiTokenProvider tokens) => new(new HttpClient(handler), tokens);

    private static AmsServiceTarget ControlPlane() => ServiceTargetAt(ControlPlaneEndpoint, AmsRouteForm.TenantHost);

    private static AmsAgentReadTransportRequest AgentReference() => new() { ServiceTarget = ControlPlane(), AgentId = Agent };

    private static AmsDeploymentReadTransportRequest Reference() => new() { ServiceTarget = ControlPlane(), AgentId = Agent, DeploymentId = Deployment };

    private static AmsDeploymentCreateTransportRequest CreateRequest() => new() { ServiceTarget = ControlPlane(), TargetEnvironmentId = Target, AgentId = Agent, DeploymentName = "dev-proof", DeploymentType = "Test", EntraId = Entra, EntraIdType = "Blueprint", Language = "Python", LanguageVersion = "3.12" };

    private static AmsDeploymentDeployTransportRequest DeployRequest() => new() { ServiceTarget = ControlPlane(), AgentId = Agent, DeploymentId = Deployment, ETag = EntityTag };

    private static AmsDeploymentDeleteTransportRequest DeleteRequest() => new() { ServiceTarget = ControlPlane(), AgentId = Agent, DeploymentId = Deployment, ETag = EntityTag };

    private static AmsServiceTarget ServiceTargetAt(string endpoint, AmsRouteForm routeForm) => new() { Endpoint = new Uri(endpoint), Audience = Audience, RouteForm = routeForm, TenantId = Tenant, EnvironmentId = Home };

    private static FakePpapiTokenProvider ReadAndUpdateTokens() => new() { Scopes = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate}" };
}
