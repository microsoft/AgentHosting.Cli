using System.Text;
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
    [InlineData("""{"error":{"code":"BadRequest","message":"Unknown field","details":"See https://example.com/?code=42"}}""", "application/problem+json")]
    [InlineData("{\"error\":\"unfinished", "application/json")]
    [InlineData("Service unavailable\r\nTry again later.", "text/plain")]
    [InlineData("scope=read+write&custom=first&custom=second&broken=%FF%ZZ", "application/x-www-form-urlencoded")]
    [InlineData("Body without a content type", null)]
    public void TextBodies_ArePreservedWithoutSchemaApprovalOrParsing(string input, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(input);

        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatRequestBody(bytes, contentType));
        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatResponseBody(bytes, contentType));
    }

    [Fact]
    public void EmptyBodies_DistinguishMissingRequestBodyFromEmptyResponseContent()
    {
        Assert.Equal("This request has no body", HttpDiagnosticLoggingPolicy.FormatRequestBody([]));
        Assert.Equal(string.Empty, HttpDiagnosticLoggingPolicy.FormatResponseBody([]));
    }

    [Fact]
    public void TextBodies_RespectDeclaredCharset_WithoutChangingWhitespace()
    {
        const string input = "  caf\u00e9\r\n";

        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatRequestBody(
            Encoding.Unicode.GetBytes(input), "application/json; charset=\"utf-16\""));
        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatResponseBody(
            Encoding.Latin1.GetBytes(input), "text/plain; charset=iso-8859-1"));
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
        var input = new string('x', HttpDiagnosticLoggingPolicy.MaximumBodyBytes);

        Assert.Equal(input, HttpDiagnosticLoggingPolicy.FormatResponseBody(Encoding.UTF8.GetBytes(input)));
        Assert.Equal("[body omitted: capture limit exceeded]",
            HttpDiagnosticLoggingPolicy.FormatResponseBody(Encoding.UTF8.GetBytes(input + "x")));
    }
}
