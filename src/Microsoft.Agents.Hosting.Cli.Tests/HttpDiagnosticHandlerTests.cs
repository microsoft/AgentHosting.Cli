using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class HttpDiagnosticHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticCopiesHideOAuthSecretsWithoutChangingTransmittedOrReturnedBodies(bool observeSerialization)
    {
        const string body = """{"properties":{"serviceProviderId":"provider-id","clientId":"client-1","clientSecret":"credential-canary","parameters":[{"key":"TenantId","value":"common"},{"key":"ApiKey","value":"provider-canary"}]}}""";
        var sink = new RecordingSink();
        using HttpContent content = observeSerialization
            ? JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(body))
            : new StringContent(body, Encoding.UTF8, "application/json");
        using var client = new HttpClient(new HttpDiagnosticHandler(sink, new CallbackHandler(async (request, token) =>
        {
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })));
        using var request = new HttpRequestMessage(HttpMethod.Put,
            "https://management.azure.com/subscriptions/sub/resourceGroups/group/providers/Microsoft.BotService/botServices/bot/connections/oauth")
        {
            Content = content,
        };
        request.Options.Set(HttpDiagnostics.ResponseBodyRedactor, value => HttpDiagnosticLoggingPolicy.RedactBody(value,
            resource => BotServiceResponseRedactor.RedactOAuth(resource)!.Value));

        using var response = await client.SendAsync(request);

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        foreach (var stage in new[] { HttpDiagnosticStage.RequestBody, HttpDiagnosticStage.ResponseBody })
        {
            var diagnostic = Assert.Single(sink.Events, entry => entry.Stage == stage);
            Assert.Contains("client-1", diagnostic.Body);
            Assert.Contains("common", diagnostic.Body);
            Assert.DoesNotContain("credential-canary", diagnostic.Body);
            Assert.DoesNotContain("provider-canary", diagnostic.Body);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseRedactor_IsNeverAppliedToRequestBodies(bool observeSerialization)
    {
        const string body = """{"clientSecret":"request-canary","parameters":[{"key":"TenantId","value":"common"},{"key":"ApiKey","value":"parameter-canary"}]}""";
        var sink = new RecordingSink();
        using HttpContent content = observeSerialization
            ? JsonContent.Create(JsonSerializer.Deserialize<JsonElement>(body))
            : new StringContent(body, Encoding.UTF8, "application/json");
        using var client = new HttpClient(new HttpDiagnosticHandler(sink, new CallbackHandler(async (request, token) =>
        {
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"result":"response"}""", Encoding.UTF8, "application/json") };
        })));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/requests") { Content = content };
        var responseRedactions = 0;
        request.Options.Set(HttpDiagnostics.ResponseBodyRedactor, response =>
        {
            responseRedactions++;
            Assert.Equal("response", response.GetProperty("result").GetString());
            return JsonSerializer.SerializeToElement(new { result = "redacted-response" });
        });

        using var response = await client.SendAsync(request);
        Assert.Equal("""{"result":"response"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, responseRedactions);
        using var diagnostic = JsonDocument.Parse(Assert.Single(sink.Events, entry => entry.Stage == HttpDiagnosticStage.RequestBody).Body!);
        Assert.Equal("*****", diagnostic.RootElement.GetProperty("clientSecret").GetString());
        var parameters = diagnostic.RootElement.GetProperty("parameters");
        Assert.Equal("TenantId", parameters[0].GetProperty("key").GetString());
        Assert.Equal("common", parameters[0].GetProperty("value").GetString());
        Assert.Equal("ApiKey", parameters[1].GetProperty("key").GetString());
        Assert.Equal("*****", parameters[1].GetProperty("value").GetString());
        Assert.Contains("redacted-response", Assert.Single(sink.Events, entry => entry.Stage == HttpDiagnosticStage.ResponseBody).Body);
    }

    [Fact]
    public async Task RequestIsFlushedBeforeBlockedTransportCompletes_AndUriIsNotRewritten()
    {
        using var output = new TrackingWriter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new HttpDiagnosticHandler(new TextWriterHttpDiagnosticSink(output),
            new CallbackHandler(async (_, token) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return new(HttpStatusCode.OK) { Content = new StringContent("response") };
            })));
        const string url = "https://example.com/bots?api-version=2022-09-15&$filter=name%20eq%20%27a%27&next=one%2btwo&next=%252F";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("{\"name\":\"literal\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-token");

        var pending = client.SendAsync(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted);
            Assert.Contains("Request: POST " + request.RequestUri!.AbsoluteUri, output.ToString());
            Assert.Equal(url, request.RequestUri.AbsoluteUri);
            Assert.Contains("Authorization: *****", output.ToString());
            Assert.Contains("Content-Type: application/json; charset=utf-8", output.ToString());
            Assert.Contains("{\"name\":\"literal\"}", output.ToString());
            Assert.DoesNotContain("test-token", output.ToString());
            Assert.Equal(2, output.Flushes);
        }
        finally
        {
            release.TrySetResult();
        }

        using var response = await pending;
        Assert.Equal("response", await response.Content.ReadAsStringAsync());
        Assert.Contains("Status: 200", output.ToString());
        Assert.Contains("Content:", output.ToString());
    }

    [Theory]
    [InlineData("buffer")]
    [InlineData("copy")]
    [InlineData("sync-copy")]
    public async Task ResponseHeadersArriveBeforeBodyRead_AndBodyIsUnchanged(string consumption)
    {
        var sink = new RecordingSink();
        var stream = new CountingStream(Encoding.UTF8.GetBytes("{\"error\":\"unfinished"));
        using var client = CreateClient(sink, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "service-request");
            return response;
        });
        using var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(0, stream.Reads);
        Assert.Contains(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseHeaders && e.StatusCode == 400);
        Assert.DoesNotContain(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        string text;
        if (consumption != "buffer")
        {
            using var target = new MemoryStream();
            if (consumption == "sync-copy")
            {
                response.Content.CopyTo(target, null, CancellationToken.None);
            }
            else
            {
                await response.Content.CopyToAsync(target);
            }
            Assert.True(target.CanWrite);
            text = Encoding.UTF8.GetString(target.ToArray());
        }
        else
        {
            text = await response.Content.ReadAsStringAsync();
        }

        Assert.Equal("{\"error\":\"unfinished", text);
        Assert.Equal("[body omitted: no safe structured representation]", Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
        Assert.True(Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).BodyComplete);
        Assert.Single(sink.Events.Select(e => e.RequestId).Distinct());
    }

    [Theory]
    [InlineData("text/event-stream", "Body is streamable")]
    [InlineData("application/octet-stream", "Body contains binary data.")]
    [InlineData("image/png", "Body contains image data.")]
    public async Task OmittedResponsesAreNotReadOrReplaced(string contentType, string marker)
    {
        var sink = new RecordingSink();
        using var original = new StreamContent(new CountingStream([1, 2, 3]));
        original.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var client = CreateClient(sink, () => new(HttpStatusCode.OK) { Content = original });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Same(original, response.Content);
        Assert.Equal(marker, Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UploadIsNotReadForDiagnostics()
    {
        var sink = new RecordingSink();
        var upload = new CountingStream(Encoding.UTF8.GetBytes("upload data"));
        using var transport = new CallbackHandler(async (request, token) =>
        {
            Assert.Equal(0, upload.Reads);
            Assert.Equal("File upload", Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.RequestBody).Body);
            Assert.Equal("upload data", await request.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent("accepted") };
        });
        using var client = new HttpClient(new HttpDiagnosticHandler(sink, transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com") { Content = new StreamContent(upload) };
        using var response = await client.SendAsync(request);
        Assert.Equal("accepted", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialConsumptionIsReportedOnce_AndDisposalReachesOriginalStream(bool synchronous)
    {
        var sink = new RecordingSink();
        var stream = new CountingStream(Encoding.UTF8.GetBytes("unread suffix"));
        using var client = CreateClient(sink, () => new(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);
        var body = synchronous ? response.Content.ReadAsStream() : await response.Content.ReadAsStreamAsync();
        try
        {
            Assert.Equal(stream.CanSeek, body.CanSeek);
            var buffer = new byte[2];
            var count = synchronous ? body.Read(buffer, 0, 2) : await body.ReadAsync(buffer);
            Assert.Equal(2, count);
            Assert.Equal("un", Encoding.UTF8.GetString(buffer));
        }
        finally
        {
            if (synchronous)
            {
                body.Dispose();
            }
            else
            {
                await body.DisposeAsync();
            }
        }

        response.Dispose();
        Assert.True(stream.Disposed);
        Assert.Contains("incomplete", Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
    }

    [Fact]
    public async Task OversizedResponsesAreDeliveredFully_ButNotRetainedInDiagnosticEvents()
    {
        var sink = new RecordingSink();
        var input = new string('x', HttpDiagnosticLoggingPolicy.MaximumBodyBytes * 3);
        using var client = CreateClient(sink, () => new(HttpStatusCode.OK) { Content = new StringContent(input) });
        using var response = await client.GetAsync("https://example.com");

        Assert.Equal(input, await response.Content.ReadAsStringAsync());
        Assert.Equal("[body omitted: capture limit exceeded]",
            Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonContentIsObservedDuringSerialization_WithoutChangingOwnershipOrLength(bool consumeStream)
    {
        var sink = new RecordingSink();
        using var original = JsonContent.Create(new { name = "example", enabled = true });
        Stream? borrowed = null;
        using var transport = new CallbackHandler(async (request, token) =>
        {
            Assert.DoesNotContain(sink.Events, e => e.Stage == HttpDiagnosticStage.RequestBody);
            Assert.Null(request.Content!.Headers.ContentLength);
            using var target = new MemoryStream();
            if (consumeStream)
            {
                borrowed = await request.Content.ReadAsStreamAsync(token);
                await borrowed.CopyToAsync(target, token);
            }
            else
            {
                await request.Content.CopyToAsync(target, token);
            }
            Assert.Equal("{\"name\":\"example\",\"enabled\":true}", Encoding.UTF8.GetString(target.ToArray()));
            return new(HttpStatusCode.OK) { Content = new StringContent("response") };
        });
        using var client = new HttpClient(new HttpDiagnosticHandler(sink, transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com") { Content = original };
        using var response = await client.SendAsync(request);

        Assert.Same(original, request.Content);
        Assert.Equal("{\"name\":\"example\",\"enabled\":true}",
            Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.RequestBody).Body);
        Assert.Equal("{\"name\":\"example\",\"enabled\":true}", await original.ReadAsStringAsync());
        if (consumeStream)
        {
            Assert.True(borrowed!.CanRead);
        }
    }

    [Fact]
    public async Task UnknownLengthBodyWaitsForEof_AndZeroByteReadsDoNotCompleteCapture()
    {
        var sink = new RecordingSink();
        using var client = CreateClient(sink, () =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream("example")) });
        using var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);
        Assert.Null(response.Content.Headers.ContentLength);
        using var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.DoesNotContain(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody);
        using var target = new MemoryStream();
        await stream.CopyToAsync(target);

        Assert.Equal("example", Encoding.UTF8.GetString(target.ToArray()));
        Assert.Equal("[body omitted: no safe structured representation]", Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
    }

    [Fact]
    public async Task ReadCancellationKeepsOriginalToken_AndIsNotReportedAsTimeout()
    {
        var sink = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = CreateClient(sink, () =>
            new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream("example")) });
        using var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var count = await stream.ReadAsync(new byte[4], cancellation.Token);
            Assert.Equal(0, count);
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(HttpDiagnosticFailure.Cancellation,
            Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.Failed).Failure);
    }

    [Fact]
    public async Task ReusingRequestForAnotherAttemptProducesNewCorrelation_WithoutNestedContentWrappers()
    {
        var sink = new RecordingSink();
        using var invoker = new HttpMessageInvoker(new HttpDiagnosticHandler(sink,
            new CallbackHandler(async (message, token) =>
            {
                using var destination = new MemoryStream();
                await message.Content!.CopyToAsync(destination, token);
                return new(HttpStatusCode.OK) { Content = new StringContent("response") };
            })));
        using var original = JsonContent.Create(new { name = "example" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com") { Content = original };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await invoker.SendAsync(request, CancellationToken.None);
            Assert.Equal("response", await response.Content.ReadAsStringAsync());
            Assert.Same(original, request.Content);
        }

        Assert.Equal(2, sink.Events.Count(e => e.Stage == HttpDiagnosticStage.Request));
        Assert.Equal(2, sink.Events.Select(e => e.RequestId).Distinct().Count());
        Assert.Equal(2, sink.Events.Count(e => e.Stage == HttpDiagnosticStage.ResponseBody));
        Assert.All(sink.Events.Where(e => e.Stage == HttpDiagnosticStage.RequestBody),
            e => Assert.Equal("{\"name\":\"example\"}", e.Body));
    }

    [Theory]
    [InlineData("GET", 200, 0)]
    [InlineData("HEAD", 200, 123)]
    [InlineData("GET", 204, 0)]
    [InlineData("GET", 304, 123)]
    public async Task BodylessResponsesCompleteAtHeaders_WithoutFalseIncompleteMarkers(string method, int status, int length)
    {
        var sink = new RecordingSink();
        using var client = CreateClient(sink, () =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = length;
            return response;
        });
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.com");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(string.Empty, Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).Body);
        Assert.True(Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseBody).BodyComplete);
    }

    [Theory]
    [InlineData("transport", HttpDiagnosticFailure.Transport)]
    [InlineData("timeout", HttpDiagnosticFailure.Timeout)]
    [InlineData("cancel", HttpDiagnosticFailure.Cancellation)]
    [InlineData("unexpected", HttpDiagnosticFailure.Transport)]
    public async Task FailuresAreClassifiedWithoutReplacingOriginalException(string kind, HttpDiagnosticFailure expected)
    {
        var sink = new RecordingSink();
        using var cancellation = new CancellationTokenSource();
        Exception original = kind switch
        {
            "transport" => new HttpRequestException("test failure"),
            "unexpected" => new InvalidOperationException("unexpected transport failure"),
            _ => new OperationCanceledException(),
        };
        using var invoker = new HttpMessageInvoker(new HttpDiagnosticHandler(sink, new CallbackHandler((_, _) =>
        {
            if (kind == "cancel")
            {
                cancellation.Cancel();
            }

            return Task.FromException<HttpResponseMessage>(original);
        })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => invoker.SendAsync(request, cancellation.Token));

        Assert.Same(original, actual);
        var diagnostic = Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.Failed);
        Assert.Equal(expected, diagnostic.Failure);
        Assert.Equal(original.GetType().Name, diagnostic.ExceptionType);
        Assert.Equal(original.Message, diagnostic.ExceptionMessage);
        Assert.DoesNotContain(sink.Events, e => e.Stage == HttpDiagnosticStage.ResponseHeaders);
    }

    [Theory]
    [InlineData(false, HttpDiagnosticFailure.Timeout)]
    [InlineData(true, HttpDiagnosticFailure.Cancellation)]
    public async Task LinkedDeadlineUsesTheOriginalCallerSignalWithoutReplacingTheException(bool cancelCaller, HttpDiagnosticFailure expected)
    {
        var sink = new RecordingSink();
        using var caller = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, deadline.Token);
        var original = new OperationCanceledException(linked.Token);
        using var invoker = new HttpMessageInvoker(new HttpDiagnosticHandler(sink, new CallbackHandler((_, _) =>
        {
            (cancelCaller ? caller : deadline).Cancel();
            return Task.FromException<HttpResponseMessage>(original);
        })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Options.Set(HttpDiagnostics.CallerCancellationToken, caller.Token);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => invoker.SendAsync(request, linked.Token));

        Assert.Same(original, actual);
        Assert.Equal(linked.Token, actual.CancellationToken);
        Assert.Equal(expected, Assert.Single(sink.Events, entry => entry.Stage == HttpDiagnosticStage.Failed).Failure);
    }

    [Fact]
    public async Task BodyReadFailureIsReported_AndOriginalIOExceptionIsPreserved()
    {
        var sink = new RecordingSink();
        var error = new IOException("test read error");
        using var client = CreateClient(sink, () => new(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream(error)) });
        using var response = await client.GetAsync("https://example.com", HttpCompletionOption.ResponseHeadersRead);
        using var body = await response.Content.ReadAsStreamAsync();

        var actual = await Assert.ThrowsAsync<IOException>(async () =>
        {
            var count = await body.ReadAsync(new byte[8]);
            Assert.Equal(0, count);
        });

        Assert.Same(error, actual);
        var diagnostic = Assert.Single(sink.Events, e => e.Stage == HttpDiagnosticStage.Failed);
        Assert.Equal(HttpDiagnosticFailure.Transport, diagnostic.Failure);
        Assert.Contains("incomplete", diagnostic.Body);
        Assert.Equal(nameof(IOException), diagnostic.ExceptionType);
        Assert.Equal(error.Message, diagnostic.ExceptionMessage);
    }

    [Fact]
    public async Task BrokenDiagnosticWriterDoesNotFailHttp_AndErrorRemainsInspectable()
    {
        using var writer = new FailingWriter();
        using var handler = new HttpDiagnosticHandler(new TextWriterHttpDiagnosticSink(writer),
            new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("response"),
            })));
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://example.com");

        Assert.Equal("response", await response.Content.ReadAsStringAsync());
        Assert.IsType<IOException>(handler.DiagnosticWriteError);
    }

    [Fact]
    public async Task ConcurrentCallsHaveIndependentIds_AndSinkWritesCompleteFlushedSections()
    {
        var sink = new RecordingSink();
        using var output = new TrackingWriter();
        var textSink = new TextWriterHttpDiagnosticSink(output);
        using var client = CreateClient(sink, () => new(HttpStatusCode.OK) { Content = new StringContent("response") });
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await client.GetAsync($"https://example.com/{index}");
        }));
        await Task.WhenAll(sink.Events.GroupBy(e => e.RequestId).Select(group => Task.Run(() =>
        {
            foreach (var diagnosticEvent in group)
            {
                textSink.Write(diagnosticEvent);
            }
        })));

        Assert.Equal(12, sink.Events.Select(e => e.RequestId).Distinct().Count());
        Assert.All(sink.Events.GroupBy(e => e.RequestId), group =>
            Assert.Equal(
                [HttpDiagnosticStage.Request, HttpDiagnosticStage.RequestBody, HttpDiagnosticStage.ResponseHeaders,
                 HttpDiagnosticStage.ResponseBody],
                group.Select(e => e.Stage)));
        Assert.Equal(36, output.Flushes);
        Assert.Equal(36, output.Sections.Count);
        Assert.Equal(12, output.Sections.Count(section => section.Contains("Response after", StringComparison.Ordinal)));
    }

    private static HttpClient CreateClient(
        RecordingSink sink, Func<HttpResponseMessage> response) =>
        new(new HttpDiagnosticHandler(sink, new CallbackHandler((_, _) => Task.FromResult(response()))));

    private sealed class RecordingSink : IHttpDiagnosticSink
    {
        public ConcurrentQueue<HttpDiagnosticEvent> Events { get; } = new();
        public void Write(HttpDiagnosticEvent diagnosticEvent) => Events.Enqueue(diagnosticEvent);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TrackingWriter : StringWriter
    {
        public int Flushes { get; private set; }
        public List<string> Sections { get; } = [];
        public override void Flush() => Flushes++;
        public override void Write(string? value)
        {
            Sections.Add(value!);
            base.Write(value);
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("test diagnostic writer unavailable");
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingStream(IOException error) : MemoryStream(new byte[8])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(error);
    }

    private sealed class NonSeekableStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
