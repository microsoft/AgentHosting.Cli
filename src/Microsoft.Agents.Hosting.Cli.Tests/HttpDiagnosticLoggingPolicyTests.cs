using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class HttpDiagnosticLoggingPolicyTests
{
    [Fact]
    public void RequestHeaders_MaskOnlyAzureCliDefaults_WithoutChangingTheRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.TryAddWithoutValidation("aUtHoRiZaTiOn", "Bearer test-token");
        request.Headers.TryAddWithoutValidation("X-MS-AUTHORIZATION-AUXILIARY", ["Bearer first", "Bearer second"]);
        request.Headers.TryAddWithoutValidation("Cookie", "session=test-session");
        request.Headers.TryAddWithoutValidation("X-Custom", "custom-value");
        request.Headers.TryAddWithoutValidation("Location", "https://example.com/?sig=test-signature");

        var headers = HttpDiagnosticLoggingPolicy.FormatRequestHeaders(request.Headers);

        Assert.Equal("*****", headers["Authorization"]);
        Assert.Equal("*****", headers["x-ms-authorization-auxiliary"]);
        Assert.Equal("session=test-session", headers["Cookie"]);
        Assert.Equal("custom-value", headers["X-Custom"]);
        Assert.Equal("https://example.com/?sig=test-signature", headers["Location"]);
        Assert.Equal("Bearer test-token", Assert.Single(request.Headers.GetValues("Authorization")));
        Assert.Equal(["Bearer first", "Bearer second"], request.Headers.GetValues("x-ms-authorization-auxiliary"));
    }

    [Fact]
    public void ResponseHeaders_AreVisible_IncludingHeadersMaskedOnRequests()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=test-session");
        response.Headers.TryAddWithoutValidation("Authorization", "test-response-value");
        response.Headers.TryAddWithoutValidation("Location", "https://example.com/?continuation=one%2Btwo");

        var headers = HttpDiagnosticLoggingPolicy.FormatResponseHeaders(response.Headers);

        Assert.Equal("session=test-session", headers["Set-Cookie"]);
        Assert.Equal("test-response-value", headers["Authorization"]);
        Assert.Equal("https://example.com/?continuation=one%2Btwo", headers["Location"]);
    }

    [Theory]
    [InlineData("""{"properties":{"enabled":true,"items":[{"name":"First","value":"literal"}]}}""", "application/json")]
    [InlineData("""{"message":"Unknown field","enabled":true}""", "application/problem+json")]
    [InlineData("""{"error":{"code":"BadRequest","message":"Unknown field"}}""", "application/problem+json")]
    [InlineData("""{"allSettings":{"region":"west"},"appSettings":{"mode":"test"},"configuration":{"enabled":true},"connectionParameters":{"TenantId":"common"},"connectionParametersSet":{"name":"default"},"parameters":{"TenantId":"common"},"parameterValues":{"scope":"read"},"authorizationUrl":"https://example.com/authorize","consentLink":"https://example.com/consent","testLinks":["https://example.com/test"]}""", "application/json")]
    public void StructuredBodies_PreserveNonCredentialFields(string input, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(input);

        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes, contentType));
        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, contentType));
    }

    [Theory]
    [InlineData("{\"error\":\"unfinished", "application/json")]
    [InlineData("Service unavailable\r\nTry again later.", "text/plain")]
    [InlineData("scope=read+write&custom=first&custom=second&broken=%FF%ZZ", "application/x-www-form-urlencoded")]
    [InlineData("Body without a content type", null)]
    [InlineData("\"opaque credential-canary\"", "application/json")]
    [InlineData("[\"opaque credential-canary\"]", "application/json")]
    public void UnstructuredBodies_AreOmittedRatherThanEchoingPotentialCredentials(string input, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(input);

        Assert.Equal("[body omitted: no safe structured representation]", HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes, contentType));
        Assert.Equal("[body omitted: no safe structured representation]", HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, contentType));
    }

    [Fact]
    public void EmptyBodies_DistinguishMissingRequestBodyFromEmptyResponseContent()
    {
        Assert.Equal("This request has no body", HttpDiagnosticLoggingPolicy.FormatRequestBody([]));
        Assert.Equal(string.Empty, HttpDiagnosticLoggingPolicy.FormatResponseBody([]));
    }

    [Fact]
    public void StructuredBodies_RespectDeclaredCharsetBeforeRedaction()
    {
        const string input = "{\"name\":\"caf\u00e9\"}";

        using var request = JsonDocument.Parse(HttpDiagnosticLoggingPolicy.FormatRequestBody(
            Encoding.Unicode.GetBytes(input), "application/json; charset=\"utf-16\""));
        using var response = JsonDocument.Parse(HttpDiagnosticLoggingPolicy.FormatResponseBody(
            Encoding.Latin1.GetBytes(input), "text/plain; charset=iso-8859-1"));
        Assert.Equal("caf\u00e9", request.RootElement.GetProperty("name").GetString());
        Assert.Equal("caf\u00e9", response.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ChannelDiagnostics_ApplyProviderDeclaredSecretMetadataInsideNestedFields()
    {
        const string input = """{"properties":{"channelName":"DirectLineChannel","properties":{"directLineEmbedCode":"channel-credential-canary","sites":[{"siteName":"safe","key":"site-credential-canary"}]}}}""";
        var bytes = Encoding.UTF8.GetBytes(input);
        JsonElement Redact(JsonElement body) => HttpDiagnosticLoggingPolicy.RedactBody(body, resource =>
            BotServiceResponseRedactor.RedactChannel(resource,
                DeploymentChannelCatalog.FindChannelDefinitionOrDefault("DirectLineChannel", ChannelKeyType.ArmName)));
        var output = HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, bodyRedactor: Redact);
        Assert.Contains("DirectLineChannel", output);
        Assert.Contains("safe", output);
        Assert.DoesNotContain("channel-credential-canary", output);
        Assert.DoesNotContain("site-credential-canary", output);
    }

    [Theory]
    [InlineData("""{"properties":{"clientSecret":false,"parameters":{"ProviderSecret":"credential-canary"}}}""")]
    [InlineData("""{"items":[{"access_token":"credential-canary","secretText":"credential-canary","code":"non-secret-code"}]}""")]
    public void DiagnosticCopies_UseExistingCredentialRedactionRules(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        foreach (var output in new[] { HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes), HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes) })
        {
            Assert.DoesNotContain("credential-canary", output);
            Assert.Contains("*****", output);
        }
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("list")]
    [InlineData("array")]
    public void OAuthDiagnostics_ReuseCanonicalNameBasedRedactionWithoutProviderMetadata(string shape)
    {
        const string resource = """{"location":"global","properties":{"serviceProviderId":"provider-id","clientId":"client-1","clientSecret":"client-canary","scopes":"id:read","parameters":[{"key":"TenantId","value":"common"},{"key":"ApiKey","value":"provider-canary"}]}}""";
        using var source = JsonDocument.Parse(resource);
        var redacted = BotServiceResponseRedactor.RedactOAuth(source.RootElement)!.Value.GetRawText();
        string Wrap(string value) => shape switch
        {
            "list" => "{\"value\":[" + value + "],\"nextLink\":\"https://example.com/next\"}",
            "array" => "[" + value + "]",
            _ => value,
        };
        var bytes = Encoding.UTF8.GetBytes(Wrap(resource));
        JsonElement Redact(JsonElement body) => HttpDiagnosticLoggingPolicy.RedactBody(body, item =>
            BotServiceResponseRedactor.RedactOAuth(item)!.Value);

        Assert.Equal(Wrap(redacted), HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, bodyRedactor: Redact));
        Assert.Equal("provider-canary", source.RootElement.GetProperty("properties").GetProperty("parameters")[1].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("DirectLineChannel")]
    [InlineData("FutureChannel")]
    public void ChannelDiagnostics_MatchCanonicalRedactionWithoutExtraRules(string channelName)
    {
        var input = "{\"properties\":{\"channelName\":\"" + channelName +
            "\",\"properties\":{\"isEnabled\":true,\"opaque\":\"visible\",\"unknownToken\":false,\"directLineEmbedCode\":\"credential-canary\",\"sites\":[{\"siteName\":\"safe\",\"key\":\"site-canary\"}]}}}";
        using var source = JsonDocument.Parse(input);
        var definition = DeploymentChannelCatalog.FindChannelDefinitionOrDefault(channelName, ChannelKeyType.ArmName);
        var expected = BotServiceResponseRedactor.RedactChannel(source.RootElement, definition).GetRawText();
        var bytes = Encoding.UTF8.GetBytes(input);
        JsonElement Redact(JsonElement body) => HttpDiagnosticLoggingPolicy.RedactBody(body, resource =>
            BotServiceResponseRedactor.RedactChannel(resource, definition));

        Assert.Equal(expected, HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, bodyRedactor: Redact));
    }

    [Fact]
    public void OtherApiBodies_UseOnlySecretNamesWithoutInferringBotServiceSchemas()
    {
        const string body = """{"properties":{"channelName":"DirectLineChannel","serviceProviderId":"provider-id","parameters":{"TenantId":"common","apiKey":"credential-canary"},"allSettings":{"region":"west"},"code":"visible-code"}}""";
        var bytes = Encoding.UTF8.GetBytes(body);
        foreach (var output in new[] { HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes), HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes) })
        {
            using var document = JsonDocument.Parse(output);
            var properties = document.RootElement.GetProperty("properties");
            Assert.Equal("common", properties.GetProperty("parameters").GetProperty("TenantId").GetString());
            Assert.Equal("*****", properties.GetProperty("parameters").GetProperty("apiKey").GetString());
            Assert.Equal("west", properties.GetProperty("allSettings").GetProperty("region").GetString());
            Assert.Equal("visible-code", properties.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("application/octet-stream", "Body contains binary data.")]
    [InlineData("application/x-octet-stream", "Body contains binary data.")]
    [InlineData("IMAGE/PNG", "Body contains image data.")]
    public void NonTextBodies_UseMarkersInsteadOfDecodingContent(string contentType, string expected)
    {
        Assert.Equal(expected, HttpDiagnosticLoggingPolicy.FormatRequestBody([0xFF], contentType));
        Assert.Equal(expected, HttpDiagnosticLoggingPolicy.FormatResponseBody([0xFF], contentType));
    }

    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("application/json")]
    [InlineData(null)]
    public void StreamingUploads_UseMarkersWithoutCapturedContent(string? contentType)
    {
        Assert.Equal("File upload", HttpDiagnosticLoggingPolicy.FormatRequestBody(
            [], contentType, isStreaming: true));
    }

    [Fact]
    public void EventStreamResponses_AreNotCaptured()
    {
        Assert.Equal("Body is streamable", HttpDiagnosticLoggingPolicy.FormatResponseBody(
            [], "text/event-stream"));
    }

    [Theory]
    [InlineData("attachment; filename=\"result.zip\"", "result.zip")]
    [InlineData("attachment; filename*=UTF-8''result%20one.zip", "result one.zip")]
    public void Attachments_ShowFilenameInsteadOfBody(string disposition, string filename)
    {
        Assert.Equal("File attachments: " + filename, HttpDiagnosticLoggingPolicy.FormatResponseBody(
            [0xFF], "application/octet-stream", contentDisposition: disposition));
    }

    [Fact]
    public void StreamingBinaryBodies_DistinguishUploadFromResponse()
    {
        Assert.Equal("File upload", HttpDiagnosticLoggingPolicy.FormatRequestBody(
            [], "application/octet-stream", isStreaming: true));
        Assert.Equal("Body contains binary data.", HttpDiagnosticLoggingPolicy.FormatResponseBody(
            [], "application/octet-stream"));
    }

    [Theory]
    [InlineData("text/plain", "[body omitted: invalid text encoding]")]
    [InlineData("text/plain; charset=not-an-encoding", "[body omitted: unsupported encoding]")]
    public void UndecodableBodies_HaveExplicitMarkers(string contentType, string expected)
    {
        Assert.Equal(expected, HttpDiagnosticLoggingPolicy.FormatRequestBody([0xFF], contentType));
        Assert.Equal(expected, HttpDiagnosticLoggingPolicy.FormatResponseBody([0xFF], contentType));
    }

    [Fact]
    public void CaptureLimit_DoesNotPresentOversizedBodyAsComplete()
    {
        var input = "{\"name\":\"" + new string('x', HttpDiagnosticLoggingPolicy.MaximumBodyBytes - 11) + "\"}";

        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatResponseBody(Encoding.UTF8.GetBytes(input)));
        Assert.Equal("[body omitted: capture limit exceeded]",
            HttpDiagnosticLoggingPolicy.FormatResponseBody(Encoding.UTF8.GetBytes(input + "x")));
    }
}
