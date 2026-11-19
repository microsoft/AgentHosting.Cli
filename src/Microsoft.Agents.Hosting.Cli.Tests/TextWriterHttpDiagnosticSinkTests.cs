using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TextWriterHttpDiagnosticSinkTests
{
    private static readonly Guid RequestId = Guid.Parse("55097603740341f490ac4858f764f9f3");
    private static readonly DateTimeOffset Started = new(2026, 11, 19, 23, 59, 59, 737, TimeSpan.Zero);

    [Fact]
    public void GroupsRawContentAndHeadersAtBodyCompletionWithUtcTimestampsAcrossMidnight()
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        sink.Write(Event(HttpDiagnosticStage.Request, 0) with
        {
            Timestamp = Started.ToOffset(TimeSpan.FromHours(-5)),
            Url = "https://example.com/?next=%252F&next=one%2btwo",
            Method = "GET",
            Headers = new Dictionary<string, string> { ["Authorization"] = "*****" },
        });
        sink.Write(Event(HttpDiagnosticStage.RequestBody, 1) with { Body = "This request has no body" });
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 1128) with
        {
            StatusCode = 403,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        });
        const string body = """{"error":{"code":"Forbidden", "message":"Denied"},"duplicate":1,"duplicate":2}""";
        Assert.DoesNotContain("Response", output.ToString());
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 1129) with { Body = body, BodyComplete = true });

        Assert.Equal(string.Join(Environment.NewLine,
        [
            $"2026-11-19 23:59:59.737 - {RequestId:N} > Request: GET https://example.com/?next=%252F&next=one%2btwo",
            "> Authorization: *****",
            "> Body: This request has no body",
            $"2026-11-20 00:00:00.866 - {RequestId:N} < Response after 1129.0 ms | Status: 403",
            "< Content-Type: application/json",
            "< Content: " + body,
            "",
        ]), output.ToString());
    }

    [Theory]
    [InlineData(HttpDiagnosticFailure.Transport)]
    [InlineData(HttpDiagnosticFailure.Timeout)]
    [InlineData(HttpDiagnosticFailure.Cancellation)]
    public void FailuresWithoutResponsesIncludeOnlyAvailableDetails(HttpDiagnosticFailure failure)
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        sink.Write(Event(HttpDiagnosticStage.Failed, 30000) with { Failure = failure });

        Assert.Contains($"- {RequestId:N} Failed after 30000.0 ms", output.ToString());
        Assert.Contains("Error: " + failure, output.ToString());
        Assert.DoesNotContain("Status:", output.ToString());
        Assert.DoesNotContain("Headers:", output.ToString());
        Assert.DoesNotContain("Content:", output.ToString());
        Assert.DoesNotContain("Elapsed:", output.ToString());
    }

    [Fact]
    public void BodyReadFailuresRetainPreviouslyReceivedStatusHeadersAndIncompleteMarker()
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output, color: true);
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 10) with
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["x-ms-request-id"] = "service-request" },
        });
        sink.Write(Event(HttpDiagnosticStage.Failed, 21) with
        {
            Body = "[body incomplete: response was not fully consumed]",
            BodyComplete = false,
            Failure = HttpDiagnosticFailure.Transport,
            ExceptionType = nameof(IOException),
            ExceptionMessage = "read failed\r\nadditional detail",
        });

        Assert.Contains("Status: 200", output.ToString());
        Assert.Contains("x-ms-request-id: service-request", output.ToString());
        Assert.Contains("Content: [body incomplete: response was not fully consumed]", output.ToString());
        Assert.Contains("Error: Transport", output.ToString());
        Assert.Contains("Exception: IOException" + Environment.NewLine, output.ToString());
        Assert.Contains("Message: read failed\r\nadditional detail" + Environment.NewLine, output.ToString());
        Assert.DoesNotContain("Response after", output.ToString());
        var coloredLine = Assert.Single(output.ToString().Split(Environment.NewLine), line => line.Contains('\u001b'));
        Assert.Equal($"\u001b[90m2026-11-19 23:59:59.758 - {RequestId:N}\u001b[0m \u001b[31mFailed after 21.0 ms | Status: 200\u001b[0m", coloredLine);
    }

    [Fact]
    public void UnobservedBodiesAreReportedImmediatelyWithoutClaimingBodyCompletion()
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 10) with { StatusCode = 200 });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 11) with { Body = "Body is streamable" });

        Assert.Contains($"- {RequestId:N} < Response (body completion not observed)", output.ToString());
        Assert.Contains("Status: 200", output.ToString());
        Assert.Contains("Content: Body is streamable", output.ToString());
        Assert.DoesNotContain("after", output.ToString());
    }

    [Fact]
    public void PartialConsumptionDoesNotClaimCompleteBody()
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 10) with { StatusCode = 200 });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 21) with
        {
            Body = "[body incomplete: response was not fully consumed]",
            BodyComplete = false,
        });

        Assert.Contains("Response after 21.0 ms (body incomplete)", output.ToString());
        Assert.Contains("Content: [body incomplete: response was not fully consumed]", output.ToString());
    }

    [Fact]
    public void InterleavedRequestsAndResponsesKeepTheirOriginalGuidAndContent()
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        var otherId = Guid.NewGuid();
        sink.Write(Event(HttpDiagnosticStage.Request, 0) with { Method = "POST" });
        sink.Write(Event(HttpDiagnosticStage.Request, 1) with { RequestId = otherId, Method = "GET" });
        sink.Write(Event(HttpDiagnosticStage.RequestBody, 2) with { Body = "first request" });
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 3) with { StatusCode = 201 });
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 4) with { RequestId = otherId, StatusCode = 403 });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 6) with
        {
            RequestId = otherId,
            Body = "{broken json",
            BodyComplete = true,
        });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 8) with { Body = "line one\nline two", BodyComplete = true });

        var text = output.ToString();
        Assert.Contains($"- {RequestId:N} > Request (continued){Environment.NewLine}> Body: first request", text);
        Assert.Contains($"- {otherId:N} < Response after 6.0 ms | Status: 403{Environment.NewLine}< Content: {{broken json", text);
        Assert.Contains($"- {RequestId:N} < Response after 8.0 ms | Status: 201{Environment.NewLine}< Content: line one\n< line two", text);
        Assert.DoesNotContain(Environment.NewLine + Environment.NewLine, text);
    }

    [Theory]
    [InlineData(false, 200, "32")]
    [InlineData(true, 200, "32")]
    [InlineData(true, 302, "33")]
    [InlineData(true, 400, "31")]
    [InlineData(true, 500, "31")]
    public void OptionalColorsSeparateGrayMetadataFromRequestAndResponseHeadingMessages(bool color, int status, string statusColor)
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output, color);
        sink.Write(Event(HttpDiagnosticStage.Request, 0) with
        {
            Method = "GET",
            Url = "https://example.com",
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
        });
        sink.Write(Event(HttpDiagnosticStage.RequestBody, 1) with { Body = "This request has no body" });
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 10) with
        {
            StatusCode = status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 21) with { Body = "{\"raw\":true}", BodyComplete = true });

        var text = output.ToString();
        if (color)
        {
            var coloredLines = text.Split(Environment.NewLine).Where(line => line.Contains('\u001b')).ToArray();
            Assert.Equal(2, coloredLines.Length);
            Assert.Equal($"\u001b[90m2026-11-19 23:59:59.737 - {RequestId:N}\u001b[0m > \u001b[34mRequest: GET \u001b[33mhttps://example.com\u001b[0m", coloredLines[0]);
            Assert.Equal($"\u001b[90m2026-11-19 23:59:59.758 - {RequestId:N}\u001b[0m < \u001b[{statusColor}mResponse after 21.0 ms | Status: {status}\u001b[0m", coloredLines[1]);
        }
        else
        {
            Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        }

        Assert.Contains("> Accept: application/json" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("> Body: This request has no body" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("< Content-Type: application/json" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("< Content: {\"raw\":true}" + Environment.NewLine, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("body without a trailing newline")]
    [InlineData("one\r\n")]
    [InlineData("one\r")]
    [InlineData("first\r\n\r\n  second\nthird\rfourth\n")]
    public void DirectionPrefixesPreserveBodyWhitespaceAndLineEndings(string body)
    {
        using var output = new StringWriter();
        var sink = new TextWriterHttpDiagnosticSink(output);
        sink.Write(Event(HttpDiagnosticStage.Request, 0) with { Method = "POST", Url = "https://example.com" });
        sink.Write(Event(HttpDiagnosticStage.RequestBody, 1) with { Body = body });
        sink.Write(Event(HttpDiagnosticStage.ResponseHeaders, 10) with { StatusCode = 200 });
        sink.Write(Event(HttpDiagnosticStage.ResponseBody, 21) with { Body = body, BodyComplete = true });

        var text = output.ToString();
        var requestBodyStart = text.IndexOf("> Body: ", StringComparison.Ordinal) + "> Body: ".Length;
        var responseStart = text.IndexOf($"2026-11-19 23:59:59.758 - {RequestId:N} <", requestBodyStart, StringComparison.Ordinal);
        var capturedRequest = text[requestBodyStart..responseStart];
        var responseBodyStart = text.IndexOf("< Content: ", StringComparison.Ordinal) + "< Content: ".Length;
        var capturedResponse = text[responseBodyStart..];
        if (body.Length == 0 || body[^1] is not ('\r' or '\n'))
        {
            capturedRequest = capturedRequest[..^Environment.NewLine.Length];
            capturedResponse = capturedResponse[..^Environment.NewLine.Length];
        }
        Assert.Equal(body, capturedRequest.Replace("\n> ", "\n", StringComparison.Ordinal).Replace("\r> ", "\r", StringComparison.Ordinal));
        Assert.Equal(body, capturedResponse.Replace("\n< ", "\n", StringComparison.Ordinal).Replace("\r< ", "\r", StringComparison.Ordinal));
    }

    [Fact]
    public void DurationsUseInvariantFormattingRegardlessOfCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var output = new StringWriter();
            var sink = new TextWriterHttpDiagnosticSink(output);
            sink.Write(Event(HttpDiagnosticStage.Failed, 1234.5) with { Failure = HttpDiagnosticFailure.Timeout });

            Assert.Contains("Failed after 1234.5 ms", output.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task RealBodyConsumptionControlsWhenResponseIsPrinted()
    {
        using var output = new StringWriter();
        var clock = new ManualClock();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("response"));
        using var client = new HttpClient(new HttpDiagnosticHandler(new TextWriterHttpDiagnosticSink(output),
            new ResponseHandler(() =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(10));
                return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            }), timeProvider: clock));
        using var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);
        Assert.DoesNotContain("Response", output.ToString());
        clock.Advance(TimeSpan.FromMilliseconds(40));

        Assert.Equal("response", await response.Content.ReadAsStringAsync());

        Assert.Contains("2026-11-19 23:59:59.787", output.ToString());
        Assert.Contains("Response after 50.0 ms", output.ToString());
        Assert.DoesNotContain("Completed", output.ToString());
        Assert.DoesNotContain("Timing:", output.ToString());
    }

    private static HttpDiagnosticEvent Event(HttpDiagnosticStage stage, double milliseconds) =>
        new(RequestId, Started.AddMilliseconds(milliseconds), stage) { Elapsed = TimeSpan.FromMilliseconds(milliseconds) };

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan _elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => Started + _elapsed;
        public void Advance(TimeSpan duration) => _elapsed += duration;
    }
}
