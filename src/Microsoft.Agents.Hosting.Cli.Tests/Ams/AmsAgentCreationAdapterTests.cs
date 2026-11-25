using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentCreationAdapterTests
{
    private const string Endpoint = "https://amsdp-alias.example";
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private const string SuccessBody =
        """
        {
          "id": "agent-1",
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "environmentId": "env/blue",
          "displayName": "My Agent",
          "repositoryId": "repo-1"
        }
        """;

    [Fact]
    public async Task Create_SendsTheAgentContract_AndParsesServiceOwnedCoordinates()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.Created, SuccessBody));
        var tokenProvider = CreateTokenProvider();

        var result = await Adapter(handler, tokenProvider).CreateAsync(Request(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://amsdp-alias.example/agenthosting/tenants/11111111-1111-1111-1111-111111111111" +
            "/environments/env%2Fblue/agents?api-version=1",
            request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.True(request.HasAuthorization);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.Equal("retry-key-1", request.IdempotencyKey);

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("My Agent", root.GetProperty("displayName").GetString());
        Assert.Equal("description", root.GetProperty("description").GetString());
        Assert.Equal(0, root.GetProperty("agentType").GetInt32());
        Assert.Equal(1, root.GetProperty("language").GetInt32());
        Assert.Equal("AgentHostCLI", root.GetProperty("origin").GetString());
        Assert.False(root.TryGetProperty("entraObjectId", out _));
        Assert.False(root.TryGetProperty("identityType", out _));
        Assert.False(root.TryGetProperty("repositoryId", out _));
        Assert.False(root.TryGetProperty("gitUrl", out _));
        Assert.False(root.TryGetProperty("storage", out _));
        Assert.False(root.TryGetProperty("owner", out _));

        Assert.Equal([Audience], tokenProvider.Resources);
        Assert.Equal([Tenant], tokenProvider.Tenants);
        Assert.Equal("agent-1", result.AgentId);
        Assert.Equal("repo-1", result.RepositoryId);
    }

    [Fact]
    public async Task Create_ThroughPowerPlatformApiFrontDoor_UsesTheUnscopedCollectionPath()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.Created, SuccessBody));
        var request = Request();
        request = request with
        {
            ServiceTarget = request.ServiceTarget with
            {
                Endpoint = new Uri("https://env-1.api.powerplatform.com/"),
                RouteForm = AmsRouteForm.EnvironmentHost,
            },
        };

        await Adapter(handler, CreateTokenProvider()).CreateAsync(request, default);

        Assert.Equal(
            "https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1",
            Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Create_TokenWithoutCreatePermission_SendsNothing()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.Created, SuccessBody));
        var tokenProvider = new FakePpapiTokenProvider
        {
            Scopes = PowerPlatformApiPermissions.AppsRead,
        };

        await Assert.ThrowsAsync<TokenClaimException>(() =>
            Adapter(handler, tokenProvider).CreateAsync(Request(), default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_Redirect_IsRejectedWithoutResendingAuthorization()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Redirect(HttpStatusCode.TemporaryRedirect, "https://attacker.example/collect"));

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request(), default));

        Assert.Equal("redirect-rejected", exception.ErrorKind);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("amsdp-alias.example", request.Uri.Host);
        Assert.True(request.HasAuthorization);
    }

    [Fact]
    public async Task Create_RepositoryBindingFailure_DoesNotAdviseBlindRetry()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.InternalServerError,
            """{"error":{"code":"AgentRepositoryBindingFailed","message":"sensitive response detail"}}""");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request(), default));

        Assert.Equal("agent-repository-binding-failed", exception.ErrorKind);
        Assert.Contains("Do not retry this create blindly", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ServiceRejection_MapsCodeWithoutEchoingBodyMessage()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"MissingAgentField","message":"sensitive response detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request(), default));

        Assert.Equal("ams-create-request-rejected", exception.ErrorKind);
        Assert.Contains("requestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ConcurrencyConflict_AdvisesSameKeyReplay()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.Conflict,
            """{"error":{"code":"AgentConcurrencyConflict","message":"sensitive response detail"}}""");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request(), default));

        Assert.Equal("agent-create-concurrency-conflict", exception.ErrorKind);
        Assert.Contains("same Idempotency-Key", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("Do not use a new key", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ResponseWithUnsupportedCharset_PreservesKeyForSameRequestRecovery()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(SuccessBody)),
        };
        response.Content.Headers.ContentType =
            MediaTypeHeaderValue.Parse("application/json; charset=unsupported-agent-charset");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request("retry&key"), default));

        Assert.Equal("agent-create-response-unreadable", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry&key", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("quote or escape", exception.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupported-agent-charset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_SuccessWithoutRequiredFields_IsRejected()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.Created, """{"id":"agent-1"}"""));

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Adapter(handler, CreateTokenProvider()).CreateAsync(Request(), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
    }

    [Fact]
    public async Task Create_Timeout_IsIndeterminateAndNotRetryShaped()
    {
        var handler = new AsyncPackageHandler(_ =>
            Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("transport-internal-detail")));
        using var httpClient = new HttpClient(handler);
        var adapter = new AmsAgentCreationAdapter(httpClient, CreateTokenProvider());

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            adapter.CreateAsync(Request("retry&key"), CancellationToken.None));

        Assert.Equal("agent-create-timeout", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry&key", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("quote or escape", exception.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--idempotency-key retry&key", exception.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("transport-internal-detail", exception.Message, StringComparison.Ordinal);
    }

    private static AmsAgentCreationAdapter Adapter(
        CapturingPackageHandler handler,
        FakePpapiTokenProvider tokenProvider) =>
        new(new HttpClient(handler), tokenProvider);

    private static FakePpapiTokenProvider CreateTokenProvider() => new()
    {
        Scopes = PowerPlatformApiPermissions.AppsCreate,
    };

    private static AmsAgentCreationTransportRequest Request(string idempotencyKey = "retry-key-1") => new()
    {
        ServiceTarget = new AmsServiceTarget
        {
            Endpoint = new Uri(Endpoint),
            Audience = Audience,
            RouteForm = AmsRouteForm.Direct,
            TenantId = Tenant,
            EnvironmentId = "env/blue",
        },
        IdempotencyKey = idempotencyKey,
        DisplayName = "My Agent",
        Description = "description",
        AgentType = 0,
        Language = 1,
        LanguageVersion = "net10.0",
        Origin = "AgentHostCLI",
    };
}
