using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

internal static class WireFixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ams", "WireFixtures", name));

    public static JsonNode Node(string name) => JsonNode.Parse(Read(name))!;

    public static void AssertSameJson(string name, byte[] actual) => AssertSameJson(name, Encoding.UTF8.GetString(actual));

    public static void AssertSameJson(string name, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Read(name)), JsonNode.Parse(actual)), $"The JSON differs from wire fixture {name}: {actual}");

    public static void AssertReadsAgent(JsonNode wire, AmsMcsAgentRecord read)
    {
        Assert.Equal(((string?)wire["id"], (string?)wire["tenantId"], (string?)wire["environmentId"], (string?)wire["displayName"], (string?)wire["description"], (string?)wire["agentType"], (string?)wire["repositoryId"], (string?)wire["eTag"]), (read.AgentId, read.TenantId, read.EnvironmentId, read.DisplayName, read.Description, read.AgentType, read.RepositoryId, read.ETag));
        var sourceControl = wire["sourceControl"];
        Assert.Equal(((string?)sourceControl?["repositoryId"], (string?)sourceControl?["gitUrl"], (string?)sourceControl?["branch"], (string?)sourceControl?["directory"], (bool?)sourceControl?["repositoryCreated"] ?? false), (read.SourceControl?.RepositoryId, read.SourceControl?.GitUrl, read.SourceControl?.Branch, read.SourceControl?.Directory, read.SourceControl?.RepositoryCreated ?? false));
        var mcs = wire["mcs"];
        Assert.Equal(((string?)mcs?["schemaName"], (string?)mcs?["authoringShape"], (string?)mcs?["homeDeploymentId"]), (read.Mcs?.SchemaName, read.Mcs?.AuthoringShape, read.Mcs?.HomeDeploymentId));
        AssertReadsDeployments(wire["deployments"], read.Deployments);
    }

    public static void AssertReadsListItem(JsonNode wire, AmsMcsAgentListItem read)
    {
        Assert.Equal(((string?)wire["id"], (string?)wire["displayName"], (string?)wire["description"], (string?)wire["agentType"], (string?)wire["repositoryId"]), (read.AgentId, read.DisplayName, read.Description, read.AgentType, read.RepositoryId));
        Assert.Equal(((string?)wire["schemaName"] ?? (string?)wire["mcs"]?["schemaName"], (string?)wire["agentFolder"], (string?)wire["branch"], (string?)wire["homeDeploymentId"], (int?)wire["deploymentCount"], (string?)wire["cloudState"]), (read.SchemaName, read.AgentFolder, read.Branch, read.HomeDeploymentId, read.DeploymentCount, read.CloudState));
        AssertReadsDeployments(wire["deployments"], read.Deployments);
    }

    public static void AssertReadsDeployment(JsonNode wire, AmsMcsDeploymentRecord read)
    {
        Assert.Equal(((string?)wire["id"], (string?)wire["agentId"], (string?)wire["homeEnvironmentId"], (string?)wire["environmentId"], (string?)wire["agentType"], (string?)wire["deploymentType"], (string?)wire["deploymentName"]), (read.DeploymentId, read.AgentId, read.HomeEnvironmentId, read.EnvironmentId, read.AgentType, read.DeploymentType, read.DeploymentName));
        Assert.Equal(((string?)wire["lastDeployedById"], wire["lastDeployedTime"] is { } time ? DateTimeOffset.Parse((string)time!, CultureInfo.InvariantCulture) : (DateTimeOffset?)null), (read.LastDeployedBy, read.LastDeployedTime));
        var mcs = wire["mcs"];
        Assert.Equal(((string?)mcs?["botId"], (string?)mcs?["workflowId"], (string?)mcs?["schemaName"], (string?)mcs?["state"]), (read.BotId, read.WorkflowId, read.SchemaName, read.State));
        Assert.Equal(((string?)mcs?["lastDeployedCommitSha"], (string?)mcs?["lastDeployOrigin"], (string?)mcs?["cloudBaseCommitSha"], (bool?)mcs?["cloudBaseExact"]), (read.LastDeployedCommitSha, read.LastDeployOrigin, read.CloudBaseCommitSha, read.CloudBaseExact));
    }

    public static WireExchange Exchange(string name)
    {
        var exchange = Node(name);
        var request = exchange["request"]!;
        var response = exchange["response"]!;
        return new WireExchange(
            (string)request["method"]!,
            (string)request["path"]!,
            Strings(request["query"]),
            Strings(request["headers"]),
            (string?)request["bodyFixture"],
            (int)response["status"]!,
            (string?)response["bodyFixture"],
            (bool)exchange["startsOperation"]!,
            [.. (exchange["errors"]?.AsArray() ?? []).Select(error => new WireExchangeError((int)error!["status"]!, (string)error["code"]!, (string?)error["bodyFixture"], (string)error["when"]!))]);
    }

    private static void AssertReadsDeployments(JsonNode? wire, IReadOnlyList<AmsMcsDeploymentRecord> read)
    {
        var deployments = wire?.AsArray() ?? [];
        Assert.Equal(deployments.Count, read.Count);
        for (var index = 0; index < deployments.Count; index++)
        {
            AssertReadsDeployment(deployments[index]!, read[index]);
        }
    }

    private static Dictionary<string, string> Strings(JsonNode? node) => (node?.AsObject() ?? []).ToDictionary(property => property.Key, property => (string)property.Value!, StringComparer.OrdinalIgnoreCase);
}

internal sealed record WireExchange(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, string? RequestBody, int Status, string? ResponseBody, bool StartsOperation, IReadOnlyList<WireExchangeError> Errors)
{
    public HttpResponseMessage Answer() => Respond(Status, ResponseBody is null ? null : WireFixture.Read(ResponseBody));

    public static HttpResponseMessage Respond(int status, string? body) => body is null ? new HttpResponseMessage((HttpStatusCode)status) : FakeHttpResponse.Json((HttpStatusCode)status, body);
}

internal sealed record WireExchangeError(int Status, string Code, string? BodyFixture, string When)
{
    public string Body() => BodyFixture is null ? JsonSerializer.Serialize(new { error = new { code = Code, message = When } }) : WireFixture.Read(BodyFixture);
}

public sealed class AmsMcsOperationAdapterTests
{
    internal const string Agent = "aaaaaaaa-1111-4111-8111-000000000001";
    internal const string Tenant = "11111111-1111-1111-1111-111111111111";
    internal const string Home = "22222222-2222-2222-2222-222222222222";
    internal const string DevDeployment = "eeeeeeee-5555-4555-8555-000000000005";
    internal const string TestDeployment = "ffffffff-6666-4666-8666-000000000006";
    internal const string TestEnvironment = "33333333-3333-3333-3333-333333333333";
    internal const string Deployed = "9fceb02d0ae598e95dc970b74767f19372d61af8";
    internal const string Head = "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12";

    private const string Operation = "12345678-7777-4777-8777-000000000007";
    private const string CaptureOperation = "12345678-7777-4777-8777-000000000017";
    private const string ReadAndUpdate = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate}";
    private const string CallerTokenExpiresTooSoonFixture = "mcs-agent-deploy.caller-token-expires-too-soon.error.json";

    internal static readonly AmsServiceTarget FrontDoor = new() { Endpoint = new Uri("https://env-1.api.powerplatform.com/"), Audience = "https://api.powerplatform.com/", RouteForm = AmsRouteForm.EnvironmentHost, TenantId = Tenant, EnvironmentId = Home };

    private static readonly AmsServiceTarget Gateway = new() { Endpoint = new Uri("https://ppapigw.example/"), Audience = "https://api.dev.powerplatform.com", RouteForm = AmsRouteForm.Direct, TenantId = Tenant, EnvironmentId = Home };
    private static readonly AmsServiceTarget ControlPlane = new() { Endpoint = new Uri("https://api.powerplatform.com"), Audience = "https://api.powerplatform.com/", RouteForm = AmsRouteForm.TenantHost, TenantId = Tenant, EnvironmentId = Home };

    [Theory]
    [InlineData(false, false, null, "mcs-deploy.request.json")]
    [InlineData(true, false, null, "mcs-deploy.publish.request.json")]
    [InlineData(false, true, null, "mcs-deploy.overwrite.request.json")]
    [InlineData(false, false, Head, "mcs-deploy.commit.request.json")]
    public async Task Deploy_SendsTheCommitAndTheChoicesWithIdempotencyKey_AndReadsTheAcceptedOperation(bool publish, bool overwrite, string? commitSha, string requestFixture)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json")));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var accepted = await new AmsMcsOperationAdapter(new HttpClient(handler), tokens).DeployAsync(DeployRequest() with { CommitSha = commitSha, Publish = publish, Overwrite = overwrite }, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://ppapigw.example/agenthosting/tenants/{Tenant}/environments/{Home}/agents/{Agent}/mcs/deployments/{TestDeployment}/deploy?api-version=1", request.Uri.AbsoluteUri);
        Assert.Equal("deploy-key-1", request.IdempotencyKey);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.True(request.HasAuthorization);
        WireFixture.AssertSameJson(requestFixture, request.Body);
        Assert.Equal(["https://api.dev.powerplatform.com"], tokens.Resources);
        Assert.Equal((Operation, "Accepted", Deployed), (accepted.OperationId, accepted.Status, accepted.SourceCommitSha));
    }

    [Fact]
    public async Task Deploy_WithoutUpdatePermission_SendsNothing()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json")));

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Adapter(handler, PowerPlatformApiPermissions.AppsRead).DeployAsync(DeployRequest(), default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Deploy_CapacityRejection_CarriesTheRetryAfterTheServiceAskedFor()
    {
        var handler = new CapturingPackageHandler(_ =>
        {
            var response = FakeHttpResponse.Json(HttpStatusCode.TooManyRequests, Error("McsOperationCapacityExceeded", "No worker slot is free."));
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter(handler).DeployAsync(DeployRequest(), default));

        Assert.Equal(ErrorCodes.AmsAgent.OperationCapacityExceeded, exception.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Contains("clientRequestId=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_ToADevBotWithoutACloudBase_IsAmsRefusal_WithoutARuleOfMsagent()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Conflict, WireFixture.Read("mcs-deploy.base-unknown.error.json")));

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter(handler).DeployAsync(DeployRequest(), default));

        Assert.Equal(ErrorCodes.AmsAgent.CaptureBaseUnknown, exception.ErrorCode);
        Assert.Contains("Run a local deploy, which records the commit Dev holds, or deploy with overwrite", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Deploy_Redirect_IsNotFollowed()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Redirect(HttpStatusCode.TemporaryRedirect, "https://elsewhere.example/"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler).DeployAsync(DeployRequest(), default));

        Assert.Single(handler.Requests);
        Assert.Contains("ppapigw.example", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("capture")]
    [InlineData("register")]
    [InlineData("deploy record")]
    public async Task AnOnBehalfOfRoute_RenewsACachedTokenThatExpiresWithinTheMinimumLifetime_BeforeSendingIt(string route)
    {
        var handler = new CapturingPackageHandler(_ => route switch
        {
            "deploy" => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json")),
            "capture" => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-capture.accepted.json")),
            "register" => FakeHttpResponse.Json(HttpStatusCode.Created, WireFixture.Read("mcs-agent-register.response.json")),
            _ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-deploy-record.response.json")),
        });
        var tokens = new FakePpapiTokenProvider { Scopes = ReadAndUpdate, ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(34) };
        var adapter = new AmsMcsOperationAdapter(new HttpClient(handler), tokens);

        Task call = route switch
        {
            "register" => adapter.RegisterAsync(AmsMcsWireExchangeTests.NewAgent(Gateway), default),
            "deploy record" => adapter.RecordDeployAsync(AmsMcsWireExchangeTests.AgentDeployRecord(Gateway), default),
            _ => StartAsync(adapter, route, respondAsync: false),
        };

        await call;

        Assert.Equal([Gateway.Audience], tokens.RenewedResources);
        Assert.True(Assert.Single(handler.Requests).HasAuthorization);
    }

    [Theory]
    [InlineData("deploy", false)]
    [InlineData("deploy", true)]
    [InlineData("capture", false)]
    [InlineData("capture", true)]
    public async Task AnOperationRoute_AsksForAnAnswerOnceTheOperationStarted_OnlyForACallerThatDoesNotWait_AndReadsTheAcceptedOperation(string route, bool respondAsync)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read($"mcs-{route}.accepted.json")));

        var accepted = await StartAsync(Adapter(handler), route, respondAsync);

        Assert.Equal(respondAsync ? "respond-async" : null, Assert.Single(handler.Requests).Headers.GetValueOrDefault("Prefer"));
        Assert.Equal(AmsMcsOperationStatus.Accepted, accepted.Status);
        Assert.Null(accepted.Finished);
    }

    [Theory]
    [InlineData("deploy", "mcs-operation.succeeded.json")]
    [InlineData("deploy", "mcs-operation.deploy-captured.json")]
    [InlineData("deploy", "mcs-operation.push-failed.json")]
    [InlineData("deploy", "mcs-operation.running.json")]
    [InlineData("capture", "mcs-operation.capture-succeeded.json")]
    [InlineData("capture", "mcs-operation.capture-merged.json")]
    public async Task AnOperationRoute_ThatAnswersWithTheOperation_CarriesItAsTheOperationReadRouteReadsIt(string route, string operationFixture)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read(operationFixture)));
        var adapter = Adapter(handler, ReadAndUpdate);

        var accepted = await StartAsync(adapter, route, respondAsync: false);
        var read = await adapter.GetOperationAsync(FrontDoor, Agent, accepted.OperationId, default);

        Assert.Equal((read.OperationId, read.Status, read.SourceCommitSha), (accepted.OperationId, accepted.Status, accepted.SourceCommitSha));
        Assert.Equivalent(read, accepted.Finished, strict: true);
        Assert.False(handler.Requests[0].Headers.ContainsKey("Prefer"));
    }

    [Fact]
    public async Task AnOperationRoute_ThatAnswersWithSomethingOtherThanAnOperation_IsAnInvalidResponse()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-deploy.accepted.json")));

        Assert.Equal("invalid-ams-response", (await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler).DeployAsync(DeployRequest(), default))).ErrorKind);
    }

    public static TheoryData<string, string> LostAnswersOfEachOperationRoute()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in new[] { "deploy", "capture" })
        {
            foreach (var trigger in LostAnswer.Triggers)
            {
                data.Add(route, trigger);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LostAnswersOfEachOperationRoute))]
    public async Task AnOperationRoute_WhoseAnswerIsLost_IsSentOnceMore_WithTheSameRequestKey_ANewClientRequestId_AndRespondAsync(string route, string lostAnswer)
    {
        var handler = new CapturingPackageHandler(LostAnswer.First(lostAnswer, () => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read($"mcs-{route}.accepted.json"))));

        var accepted = await StartAsync(Adapter(handler), route, respondAsync: false);

        LostAnswer.AssertReplayedOnce(handler.Requests);
        Assert.Equal(AmsMcsOperationStatus.Accepted, accepted.Status);
        Assert.Null(accepted.Finished);
    }

    [Theory]
    [MemberData(nameof(LostAnswersOfEachOperationRoute))]
    public async Task AnOperationRoute_WhoseReplayIsLostToo_FailsAsALostAnswerFails_NamingBothClientRequestIds(string route, string lostAnswer)
    {
        var handler = new CapturingPackageHandler(_ => LostAnswer.Of(lostAnswer));

        var exception = await Assert.ThrowsAnyAsync<AgentHostingException>(() => StartAsync(Adapter(handler), route, respondAsync: false));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal((lostAnswer switch { "timeout" => ErrorCodes.Deployment.AgentDeployTimedOut, "gateway-timeout" => ErrorCodes.Environment.TargetRequestFailed, _ => ErrorCodes.Deployment.AgentDeployTransportFailed }, ExitCodes.Failure), (exception.ErrorCode, exception.ExitCode));
        Assert.Contains(LostAnswer.ReportedIds(handler.Requests), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lost-answer-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("capture")]
    public async Task AnOperationRoute_WhoseReplayIsRefused_FailsAsTheRefusalDoesWithoutAReplay_NamingBothClientRequestIds(string route)
    {
        var conflict = () => FakeHttpResponse.Json(HttpStatusCode.Conflict, Error("IdempotencyConflict", "The idempotency key was used with another request."));
        var replayed = new CapturingPackageHandler(LostAnswer.First("timeout", conflict));
        var refused = new CapturingPackageHandler(_ => conflict());

        var afterReplay = await Assert.ThrowsAnyAsync<AgentHostingException>(() => StartAsync(Adapter(replayed), route, respondAsync: false));
        var withoutReplay = await Assert.ThrowsAnyAsync<AgentHostingException>(() => StartAsync(Adapter(refused), route, respondAsync: false));

        Assert.Equal(2, replayed.Requests.Count);
        Assert.Equal((withoutReplay.GetType(), withoutReplay.ErrorCode, withoutReplay.ExitCode, withoutReplay.ErrorKind, withoutReplay.Remediation), (afterReplay.GetType(), afterReplay.ErrorCode, afterReplay.ExitCode, afterReplay.ErrorKind, afterReplay.Remediation));
        Assert.Equal(withoutReplay.Message.Replace($"clientRequestId={Assert.Single(refused.Requests).ClientRequestId}", LostAnswer.ReportedIds(replayed.Requests), StringComparison.Ordinal), afterReplay.Message);
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("capture")]
    public async Task AnOperationRoute_CanceledByTheCallerWhileItWaitsForTheAnswer_IsNotReplayed(string route)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CapturingPackageHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartAsync(Adapter(handler), route, respondAsync: false, cancellation.Token));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "McsOperationCapacityExceeded")]
    [InlineData(HttpStatusCode.TooManyRequests, "TooManyRequests")]
    public async Task Deploy_RefusedAsUnavailableOrThrottled_IsNotReplayed(HttpStatusCode status, string code)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(status, Error(code, "Try again later.")));

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Adapter(handler).DeployAsync(DeployRequest(), default));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(LostAnswerTriggers))]
    public async Task Register_WhoseAnswerIsLost_IsSentOnceMoreWithTheSameBody_AndReadsTheReplayAnswer(string lostAnswer)
    {
        var handler = new CapturingPackageHandler(LostAnswer.First(lostAnswer, () => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-register.response.json"))));

        var registered = await Adapter(handler).RegisterAsync(AmsMcsWireExchangeTests.NewAgent(FrontDoor), default);

        Assert.Equal(2, handler.Requests.Count);
        var (lost, replay) = (handler.Requests[0], handler.Requests[1]);
        Assert.Equal((lost.Method, lost.Uri), (replay.Method, replay.Uri));
        Assert.Equal(lost.Body, replay.Body);
        Assert.All(handler.Requests, request => Assert.Equal((null, null), (request.IdempotencyKey, request.Headers.GetValueOrDefault("Prefer"))));
        Assert.NotEqual(lost.ClientRequestId, replay.ClientRequestId);
        Assert.Equal((Agent, "\"etag-1\""), (registered.AgentId, registered.ETag));
    }

    public static TheoryData<string> LostAnswerTriggers() => new(LostAnswer.Triggers);

    [Theory]
    [InlineData("agent read")]
    [InlineData("operation read")]
    [InlineData("repository read")]
    [InlineData("deploy record")]
    [InlineData("deployment create")]
    [InlineData("deployment rename")]
    [InlineData("deployment delete")]
    [InlineData("agent delete")]
    public async Task ARequestThatStartsNoOperation_WhoseAnswerIsLost_IsNotReplayed(string call)
    {
        var handler = new CapturingPackageHandler(_ => LostAnswer.Of("timeout"));
        var adapter = Adapter(handler, ReadAndUpdate);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => call switch
        {
            "agent read" => adapter.GetAgentAsync(FrontDoor, Agent, default),
            "operation read" => adapter.GetOperationAsync(FrontDoor, Agent, Operation, default),
            "repository read" => adapter.ReadRepositoryAsync(FrontDoor, AmsMcsWireExchangeTests.Repository, null, includeDeployments: true, default),
            "deploy record" => adapter.RecordDeployAsync(AmsMcsWireExchangeTests.AgentDeployRecord(FrontDoor), default),
            "deployment create" => adapter.CreateMcsDeploymentAsync(AmsMcsWireExchangeTests.TestCreate(ControlPlane, TestDeployment, "contoso-test"), default),
            "deployment rename" => adapter.RenameMcsDeploymentAsync(Rename(), default),
            "deployment delete" => adapter.DeleteDeploymentAsync(ControlPlane, Agent, TestDeployment, "MCSAgent", default),
            _ => adapter.DeleteAgentAsync(FrontDoor, Agent, "\"etag-1\"", default),
        });

        Assert.Equal(ErrorCodes.Deployment.AgentDeployTimedOut, exception.ErrorCode);
        Assert.Contains($"(clientRequestId={Assert.Single(handler.Requests).ClientRequestId})", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenThatOutlivesTheMinimumLifetime_IsReused_AfterTheRenewalThatGaveIt()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json")));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate, ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(34), RenewedExpiresOn = DateTimeOffset.UtcNow.AddMinutes(36) };
        var adapter = new AmsMcsOperationAdapter(new HttpClient(handler), tokens);

        await adapter.DeployAsync(DeployRequest(), default);
        await adapter.DeployAsync(DeployRequest(), default);

        Assert.Equal([Gateway.Audience], tokens.RenewedResources);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task APlainRead_SendsTheCachedTokenThatExpiresWithinTheMinimumLifetime_WhileTheNextOnBehalfOfRequestRenewsIt()
    {
        var handler = new CapturingPackageHandler(request => request.Method == HttpMethod.Get ? FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-operation.succeeded.json")) : FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json")));
        var tokens = new FakePpapiTokenProvider { Scopes = ReadAndUpdate, ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(10) };
        var adapter = new AmsMcsOperationAdapter(new HttpClient(handler), tokens);

        await adapter.GetOperationAsync(Gateway, Agent, Operation, default);

        Assert.Empty(tokens.RenewedResources);

        await adapter.DeployAsync(DeployRequest(), default);

        Assert.Equal([Gateway.Audience], tokens.RenewedResources);
    }

    [Fact]
    public async Task Deploy_RefusedBecauseTheTokenExpiresTooSoon_RenewsItOnce_AndSendsTheSameRequestAgain()
    {
        var responses = new Queue<HttpResponseMessage>([FakeHttpResponse.Json(HttpStatusCode.Forbidden, WireFixture.Read(CallerTokenExpiresTooSoonFixture)), FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-deploy.accepted.json"))]);
        var handler = new CapturingPackageHandler(_ => responses.Dequeue());
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        Assert.Equal(Operation, (await new AmsMcsOperationAdapter(new HttpClient(handler), tokens).DeployAsync(DeployRequest(), default)).OperationId);
        Assert.Equal([Gateway.Audience], tokens.RenewedResources);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal((HttpMethod.Post, "deploy-key-1", handler.Requests[0].ClientRequestId, true), (request.Method, request.IdempotencyKey, request.ClientRequestId, request.HasAuthorization)));
        Assert.All(handler.Requests, request => WireFixture.AssertSameJson("mcs-deploy.request.json", request.Body));
    }

    [Fact]
    public async Task Deploy_RefusedAgainAfterTheRenewal_IsNotRetriedTwice_AndAsksForANewSignIn()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Forbidden, WireFixture.Read(CallerTokenExpiresTooSoonFixture)));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => new AmsMcsOperationAdapter(new HttpClient(handler), tokens).DeployAsync(DeployRequest(), default));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([Gateway.Audience], tokens.RenewedResources);
        Assert.Equal((ErrorCodes.AmsAgent.OnBehalfOfUnavailable, ExitCodes.Failure, "mcs-on-behalf-of-unavailable"), (exception.ErrorCode, exception.ExitCode, exception.ErrorKind));
        Assert.Equal("Your sign-in token could not be renewed with enough lifetime for the operation. Run `msagent auth login` and retry.", exception.Remediation);
        Assert.EndsWith($"(clientRequestId={handler.Requests[1].ClientRequestId}).", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOperation_ReadsTheOperationContract()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-operation.succeeded.json")));

        var operation = await Adapter(handler, PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default);

        Assert.Equal($"https://env-1.api.powerplatform.com/agenthosting/agents/{Agent}/mcs/operations/{Operation}?api-version=1", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Null(handler.Requests[0].IdempotencyKey);
        Assert.Equal(("Deploy", AmsMcsOperationStatus.Succeeded, TestDeployment, Deployed, null), (operation.Kind, operation.Status, operation.DeploymentId, operation.SourceCommitSha, operation.ResultCommitSha));
        Assert.True(operation.IsTerminal);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 6, 41, TimeSpan.Zero), operation.CompletedTime);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 6, 40, TimeSpan.Zero), operation.LastPublishedTime);
        Assert.Equal(
            [
                ("FlowLeftInDraft", "Send HR reminder", "cr1a2_sharedoutlook|cr1a2_sharedteams", null, null, null),
                ("CollectionSharedWithOtherBots", "cr1a2_shared", "cr1a2_payroll|cr1a2_benefits", null, null, null),
                ("PushWarning", null, "", "PromptRefused", null, "The environment refused the AI prompt 'HR summary prompt': The prompt model is unavailable."),
            ],
            operation.Notices.Select(notice => (notice.Kind, notice.Target, string.Join("|", notice.RelatedItems), notice.Code, notice.Detail, notice.Message)));
    }

    [Theory]
    [InlineData("\"notices\": null")]
    [InlineData("\"notices\": []")]
    [InlineData("\"error\": null")]
    public async Task GetOperation_WithoutNotices_ReadsNone(string notices)
    {
        var body = $"{{ \"id\": \"{Operation}\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"{Agent}\", {notices} }}";

        Assert.Empty((await Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body)), PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default)).Notices);
    }

    [Theory]
    [InlineData(", \"publish\": true")]
    [InlineData(", \"publish\": null")]
    [InlineData("")]
    public async Task GetOperation_ReadsADeployAsPublishing_WhenTheServiceSaysSoOrDoesNotSay(string publish)
    {
        var body = $"{{ \"id\": \"{Operation}\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"{Agent}\"{publish} }}";

        Assert.True((await Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body)), PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default)).Publish);
    }

    [Fact]
    public async Task GetOperation_ReadsTheSucceededOperationFixtureOfADeployThatDidNotPublish()
    {
        var operation = await ReadOperationAsync("mcs-operation.succeeded-without-publish.json");

        Assert.Equal((AmsMcsOperationStatus.Succeeded, Deployed, false), (operation.Status, operation.SourceCommitSha, operation.Publish));
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 19, 2, 38, TimeSpan.Zero), operation.LastPublishedTime);
    }

    [Fact]
    public async Task GetOperation_ReadsADeployThatCapturedDevsEdits_WithTheCommitTheCaptureMade()
    {
        var operation = await ReadOperationAsync("mcs-operation.deploy-captured.json");

        Assert.Equal(("Deploy", DevDeployment, false), (operation.Kind, operation.DeploymentId, operation.Publish));
        Assert.Equal((Deployed, "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c", "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c", null), (operation.SourceCommitSha, operation.ResultCommitSha, operation.CapturedCommitSha, operation.MergedCommitSha));
        Assert.Equal(new DateTimeOffset(2026, 7, 25, 19, 2, 38, TimeSpan.Zero), operation.LastPublishedTime);
    }

    [Fact]
    public async Task GetOperation_ReadsACaptureThatMergedALaterCommit()
    {
        var operation = await ReadOperationAsync("mcs-operation.capture-merged.json");

        Assert.Equal(("Capture", "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c", Head, null), (operation.Kind, operation.ResultCommitSha, operation.MergedCommitSha, operation.CapturedCommitSha));
        Assert.Null(operation.LastPublishedTime);
    }

    [Fact]
    public async Task GetOperation_ReadsACaptureWithItsResultCommit()
    {
        var operation = await ReadOperationAsync("mcs-operation.capture-succeeded.json");

        Assert.Equal(("Capture", AmsMcsOperationStatus.Succeeded, DevDeployment), (operation.Kind, operation.Status, operation.DeploymentId));
        Assert.Equal((Deployed, "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c", null), (operation.SourceCommitSha, operation.ResultCommitSha, operation.MergedCommitSha));
    }

    [Fact]
    public async Task GetOperation_ReadsTheConflictOfACapture_WithTheCommitItMergedWith()
    {
        var operation = await ReadOperationAsync("mcs-operation.capture-conflict.json");

        Assert.Equal((AmsMcsOperationStatus.Failed, "McsCaptureConflict", Head), (operation.Status, operation.Error?.Code, operation.MergedCommitSha));
        Assert.Equal(["ContosoHR/behaviors/skill-1/SKILL.md", "ContosoHR/topics/Greeting.mcs.yml"], operation.Error!.Details.Select(detail => detail.Message));
    }

    [Theory]
    [InlineData("mcs-operation.deploy-dev-edits-not-in-commit.json", "McsDevEditsNotInCommit")]
    [InlineData("mcs-operation.deploy-commit-not-found.json", "McsSourceValidationFailed")]
    public async Task GetOperation_ReadsAFailedDeploy_WithItsRecordedCode(string fixture, string code)
    {
        var operation = await ReadOperationAsync(fixture);

        Assert.Equal(("Deploy", AmsMcsOperationStatus.Failed, code), (operation.Kind, operation.Status, operation.Error?.Code));
    }

    [Fact]
    public async Task GetOperation_ReadsANumericNoticeKindAndAMissingTarget()
    {
        var body = $"{{ \"id\": \"{Operation}\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"{Agent}\", \"notices\": [{{ \"kind\": 1, \"relatedItems\": [\"cr1a2_payroll\"] }}, {{ \"kind\": \"FlowLeftInDraft\", \"target\": \"Reminder\" }}, {{ \"kind\": 2, \"code\": \"ChangesNotStored\" }}] }}";

        Assert.Equal([("CollectionSharedWithOtherBots", null, "cr1a2_payroll"), ("FlowLeftInDraft", "Reminder", ""), ("PushWarning", null, "")], (await Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body)), PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default)).Notices.Select(notice => (notice.Kind, notice.Target, string.Join("|", notice.RelatedItems))));
    }

    [Fact]
    public async Task GetOperation_ReadsARunningOperationWithItsHeartbeat()
    {
        var operation = await ReadOperationAsync("mcs-operation.running.json");

        Assert.Equal((AmsMcsOperationStatus.Running, false), (operation.Status, operation.IsTerminal));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 5, 30, TimeSpan.Zero), operation.LastHeartbeatTime);
        Assert.Null(operation.CompletedTime);
        Assert.Null(operation.MergedCommitSha);
        Assert.Null(operation.CapturedCommitSha);
        Assert.Null(operation.LastPublishedTime);
        Assert.Empty(operation.Notices);
    }

    [Fact]
    public async Task GetOperation_ReadsTheRecordedFailureWithItsDetails()
    {
        var operation = await ReadOperationAsync("mcs-operation.failed.json");

        Assert.Equal(AmsMcsOperationStatus.Failed, operation.Status);
        Assert.Equal("McsOperationFailed", operation.Error?.Code);
        Assert.Equal(new AmsMcsOperationErrorDetail("ConnectionReferenceUnusable", "cr1a2_sharedoffice365"), Assert.Single(operation.Error!.Details));
        Assert.Empty(operation.Notices);
    }

    [Theory]
    [InlineData("{ \"kind\": \"Deploy\", \"status\": \"Running\", \"agentId\": \"a\" }")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{ \"id\": \"op\", \"kind\": \"Deploy\", \"status\": 7, \"agentId\": \"a\", \"notices\": \"none\" }")]
    [InlineData("{ \"id\": \"op\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"a\", \"notices\": [\"Flow stays in draft.\"] }")]
    [InlineData("{ \"id\": \"op\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"a\", \"notices\": [{ \"target\": \"Reminder\" }] }")]
    [InlineData("{ \"id\": \"op\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"a\", \"notices\": [{ \"kind\": \"FlowLeftInDraft\", \"relatedItems\": [3] }] }")]
    [InlineData("{ \"id\": \"op\", \"kind\": \"Deploy\", \"status\": \"Succeeded\", \"agentId\": \"a\", \"notices\": [{ \"kind\": \"FlowLeftInDraft\", \"relatedItems\": \"cr1a2_x\" }] }")]
    public async Task GetOperation_MalformedSuccess_IsAnInvalidResponse(string body)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body));

        Assert.Equal("invalid-ams-response", (await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default))).ErrorKind);
    }

    [Fact]
    public async Task GetAgent_ReadsTheRecordAsTheRegisterAnswerCarriesIt()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-register.response.json")));

        var agent = await Adapter(handler, PowerPlatformApiPermissions.AppsRead).GetAgentAsync(FrontDoor, Agent, default);

        Assert.Equal($"https://env-1.api.powerplatform.com/agenthosting/agents/{Agent}?api-version=1", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        WireFixture.AssertReadsAgent(WireFixture.Node("mcs-agent-register.response.json"), agent);
    }

    [Fact]
    public async Task GetAgent_ReadsTheETagHeaderBeforeTheBodyETag()
    {
        var fromHeader = new CapturingPackageHandler(_ =>
        {
            var response = FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-register.response.json"));
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"agent-etag-header\"");
            return response;
        });
        var fromBody = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-register.response.json")));

        Assert.Equal("\"agent-etag-header\"", (await Adapter(fromHeader, PowerPlatformApiPermissions.AppsRead).GetAgentAsync(FrontDoor, Agent, default)).ETag);
        Assert.Equal("\"etag-1\"", (await Adapter(fromBody, PowerPlatformApiPermissions.AppsRead).GetAgentAsync(FrontDoor, Agent, default)).ETag);
    }

    [Fact]
    public async Task ListAgents_SendsTheFiltersAndSkipToken_AndReadsTheListFieldsOfEachMcsItem()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("mcs-agent-list.response.json")));

        var page = await Adapter(handler, PowerPlatformApiPermissions.AppsRead).ListAgentsAsync(FrontDoor, 2, "MCSAgent", AmsMcsWireExchangeTests.Repository, "page 1", default);

        Assert.Equal($"https://env-1.api.powerplatform.com/agenthosting/agents/mcs?api-version=1&$top=2&$skiptoken=page%201&agentType=MCSAgent&repositoryId={AmsMcsWireExchangeTests.Repository}", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        Assert.Equal((true, "page-2"), (page.HasMore, page.NextSkipToken));
        var items = WireFixture.Node("mcs-agent-list.response.json")["value"]!.AsArray();
        Assert.Equal(items.Count, page.Agents.Count);
        for (var index = 0; index < items.Count; index++)
        {
            WireFixture.AssertReadsListItem(items[index]!, page.Agents[index]);
        }
    }

    [Fact]
    public async Task ListAgents_WithoutFilters_AsksForThePageAlone()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, "{\"value\":[]}"));

        var page = await Adapter(handler, PowerPlatformApiPermissions.AppsRead).ListAgentsAsync(FrontDoor, 100, null, null, null, default);

        Assert.Equal("https://env-1.api.powerplatform.com/agenthosting/agents/mcs?api-version=1&$top=100", Assert.Single(handler.Requests).Uri.AbsoluteUri);
        Assert.Equal((false, null, 0), (page.HasMore, page.NextSkipToken, page.Agents.Count));
    }

    [Fact]
    public async Task ListAgents_ReadsAnItemWithoutTheListFieldsFromItsRecord_AndToleratesNumericAndUnknownValues()
    {
        var page = $"{{ \"value\": [{WireFixture.Read("mcs-agent-register.response.json")}, {{ \"id\": \"CODE-1\", \"tenantId\": \"{Tenant}\", \"environmentId\": \"{Home}\", \"displayName\": \"Code\", \"agentType\": 0, \"cloudState\": 1, \"futureField\": {{ \"x\": 1 }} }}, {{ \"id\": \"code-2\", \"tenantId\": \"{Tenant}\", \"environmentId\": \"{Home}\", \"displayName\": \"Code 2\", \"agentType\": \"CodeAgent\", \"cloudState\": \"deployed\", \"deploymentCount\": null }}] }}";

        var result = await Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, page)), PowerPlatformApiPermissions.AppsRead).ListAgentsAsync(FrontDoor, 100, null, null, null, default);

        Assert.Equal(("cr1a2_contosoHr", AmsMcsWireExchangeTests.Repository, "ContosoHR", "main", DevDeployment, null, null), (result.Agents[0].SchemaName, result.Agents[0].RepositoryId, result.Agents[0].AgentFolder, result.Agents[0].Branch, result.Agents[0].HomeDeploymentId, result.Agents[0].DeploymentCount, result.Agents[0].CloudState));
        Assert.Equal([("CODE-1", "CodeAgent", "Deployed"), ("code-2", "CodeAgent", "Deployed")], result.Agents.Skip(1).Select(agent => (agent.AgentId, agent.AgentType, agent.CloudState)));
        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData("{\"value\":[],\"nextLink\":\"https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1\"}")]
    [InlineData("{\"value\":[{\"id\":\"a\",\"tenantId\":\"t\",\"environmentId\":\"e\",\"displayName\":\"A\",\"agentType\":\"MCSAgent\",\"deploymentCount\":\"two\"}]}")]
    [InlineData("{\"value\":[{\"id\":\"a\",\"tenantId\":\"t\",\"environmentId\":\"e\",\"displayName\":\"A\",\"agentType\":\"MCSAgent\",\"deploymentCount\":-1}]}")]
    [InlineData("{\"value\":[{\"id\":\"a\",\"tenantId\":\"t\",\"environmentId\":\"e\",\"displayName\":\"A\"}]}")]
    public async Task ListAgents_AMalformedPage_IsAnInvalidResponse(string body)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body));

        Assert.Contains("Agent list", (await Assert.ThrowsAsync<AgentDeployException>(() => Adapter(handler, PowerPlatformApiPermissions.AppsRead).ListAgentsAsync(FrontDoor, 100, null, null, null, default))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownServiceCode_FallsBackToTheStatusMapping_WithCorrelation()
    {
        var handler = new CapturingPackageHandler(_ =>
        {
            var response = FakeHttpResponse.Json(HttpStatusCode.NotFound, Error("AgentNotFound", "The agent was not found."));
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-1");
            return response;
        });

        var exception = await Assert.ThrowsAsync<TargetRequestException>(() => Adapter(handler, PowerPlatformApiPermissions.AppsRead).GetAgentAsync(FrontDoor, Agent, default));

        Assert.Equal(404, exception.Status);
        Assert.Contains("serviceRequestId=service-request-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_IsReportedWithoutTransportDetail()
    {
        using var client = new HttpClient(new AsyncPackageHandler(_ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("socket-detail"))));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => new AmsMcsOperationAdapter(client, new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead }).GetOperationAsync(FrontDoor, Agent, Operation, default));

        Assert.Equal(ErrorCodes.Deployment.AgentDeployTimedOut, exception.ErrorCode);
        Assert.DoesNotContain("socket-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "mcs-capture.request.json")]
    [InlineData("cloud", "mcs-capture.resolve-cloud.request.json")]
    public async Task Capture_SendsTheWireContractWithIdempotencyKey_AndReadsTheAcceptedOperation(string? resolve, string requestFixture)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-capture.accepted.json")));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var accepted = await new AmsMcsOperationAdapter(new HttpClient(handler), tokens).CaptureAsync(new AmsMcsCaptureTransportRequest { Target = Gateway, AgentId = Agent, DeploymentId = DevDeployment, IdempotencyKey = "capture-key-1", Message = "Capture portal edits", Resolve = resolve }, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://ppapigw.example/agenthosting/tenants/{Tenant}/environments/{Home}/agents/{Agent}/mcs/deployments/{DevDeployment}/capture?api-version=1", request.Uri.AbsoluteUri);
        Assert.Equal("capture-key-1", request.IdempotencyKey);
        WireFixture.AssertSameJson(requestFixture, request.Body);
        Assert.Equal(["https://api.dev.powerplatform.com"], tokens.Resources);
        Assert.Equal((CaptureOperation, "Accepted", Deployed), (accepted.OperationId, accepted.Status, accepted.SourceCommitSha));
    }

    [Fact]
    public async Task Capture_WithoutUpdatePermission_SendsNothing()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Accepted, WireFixture.Read("mcs-capture.accepted.json")));

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Adapter(handler, PowerPlatformApiPermissions.AppsRead).CaptureAsync(new AmsMcsCaptureTransportRequest { Target = Gateway, AgentId = Agent, DeploymentId = DevDeployment, IdempotencyKey = "capture-key-1" }, default));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("mcs-capture.base-unknown.error.json", HttpStatusCode.Conflict, ErrorCodes.AmsAgent.CaptureBaseUnknown)]
    [InlineData(null, HttpStatusCode.Conflict, ErrorCodes.AmsAgent.CaptureAgentNotInCloud)]
    public async Task Capture_ServiceRejections_AreTyped(string? fixture, HttpStatusCode status, int errorCode)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(status, fixture is null ? Error("McsCaptureAgentNotInCloud", "Rejected.") : WireFixture.Read(fixture)));

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter(handler).CaptureAsync(new AmsMcsCaptureTransportRequest { Target = Gateway, AgentId = Agent, DeploymentId = DevDeployment, IdempotencyKey = "capture-key-1" }, default));

        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateMcsDeployment_ForAStandaloneWorkflow_SendsItsTypeAndReadsTheDeployedWorkflow()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Created, WireFixture.Read("cmp-deployment.workflow.response.json")));

        var created = await Adapter(handler).CreateMcsDeploymentAsync(new AmsMcsDeploymentCreateTransportRequest { Target = ControlPlane, AgentId = "aaaaaaaa-1111-4111-8111-000000000011", DeploymentId = "ffffffff-6666-4666-8666-000000000016", AgentType = "MCSWorkflow", DeploymentType = "Test", DeploymentName = "approvals-test", TargetEnvironmentId = TestEnvironment }, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/aaaaaaaa-1111-4111-8111-000000000011/deployments?api-version=1", request.Uri.AbsoluteUri);
        Assert.DoesNotContain(AmsMcsOperationAdapter.AgentTypeHeaderName, request.HeaderNames);
        Assert.Equal(["deploymentId", "agentType", "deploymentType", "deploymentName", "environmentId"], JsonNode.Parse(request.Body)!.AsObject().Select(property => property.Key));
        WireFixture.AssertSameJson("cmp-deployment-create.workflow.request.json", request.Body);
        WireFixture.AssertReadsDeployment(WireFixture.Node("cmp-deployment.workflow.response.json"), created);
    }

    [Theory]
    [InlineData("cmp-deployment-create.dev.request.json", DevDeployment, "Dev", "contoso-dev", Home, "cmp-deployment.dev.response.json")]
    [InlineData("cmp-deployment-create.request.json", TestDeployment, "Test", "contoso-test", TestEnvironment, "cmp-deployment.response.json")]
    public async Task CreateMcsDeployment_SendsMsagentsDeploymentIdFirst_AndReadsADeploymentThatClaimsNoBot(string requestFixture, string deploymentId, string deploymentType, string deploymentName, string environmentId, string responseFixture)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.Created, WireFixture.Read(responseFixture)));

        var created = await Adapter(handler).CreateMcsDeploymentAsync(new AmsMcsDeploymentCreateTransportRequest { Target = ControlPlane, AgentId = Agent, DeploymentId = deploymentId, AgentType = "MCSAgent", DeploymentType = deploymentType, DeploymentName = deploymentName, TargetEnvironmentId = environmentId }, default);

        WireFixture.AssertSameJson(requestFixture, Assert.Single(handler.Requests).Body);
        WireFixture.AssertReadsDeployment(WireFixture.Node(responseFixture), created);
        Assert.Null(created.BotId);
    }

    [Theory]
    [InlineData("MCSAgent")]
    [InlineData("MCSWorkflow")]
    public async Task RenameMcsDeployment_PatchesOnlyTheName_WithTheRecordsAgentTypeHint_AndReadsTheRenamedDeployment(string agentType)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("cmp-deployment-update.response.json")));

        var renamed = await Adapter(handler).RenameMcsDeploymentAsync(Rename() with { AgentType = agentType }, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Patch, $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments/{TestDeployment}?api-version=1", "application/json"), (request.Method, request.Uri.AbsoluteUri, request.ContentType));
        Assert.Equal(agentType, request.Headers[AmsMcsOperationAdapter.AgentTypeHeaderName]);
        WireFixture.AssertSameJson("cmp-deployment-rename.request.json", request.Body);
        WireFixture.AssertReadsDeployment(WireFixture.Node("cmp-deployment-update.response.json"), renamed);
    }

    [Fact]
    public async Task RenameMcsDeployment_ReadsTheCommitsADeployedAndCapturedDevHolds()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read("cmp-deployment.dev-captured.response.json")));

        var renamed = await Adapter(handler).RenameMcsDeploymentAsync(Rename(), default);

        WireFixture.AssertReadsDeployment(WireFixture.Node("cmp-deployment.dev-captured.response.json"), renamed);
    }
    [Fact]
    public async Task CreateMcsDeployment_StageRejection_MapsToItsCliError()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.BadRequest, Error("McsDeploymentStageEnvironmentInvalid", "Test on home.")));

        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Adapter(handler).CreateMcsDeploymentAsync(AmsMcsWireExchangeTests.TestCreate(ControlPlane, TestDeployment, "contoso-test") with { TargetEnvironmentId = Home }, default));

        Assert.Equal((ErrorCodes.AmsAgent.DeploymentStageEnvironmentInvalid, ExitCodes.Usage), (exception.ErrorCode, exception.ExitCode));
    }

    internal static string Error(string code, string message) => $"{{ \"error\": {{ \"code\": \"{code}\", \"message\": \"{message}\" }} }}";

    internal static AmsMcsOperationAdapter Adapter(CapturingPackageHandler handler, string scopes = ReadAndUpdate) =>
        new(new HttpClient(handler), new FakePpapiTokenProvider { Scopes = scopes });

    private static AmsMcsDeployTransportRequest DeployRequest() => new() { Target = Gateway, AgentId = Agent, DeploymentId = TestDeployment, IdempotencyKey = "deploy-key-1" };

    private static AmsMcsDeploymentRenameTransportRequest Rename() => new() { Target = ControlPlane, AgentId = Agent, DeploymentId = TestDeployment, AgentType = "MCSAgent", DeploymentName = "contoso-uat" };

    private static async Task<AmsMcsOperationRecord> ReadOperationAsync(string fixture) =>
        await Adapter(new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, WireFixture.Read(fixture))), PowerPlatformApiPermissions.AppsRead).GetOperationAsync(FrontDoor, Agent, Operation, default);

    private static Task<AmsMcsOperationAccepted> StartAsync(AmsMcsOperationAdapter adapter, string route, bool respondAsync, CancellationToken cancellationToken = default) => route switch
    {
        "deploy" => adapter.DeployAsync(DeployRequest() with { RespondAsync = respondAsync }, cancellationToken),
        _ => adapter.CaptureAsync(new AmsMcsCaptureTransportRequest { Target = Gateway, AgentId = Agent, DeploymentId = DevDeployment, IdempotencyKey = "capture-key-1", RespondAsync = respondAsync }, cancellationToken),
    };
}
