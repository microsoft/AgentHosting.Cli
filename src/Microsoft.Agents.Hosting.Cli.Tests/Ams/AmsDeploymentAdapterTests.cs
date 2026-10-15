using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

/// <summary>Offline wire and authorization coverage for the Agent read and Deployment create ports.</summary>
public sealed class AmsDeploymentAdapterTests
{
    private const string DataPlaneEndpoint = "https://env-1.api.powerplatform.com";
    private const string ControlPlaneEndpoint = "https://api.powerplatform.com";
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Repository = "55555555-5555-5555-5555-555555555555";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Entra = "88888888-8888-8888-8888-888888888888";
    private static readonly string Digest = new('a', 64);

    private const string AgentBody =
        """
        {
          "id": "44444444-4444-4444-4444-444444444444",
          "tenantId": "11111111-1111-1111-1111-111111111111",
          "environmentId": "22222222-2222-2222-2222-222222222222",
          "repositoryId": "55555555-5555-5555-5555-555555555555",
          "repositoryBindingStatus": 2,
          "agentType": 0,
          "language": 1,
          "languageVersion": "10"
        }
        """;

    private static readonly string DeploymentBody =
        $$"""
        {
          "id": "{{Deployment}}",
          "agentId": "{{Agent}}",
          "tenantId": "{{Tenant}}",
          "homeEnvironmentId": "{{Home}}",
          "environmentId": "{{Target}}",
          "packageRevisionId": "{{Revision}}",
          "packageSha256": "{{Digest}}",
          "packageSizeInBytes": 42,
          "agentType": 0,
          "deploymentType": 0,
          "deploymentName": "dev-proof",
          "entraAppId": "{{Entra}}",
          "entraAppIdType": 0,
          "language": 1,
          "languageVersion": "10",
          "endpoint": "https://runtime.example/api/messages",
          "isCustomEndpoint": false,
          "isQuarantined": false
        }
        """;

    [Fact]
    public async Task GetAgent_UsesTheV1DataPlaneRouteAndReadPermission()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, AgentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var result = await Adapter(handler, tokens).GetAsync(AgentRequest(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"https://env-1.api.powerplatform.com/agenthosting/agents/{Agent}?api-version=1",
            request.Uri.AbsoluteUri);
        Assert.True(request.HasAuthorization);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.Empty(request.Body);
        Assert.Equal(Agent, result.AgentId);
        Assert.Equal(Repository, result.RepositoryId);
        Assert.Equal("Bound", result.RepositoryBindingStatus);
        Assert.Equal("CodeAgent", result.AgentType);
        Assert.Equal("DotNet", result.Language);
        Assert.Equal("10", result.LanguageVersion);
    }

    [Theory]
    [InlineData("1", "DotNet")]
    [InlineData("2", "Python")]
    [InlineData("3", "Node")]
    [InlineData("\"DotNet\"", "DotNet")]
    [InlineData("\"Python\"", "Python")]
    [InlineData("\"Node\"", "Node")]
    [InlineData("\"dotnet\"", "DotNet")]
    public async Task GetAgent_ParsesNamedAndNumericLanguageValues(string wireValue, string language)
    {
        var body = AgentBody.Replace("\"language\": 1", $"\"language\": {wireValue}", StringComparison.Ordinal);
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, body));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };

        var result = await Adapter(handler, tokens).GetAsync(AgentRequest(), default);

        Assert.Equal(language, result.Language);
    }

    [Fact]
    public async Task CreateDeployment_UsesTheV1ControlPlaneRouteAndExactPublicBody()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).CreateAsync(DeploymentRequest(), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments?api-version=1",
            request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.True(request.HasAuthorization);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(0, root.GetProperty("agentType").GetInt32());
        Assert.Equal(Revision, root.GetProperty("packageRevisionId").GetString());
        Assert.Equal(0, root.GetProperty("deploymentType").GetInt32());
        Assert.Equal("dev-proof", root.GetProperty("deploymentName").GetString());
        Assert.Equal(Target, root.GetProperty("environmentId").GetString());
        Assert.Equal(Entra, root.GetProperty("audience").GetString());
        Assert.Equal(0, root.GetProperty("entraAppIdType").GetInt32());
        Assert.Equal("DotNet", root.GetProperty("language").GetString());
        Assert.Equal("10", root.GetProperty("languageVersion").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("endpoint").ValueKind);
        Assert.False(root.GetProperty("isQuarantined").GetBoolean());
        Assert.Equal(request.ClientRequestId, result.ClientRequestId);
        Assert.Null(result.ServiceRequestId);
        Assert.Equal(Deployment, result.DeploymentId);
        Assert.Equal(Entra, result.EntraId);
        Assert.Equal("https://runtime.example/api/messages", result.RuntimeEndpoint);
        Assert.False(result.IsCustomEndpoint);
        Assert.False(result.IsQuarantined);
    }

    [Theory]
    [InlineData("DotNet")]
    [InlineData("Python")]
    [InlineData("Node")]
    public async Task CreateDeployment_UsesTheDeclaredLanguageEnumName(string language)
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };
        var request = DeploymentRequest() with { Language = language };

        await Adapter(handler, tokens).CreateAsync(request, default);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(language, document.RootElement.GetProperty("language").GetString());
    }

    [Theory]
    [InlineData("1", "DotNet")]
    [InlineData("2", "Python")]
    [InlineData("3", "Node")]
    [InlineData("\"DotNet\"", "DotNet")]
    [InlineData("\"Python\"", "Python")]
    [InlineData("\"Node\"", "Node")]
    [InlineData("\"dotnet\"", "DotNet")]
    public async Task CreateDeployment_ParsesNamedAndNumericResponseLanguages(string wireValue, string language)
    {
        var body = DeploymentBody.Replace("\"language\": 1", $"\"language\": {wireValue}", StringComparison.Ordinal);
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var result = await Adapter(handler, tokens).CreateAsync(
            DeploymentRequest() with { Language = language },
            default);

        Assert.Equal(language, result.Language);
    }

    [Theory]
    [InlineData("NotSpecified")]
    [InlineData("Java")]
    [InlineData("1")]
    public async Task CreateDeployment_UnsupportedLanguage_SendsNothing(string language)
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Adapter(handler, tokens).CreateAsync(DeploymentRequest() with { Language = language }, default));

        Assert.Equal("agent-not-deployable", exception.ErrorKind);
        Assert.Empty(handler.Requests);
        Assert.Empty(tokens.Resources);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TokenWithoutTheOperationPermission_SendsNothing(bool isAgentRead)
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, isAgentRead ? AgentBody : DeploymentBody));
        var tokens = new FakePpapiTokenProvider
        {
            Scopes = isAgentRead
                ? PowerPlatformApiPermissions.AppsUpdate
                : PowerPlatformApiPermissions.AppsRead,
        };
        var adapter = Adapter(handler, tokens);

        await Assert.ThrowsAsync<TokenClaimException>(() =>
            isAgentRead
                ? adapter.GetAsync(AgentRequest(), default)
                : adapter.CreateAsync(DeploymentRequest(), default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DeploymentRedirect_IsRejectedWithoutASecondCredentialBearingRequest()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Redirect(HttpStatusCode.TemporaryRedirect, "https://attacker.example/collect"));
        var adapter = Adapter(
            handler,
            new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate });

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            adapter.CreateAsync(DeploymentRequest(), default));

        Assert.Equal("redirect-rejected", exception.ErrorKind);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.powerplatform.com", request.Uri.Host);
        Assert.True(request.HasAuthorization);
        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedirectRejectingHandler_RequiresTls13AndDoesNotFollowRedirects()
    {
        using var handler = AmsDeploymentAdapter.CreateRedirectRejectingHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(SslProtocols.Tls13, handler.SslProtocols);
    }

    [Fact]
    public async Task DeploymentRejection_DoesNotEchoServiceControlledBody()
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"InvalidRequest","message":"private-response-detail"}}"""));
        var adapter = Adapter(
            handler,
            new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate });

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            adapter.CreateAsync(DeploymentRequest(), default));

        Assert.Contains("clientRequestId=", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentResponseBodyConnectionFailure_IsIndeterminateAndSafe()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new ThrowingReadContent(new HttpRequestException("private-response-detail")),
        });
        var adapter = Adapter(
            handler,
            new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsUpdate });

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            adapter.CreateAsync(DeploymentRequest(), default));

        Assert.Equal("deploy-transport-failed", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDeployment_ReadsCurrentRevisionThenPatchesExactConditionalBody()
    {
        var handler = new CapturingPackageHandler(request =>
            FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = ReadAndUpdateTokens();
        var adapter = Adapter(handler, tokens);

        var current = await adapter.GetAsync(UpdateRequest(), default);
        var updated = await adapter.UpdateAsync(
            UpdateRequest() with
            {
                PackageRevisionId = Revision,
                ExpectedPackageRevisionId = current.PackageRevisionId,
            },
            default);

        Assert.Equal(2, handler.Requests.Count);
        var read = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.Equal(
            $"https://api.powerplatform.com/agenthosting/environments/{Home}/agents/{Agent}/deployments/{Deployment}?api-version=1",
            read.Uri.AbsoluteUri);
        Assert.Empty(read.Body);
        Assert.Null(read.ContentType);
        Assert.True(read.HasAuthorization);

        var patch = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal(read.Uri, patch.Uri);
        Assert.Equal("application/json", patch.ContentType);
        Assert.True(patch.HasAuthorization);
        using var body = JsonDocument.Parse(patch.Body);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(Revision, body.RootElement.GetProperty("packageRevisionId").GetString());
        Assert.Equal(Revision, body.RootElement.GetProperty("expectedPackageRevisionId").GetString());
        Assert.Equal(Revision, updated.PackageRevisionId);
        Assert.Equal([Audience, Audience], tokens.Resources);
        Assert.Equal([Tenant, Tenant], tokens.Tenants);
    }

    [Fact]
    public async Task UpdateDeployment_GetRequiresOnlyIdentityAndRevision()
    {
        var body = JsonNode.Parse(DeploymentBody)!.AsObject();
        foreach (var field in new[]
        {
            "environmentId",
            "packageSha256",
            "packageSizeInBytes",
            "agentType",
            "deploymentType",
            "deploymentName",
            "entraAppId",
            "entraAppIdType",
            "language",
            "languageVersion",
            "endpoint",
            "isCustomEndpoint",
            "isQuarantined",
        })
        {
            body.Remove(field);
        }
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, body.ToJsonString()));

        var current = await Adapter(handler, ReadAndUpdateTokens()).GetAsync(UpdateRequest(), default);

        Assert.Equal(Deployment, current.DeploymentId);
        Assert.Equal(Revision, current.PackageRevisionId);
    }

    [Theory]
    [InlineData(PowerPlatformApiPermissions.AppsRead)]
    [InlineData(PowerPlatformApiPermissions.AppsUpdate)]
    [InlineData("")]
    public async Task UpdateDeployment_GetRequiresReadAndUpdateBeforeSending(string scopes)
    {
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, DeploymentBody));
        var tokens = new FakePpapiTokenProvider { Scopes = scopes };

        await Assert.ThrowsAsync<TokenClaimException>(() =>
            Adapter(handler, tokens).GetAsync(UpdateRequest(), default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UpdateDeployment_ConcurrencyConflictPreservesCorrelationWithoutRetry()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.Conflict,
            """{"error":{"code":"AgentConcurrencyConflict","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-conflict-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Adapter(handler, ReadAndUpdateTokens()).UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("agent-deployment-concurrency-conflict", exception.ErrorKind);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-conflict-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDeployment_MissingPackageUsesServiceErrorCodeAndReportsUsage()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.NotFound,
            """{"error":{"code":"AgentPackageRevisionNotFound","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-missing-package-id");
        var handler = new CapturingPackageHandler(_ => response);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Adapter(handler, ReadAndUpdateTokens()).UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("package-revision-not-found", exception.ErrorKind);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-missing-package-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateDeployment_FailurePreservesMethodCorrelationAndOutcome(bool failRead)
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"Unavailable","message":"private-response-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-failure-id");
        var handler = new CapturingPackageHandler(_ => response);
        var adapter = Adapter(handler, ReadAndUpdateTokens());

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            failRead
                ? adapter.GetAsync(UpdateRequest(), default)
                : adapter.UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(failRead ? HttpMethod.Get : HttpMethod.Patch, sent.Method);
        Assert.Contains(sent.Method.Method, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-failure-id", exception.Message, StringComparison.Ordinal);
        Assert.Equal(failRead, exception.Message.Contains("No update request was sent", StringComparison.Ordinal));
        Assert.Equal(!failRead, exception.Message.Contains("indeterminate", StringComparison.Ordinal));
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateDeployment_MalformedSuccessPreservesCorrelationAndOutcome(bool failRead)
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.OK,
            """{"id":"private-response-detail"}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-invalid-id");
        var handler = new CapturingPackageHandler(_ => response);
        var adapter = Adapter(handler, ReadAndUpdateTokens());

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            failRead
                ? adapter.GetAsync(UpdateRequest(), default)
                : adapter.UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-invalid-id", exception.Message, StringComparison.Ordinal);
        Assert.Equal(failRead, exception.Message.Contains("No update request was sent", StringComparison.Ordinal));
        Assert.Equal(!failRead, exception.Message.Contains("indeterminate", StringComparison.Ordinal));
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateDeployment_RedirectIsRejectedWithoutCredentialReplay(bool failRead)
    {
        var response = FakeHttpResponse.Redirect(
            HttpStatusCode.TemporaryRedirect,
            "https://attacker.example/collect");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-redirect-id");
        var handler = new CapturingPackageHandler(_ => response);
        var adapter = Adapter(handler, ReadAndUpdateTokens());

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            failRead
                ? adapter.GetAsync(UpdateRequest(), default)
                : adapter.UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("api.powerplatform.com", sent.Uri.Host);
        Assert.Equal("redirect-rejected", exception.ErrorKind);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-redirect-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateDeployment_TransportFailureIsNotRetriedOrLeaked(bool failRead)
    {
        var handler = new CapturingPackageHandler(_ =>
            throw new HttpRequestException("private-transport-detail"));
        var adapter = Adapter(handler, ReadAndUpdateTokens());

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            failRead
                ? adapter.GetAsync(UpdateRequest(), default)
                : adapter.UpdateAsync(UpdateRequest(), default));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(
            failRead ? "agent-deployment-read-failed" : "agent-deployment-transport-failed",
            exception.ErrorKind);
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-transport-detail", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<bool, string?, string> MalformedSuccessResponses
    {
        get
        {
            var corpus = new TheoryData<bool, string?, string>();
            foreach (var body in MalformedResponses(AgentBody))
            {
                corpus.Add(true, null, body);
            }

            foreach (var body in MalformedResponses(DeploymentBody))
            {
                corpus.Add(false, "service-request-id", body);
                corpus.Add(false, null, body);
            }

            return corpus;
        }
    }

    private static IEnumerable<string> MalformedResponses(string validBody)
    {
        string[] invalidRoots =
        [
            "", " ", "{", "null", "[]", "[{}]", "42", "true", "\"private-response-detail\"", "{}",
            """{"id":"private-response-detail"}""",
            """{"id":"private-response-detail""",
            """{"id":"\uZZZZ-private-response-detail"}""",
        ];
        foreach (var body in invalidRoots)
        {
            yield return body;
        }

        yield return validBody[..(validBody.Length / 2)];
        yield return validBody[..^1];
        yield return validBody + " private-response-detail";
        yield return validBody[..^1] + ",}";
        yield return validBody[..^1] + ",\"private-response-detail\":" +
            new string('[', 65) + "0" + new string(']', 65) + "}";

        var seed = JsonNode.Parse(validBody)!.AsObject();
        foreach (var property in seed)
        {
            var missing = seed.DeepClone().AsObject();
            missing.Remove(property.Key);
            missing["untrustedDetail"] = "private-response-detail";
            yield return missing.ToJsonString();

            string[] invalidValues = property.Key switch
            {
                "repositoryBindingStatus" or "agentType" or "language" or "deploymentType" or "entraAppIdType" =>
                    ["null", "{}", "[]", "true", "-1", "2147483648", "0.5", "\"\"", "\"private-response-detail\""],
                "packageSizeInBytes" =>
                    ["null", "{}", "[]", "true", "\"42\"", "0.5", "9223372036854775808", "-9223372036854775809", "1e100"],
                "isCustomEndpoint" or "isQuarantined" =>
                    ["null", "{}", "[]", "0", "\"false\"", "\"private-response-detail\""],
                _ => ["null", "{}", "[]", "false", "42", "\"\"", "\" \\t \""],
            };
            foreach (var invalidValue in invalidValues)
            {
                var malformed = seed.DeepClone().AsObject();
                malformed[property.Key] = JsonNode.Parse(invalidValue);
                malformed["untrustedDetail"] = "private-response-detail";
                yield return malformed.ToJsonString();
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSuccessResponses))]
    public async Task MalformedSuccess_IsRejected(
        bool isAgentRead,
        string? serviceRequestId,
        string body)
    {
        var response = FakeHttpResponse.Json(
            isAgentRead ? HttpStatusCode.OK : HttpStatusCode.Created,
            body);
        if (serviceRequestId is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ms-request-id", serviceRequestId);
        }

        var handler = new CapturingPackageHandler(_ => response);
        var tokens = new FakePpapiTokenProvider
        {
            Scopes = isAgentRead
                ? PowerPlatformApiPermissions.AppsRead
                : PowerPlatformApiPermissions.AppsUpdate,
        };
        var adapter = Adapter(handler, tokens);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            isAgentRead
                ? adapter.GetAsync(AgentRequest(), default)
                : adapter.CreateAsync(DeploymentRequest(), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
        var sent = Assert.Single(handler.Requests);
        if (isAgentRead)
        {
            Assert.Equal(
                "The Agent read returned a success status without a valid bound Agent response.",
                exception.Message);
        }
        else
        {
            Assert.True(Guid.TryParse(sent.ClientRequestId, out _));
            Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
            if (serviceRequestId is not null)
            {
                Assert.Contains($"serviceRequestId={serviceRequestId}", exception.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("serviceRequestId=", exception.Message, StringComparison.Ordinal);
            }
        }
    }

    private static AmsDeploymentAdapter Adapter(
        CapturingPackageHandler handler,
        FakePpapiTokenProvider tokens) =>
        new(new HttpClient(handler), tokens);

    private static AmsAgentReadTransportRequest AgentRequest() => new()
    {
        Endpoint = new Uri(DataPlaneEndpoint),
        Audience = Audience,
        TenantId = Tenant,
        EnvironmentId = Home,
        AgentId = Agent,
    };

    private static AmsDeploymentCreateTransportRequest DeploymentRequest() => new()
    {
        Endpoint = new Uri(ControlPlaneEndpoint),
        Audience = Audience,
        TenantId = Tenant,
        HomeEnvironmentId = Home,
        TargetEnvironmentId = Target,
        AgentId = Agent,
        PackageRevisionId = Revision,
        DeploymentName = "dev-proof",
        DeploymentType = "Dev",
        EntraId = Entra,
        EntraIdType = "AppReg",
        Language = "DotNet",
        LanguageVersion = "10",
    };

    private static AmsDeploymentUpdateTransportRequest UpdateRequest() => new()
    {
        Endpoint = new Uri(ControlPlaneEndpoint),
        Audience = Audience,
        TenantId = Tenant,
        HomeEnvironmentId = Home,
        AgentId = Agent,
        DeploymentId = Deployment,
        PackageRevisionId = Revision,
        ExpectedPackageRevisionId = Revision,
    };

    private static FakePpapiTokenProvider ReadAndUpdateTokens() => new()
    {
        Scopes = $"{PowerPlatformApiPermissions.AppsRead} {PowerPlatformApiPermissions.AppsUpdate}",
    };
}
