using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

/// <summary>Offline wire-contract and credential-safety coverage for the AMS-DP v1 package upload.</summary>
public sealed class AmsPackagePublicationAdapterTests
{
    private const string Endpoint = "https://env-1.api.powerplatform.com";
    private const string Audience = "https://api.powerplatform.com/";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Agent = "22222222-2222-2222-2222-222222222222";
    private const string Operation = "33333333-3333-3333-3333-333333333333";
    private const string Revision = "44444444-4444-4444-4444-444444444444";
    private static readonly byte[] PackageBytes = [0x50, 0x4b, 0x03, 0x04, 0x00, 0xff, 0x7f, 0x80];

    private static readonly string[] UniversallyInvalidValues = ["null", "{}", "[]", "false"];

    private const string SuccessBody =
        """
        {
          "operationId": "33333333-3333-3333-3333-333333333333",
          "agentId": "22222222-2222-2222-2222-222222222222",
          "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "sizeInBytes": 8,
          "status": 2,
          "packageRevisionId": "44444444-4444-4444-4444-444444444444",
          "createdTime": "2026-09-05T12:00:00Z",
          "changedTime": "2026-09-05T12:00:01Z"
        }
        """;

    [Theory]
    [InlineData("2", false)]
    [InlineData("2", true)]
    [InlineData("\"Succeeded\"", false)]
    [InlineData("\"Succeeded\"", true)]
    public async Task Publish_SendsExactBytesOnceToTheV1Route_AndParsesTheTerminalOperation(
        string wireStatus,
        bool includeServiceRequestId)
    {
        var body = SuccessBody.Replace("\"status\": 2", $"\"status\": {wireStatus}", StringComparison.Ordinal);
        var response = FakeHttpResponse.Json(HttpStatusCode.OK, body);
        if (includeServiceRequestId)
        {
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        }

        var handler = new CapturingPackageHandler(_ => response);
        var tokenProvider = new FakePpapiTokenProvider();
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var result = await Adapter(handler, tokenProvider).PublishAsync(Request(packageStream), default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://env-1.api.powerplatform.com/agenthosting/agents/22222222-2222-2222-2222-222222222222/packages?api-version=1",
            request.Uri.AbsoluteUri);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal((long)PackageBytes.Length, request.ContentLength);
        Assert.Equal(PackageBytes, request.Body);
        Assert.True(request.HasAuthorization);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.Equal(request.ClientRequestId, result.ClientRequestId);
        Assert.Equal(includeServiceRequestId ? "service-request-id" : null, result.ServiceRequestId);
        Assert.Equal([Audience], tokenProvider.Resources);
        Assert.Equal([Tenant], tokenProvider.Tenants);
        Assert.Equal(Operation, result.OperationId);
        Assert.Equal(Agent, result.AgentId);
        Assert.Equal(Revision, result.PackageRevisionId);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(PackageBytes.Length, result.SizeInBytes);
        Assert.True(packageStream.CanRead);
    }

    [Fact]
    public async Task Publish_CarriesNoRepositoryStorageOrOwnerIdentifier()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, SuccessBody));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        await Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default);

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain(
            request.HeaderNames,
            name => name.Contains("repository", StringComparison.OrdinalIgnoreCase)
                || name.Contains("storage", StringComparison.OrdinalIgnoreCase)
                || name.Contains("owner", StringComparison.OrdinalIgnoreCase)
                || name.Contains("probe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("repositor", request.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Publish_HostChangingRedirect_IsRejectedWithoutResendingAuthorization(HttpStatusCode status)
    {
        var handler = new CapturingPackageHandler(
            _ => FakeHttpResponse.Redirect(status, "https://attacker.example/collect"));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("redirect-rejected", exception.ErrorKind);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("env-1.api.powerplatform.com", request.Uri.Host);
        Assert.True(request.HasAuthorization);
        Assert.DoesNotContain("attacker.example", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedirectRejectingHandler_DoesNotFollowRedirects()
    {
        using var handler = AmsPackagePublicationAdapter.CreateRedirectRejectingHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "MissingUserIdentity", "ams-missing-user-identity", ExitCodes.NotSignedIn)]
    [InlineData(HttpStatusCode.Forbidden, "TenantIdMismatch", "tenant-mismatch", ExitCodes.Usage)]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden", "ams-publication-disabled", ExitCodes.Failure)]
    [InlineData(HttpStatusCode.NotFound, "AppNotFound", "ams-agent-not-bound", ExitCodes.Failure)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "InvalidRequestContent", "package-too-large", ExitCodes.Usage)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "InvalidRequestContent", "ams-content-type-rejected", ExitCodes.Failure)]
    public async Task Publish_ServiceRejection_MapsKnownCodesWithoutEchoingTheResponse(
        HttpStatusCode status,
        string serviceCode,
        string expectedErrorKind,
        int expectedExitCode)
    {
        var body = "{\"error\":{\"code\":\"" + serviceCode + "\",\"message\":\"response-body-detail\"}}";
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(status, body));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal(expectedErrorKind, exception.ErrorKind);
        Assert.Equal(expectedExitCode, exception.ExitCode);
        Assert.Contains("clientRequestId=", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("response-body-detail", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.PartialResult);
    }

    [Fact]
    public async Task Publish_RejectionSurfacesSafeClientAndServiceCorrelation()
    {
        var response = FakeHttpResponse.Json(
            HttpStatusCode.BadRequest,
            """{"Error":{"Code":"InvalidRequestContent","Message":"response-body-detail"}}""");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        var request = Assert.Single(handler.Requests);
        Assert.Contains($"clientRequestId={request.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("requestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("response-body-detail", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", "Accepted", false)]
    [InlineData("3", "Failed", false)]
    [InlineData("\"Accepted\"", "Accepted", false)]
    [InlineData("\"Failed\"", "Failed", false)]
    [InlineData("1", "Accepted", true)]
    [InlineData("3", "Failed", true)]
    [InlineData("\"Accepted\"", "Accepted", true)]
    [InlineData("\"Failed\"", "Failed", true)]
    public async Task Publish_DurableNonSuccess_PreservesTypedOperationIdentity(
        string wireStatus,
        string status,
        bool includeNullRevision)
    {
        var body = JsonNode.Parse(FailureBody(wireStatus))!.AsObject();
        if (includeNullRevision)
        {
            body["packageRevisionId"] = null;
        }

        var response = FakeHttpResponse.Json(HttpStatusCode.BadGateway, body.ToJsonString());
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("publication-failed", exception.ErrorKind);
        var partial = Assert.IsType<AgentPackageUploadPartialResult>(exception.PartialResult);
        Assert.Equal(Operation, partial.PackageOperationId);
        Assert.Equal(status, partial.OperationStatus);
        Assert.Contains(Operation, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"status '{status}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    public async Task Publish_DurableFailureForAnotherAgent_DoesNotExposePartialResult(string wireStatus)
    {
        var body = FailureBody(wireStatus).Replace(Agent, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", StringComparison.Ordinal);
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.BadGateway, body));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
        Assert.Null(exception.PartialResult);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-an-operation-id")]
    public async Task Publish_MalformedDurableFailure_DoesNotExposePartialResult(string operationId)
    {
        var body = FailureBody("3").Replace(Operation, operationId, StringComparison.Ordinal);
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.BadGateway, body));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAnyAsync<AgentHostingException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("request-failed", exception.ErrorKind);
        Assert.False(exception is PackagePublicationException { PartialResult: not null });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Publish_TokenWithoutUpdatePermission_SendsNothing()
    {
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, SuccessBody));
        var tokenProvider = new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead };
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<TokenClaimException>(() =>
            Adapter(handler, tokenProvider).PublishAsync(Request(packageStream), default));

        Assert.Equal(TokenClaimFailure.MissingPermission, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Publish_SuccessWithoutTheRequiredFields_IsRejected()
    {
        var handler = new CapturingPackageHandler(
            _ => FakeHttpResponse.Json(HttpStatusCode.OK, """{"operationId":"missing-the-rest"}"""));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
    }

    [Fact]
    public async Task Publish_SuccessWithEmptyDurableIdentifiers_IsRejected()
    {
        var body = SuccessBody
            .Replace(Operation, Guid.Empty.ToString("D"), StringComparison.Ordinal)
            .Replace(Revision, Guid.Empty.ToString("D"), StringComparison.Ordinal);
        var handler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, body));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("invalid-ams-response", exception.ErrorKind);
    }

    public static TheoryData<HttpStatusCode, string> RejectedOperationResponses
    {
        get
        {
            var corpus = new TheoryData<HttpStatusCode, string>();
            foreach (var body in MalformedOperationBodies().Distinct(StringComparer.Ordinal))
            {
                corpus.Add(HttpStatusCode.OK, body);
                corpus.Add(HttpStatusCode.BadGateway, body);
            }

            foreach (var wireStatus in new[] { "1", "3", "\"Accepted\"", "\"Failed\"" })
            {
                corpus.Add(HttpStatusCode.OK, FailureBody(wireStatus));
            }

            corpus.Add(HttpStatusCode.BadGateway, SuccessBody);
            corpus.Add(
                HttpStatusCode.BadGateway,
                SuccessBody.Replace("\"status\": 2", "\"status\": \"Succeeded\"", StringComparison.Ordinal));
            return corpus;
        }
    }

    private static IEnumerable<string> MalformedOperationBodies()
    {
        string[] invalidRoots =
        [
            "", " ", "{", "null", "[]", "[{}]", "42", "true", "\"private-response-detail\"", "{}",
            """{"operationId":"private-response-detail"}""",
            """{"operationId":"private-response-detail""",
            """{"operationId":"\uZZZZ-private-response-detail"}""",
        ];
        foreach (var body in invalidRoots)
        {
            yield return body;
        }

        yield return SuccessBody[..(SuccessBody.Length / 2)];
        yield return SuccessBody[..^1];
        yield return SuccessBody + " private-response-detail";
        yield return SuccessBody[..^1] + ",}";
        yield return SuccessBody[..^1] + ",\"private-response-detail\":" +
            new string('[', 65) + "0" + new string(']', 65) + "}";

        foreach (var validBody in new[] { SuccessBody, FailureBody("1"), FailureBody("3") })
        {
            var seed = JsonNode.Parse(validBody)!.AsObject();
            seed["untrustedDetail"] = "private-response-detail";
            foreach (var property in seed.Where(property => property.Key != "untrustedDetail"))
            {
                var missing = seed.DeepClone().AsObject();
                missing.Remove(property.Key);
                yield return missing.ToJsonString();

                string[] invalidValues = property.Key switch
                {
                    "sizeInBytes" => ["\"8\"", "0", "-1", "0.5", "9223372036854775808", "-9223372036854775809", "1e100"],
                    "status" => ["-1", "0", "4", "2147483648", "0.5", "\"\"", "\"NotSpecified\"", "\"private-response-detail\""],
                    "createdTime" or "changedTime" =>
                        ["42", "\"\"", "\" \\t \"", "\"private-response-detail\"", "\"2026-02-30T12:00:00Z\"", "\"10000-01-01T00:00:00Z\""],
                    "sha256" =>
                        ["42", "\"\"", "\" \\t \"", $"\"{new string('a', 63)}\"", $"\"{new string('a', 65)}\"", $"\"{new string('g', 64)}\""],
                    _ => ["42", "\"\"", "\" \\t \"", "\"private-response-detail\"", $"\"{Guid.Empty:D}\""],
                };
                foreach (var invalidValue in UniversallyInvalidValues.Concat(invalidValues))
                {
                    var malformed = seed.DeepClone().AsObject();
                    malformed[property.Key] = JsonNode.Parse(invalidValue);
                    yield return malformed.ToJsonString();
                }
            }

            var reversedDates = seed.DeepClone().AsObject();
            reversedDates["changedTime"] = "2026-09-05T11:59:59Z";
            yield return reversedDates.ToJsonString();
        }

        foreach (var wireStatus in new[] { "1", "3", "\"Accepted\"", "\"Failed\"" })
        {
            var unexpectedRevision = JsonNode.Parse(FailureBody(wireStatus))!.AsObject();
            unexpectedRevision["packageRevisionId"] = Revision;
            yield return unexpectedRevision.ToJsonString();
        }
    }

    [Theory]
    [MemberData(nameof(RejectedOperationResponses))]
    public async Task Publish_RejectedOperationCorpus_ReturnsSafeFailureWithoutPartialResultOrRetry(
        HttpStatusCode status,
        string body)
    {
        var response = FakeHttpResponse.Json(status, body);
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request-id");
        var handler = new CapturingPackageHandler(_ => response);
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAnyAsync<AgentHostingException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        if (status == HttpStatusCode.OK)
        {
            var publication = Assert.IsType<PackagePublicationException>(exception);
            Assert.Equal("invalid-ams-response", publication.ErrorKind);
            Assert.Null(publication.PartialResult);
            Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            var target = Assert.IsType<TargetRequestException>(exception);
            Assert.Equal("request-failed", target.ErrorKind);
            Assert.Equal((int)status, target.Status);
            Assert.Contains("requestId=service-request-id", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        if (exception.Remediation is not null)
        {
            Assert.DoesNotContain("private-response-detail", exception.Remediation, StringComparison.Ordinal);
        }

        var request = Assert.Single(handler.Requests);
        Assert.True(Guid.TryParse(request.ClientRequestId, out _));
        Assert.Contains($"clientRequestId={request.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        Assert.True(packageStream.CanRead);
    }

    [Fact]
    public async Task Publish_ResponseBodyConnectionFailure_IsIndeterminateAndDoesNotLeakDetails()
    {
        var handler = new CapturingPackageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingReadContent(new HttpRequestException("private-response-detail")),
        });
        using var packageStream = new MemoryStream(PackageBytes, writable: false);

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            Adapter(handler, new FakePpapiTokenProvider()).PublishAsync(Request(packageStream), default));

        Assert.Equal("publication-transport-failed", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-response-detail", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.PartialResult);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Publish_Timeout_IsStructuredAndWarnsThatTheOutcomeIsIndeterminate()
    {
        var handler = new AsyncPackageHandler(_ =>
            Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("transport-internal-detail")));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);
        using var httpClient = new HttpClient(handler);
        var adapter = new AmsPackagePublicationAdapter(httpClient, new FakePpapiTokenProvider());

        var exception = await Assert.ThrowsAsync<PackagePublicationException>(() =>
            adapter.PublishAsync(Request(packageStream), CancellationToken.None));

        Assert.Equal("publication-timeout", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clientRequestId=", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("transport-internal-detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new AsyncPackageHandler(token => Task.FromCanceled<HttpResponseMessage>(token));
        using var packageStream = new MemoryStream(PackageBytes, writable: false);
        using var httpClient = new HttpClient(handler);
        var adapter = new AmsPackagePublicationAdapter(httpClient, new FakePpapiTokenProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.PublishAsync(Request(packageStream), cancellation.Token));
    }

    private static AmsPackagePublicationAdapter Adapter(
        CapturingPackageHandler handler,
        FakePpapiTokenProvider tokenProvider) =>
        new(new HttpClient(handler), tokenProvider);

    private static string FailureBody(string wireStatus) =>
        SuccessBody
            .Replace("\"status\": 2", $"\"status\": {wireStatus}", StringComparison.Ordinal)
            .Replace($"\"packageRevisionId\": \"{Revision}\",", string.Empty, StringComparison.Ordinal);

    private static AmsPackagePublicationTransportRequest Request(Stream packageStream) => new()
    {
        Endpoint = new Uri(Endpoint),
        Audience = Audience,
        TenantId = Tenant,
        EnvironmentId = "env/blue",
        AgentId = Agent,
        PackageStream = packageStream,
        PackageLength = packageStream.Length,
    };
}
