using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class UpdateCheckTests
{
    [Fact]
    public async Task Discovery_RequestsListedPrereleasesAndSelectsNumericMaxima()
    {
        using var fixture = new UpdateFixture("1.0.0", "1.1.0-rc.2", "1.1.0-rc.10", "0.9.9", "bad");
        var versions = await fixture.Client.GetAsync(CancellationToken.None);

        Assert.Equal("1.0.0", versions!.Stable);
        Assert.Equal("1.1.0-rc.10", versions.Prerelease);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Contains("prerelease=true", fixture.Requests[1].Query);
        Assert.Contains("semVerLevel=2.0.0", fixture.Requests[1].Query);
        Assert.Contains("packageid%3AMicrosoft.Agents.Hosting.Cli", fixture.Requests[1].Query);
    }

    [Theory]
    [InlineData("""{"resources":[]}""", null)]
    [InlineData("""{"resources":[{"@type":"SearchQueryService","@id":"https://untrusted.example/query"}]}""", null)]
    [InlineData(null, """{"data":[{"id":"Other.Package","versions":[{"version":"9.0.0"}]}]}""")]
    [InlineData(null, """{"data":[]}""")]
    [InlineData(null, """{"data":[{"id":"Microsoft.Agents.Hosting.Cli","versions":[{"version":"bad"}]}]}""")]
    [InlineData(null, """{"data":42}""")]
    [InlineData(null, "not json")]
    public async Task UnusableMetadata_ProducesNoResult(string? index, string? search)
    {
        using var fixture = new UpdateFixture("1.0.0") { Index = index, Search = search };
        Assert.Null(await fixture.Client.GetAsync(CancellationToken.None));
        Assert.All(fixture.Requests, uri => Assert.EndsWith(".nuget.org", uri.Host));
    }

    [Fact]
    public async Task OversizedResponse_IsIgnored()
    {
        using var fixture = new UpdateFixture("1.0.0") { Search = new string(' ', 1024 * 1024 + 1) };
        Assert.Null(await fixture.Client.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Deadline_IsSharedByDiscoveryAndSearch()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var searching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeResponse = async (uri, token) =>
        {
            if (uri.Host == "api.nuget.org")
            {
                fixture.Time.Advance(TimeSpan.FromSeconds(2));
            }
            else
            {
                searching.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var pending = fixture.Client.GetAsync(CancellationToken.None);
        await searching.Task;
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, fixture.Time.ActiveTimers);
    }

    [Fact]
    public async Task UserCancellation_IsNotSwallowed()
    {
        using var fixture = new UpdateFixture("1.0.0");
        using var cancellation = new CancellationTokenSource();
        fixture.BeforeResponse = (_, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GetAsync(cancellation.Token));
    }

    [Fact]
    public async Task Deadline_CancelsResponseBodyRead()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Content = uri => uri.Host == "api.nuget.org"
            ? null
            : new StreamContent(new BlockingReadStream(reading));
        var pending = fixture.Client.GetAsync(CancellationToken.None);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, fixture.Time.ActiveTimers);
    }

    [Theory]
    [InlineData("0.9.0+commit", "1.0.0", "1.1.0-beta", true, true)]
    [InlineData("1.0.0+commit", "1.0.0", "1.0.0-rc.1", false, false)]
    [InlineData("1.0.0-beta+commit", "1.0.0", "1.0.0-rc.1", true, false)]
    [InlineData("2.0.0+commit", "1.0.0", "1.1.0-beta", false, false)]
    [InlineData("0.9.0", "1.0.0-beta", "1.0.0-rc.1", false, true)]
    public async Task Notice_UsesExecutableBaselineAndLabelsNewerChannels(
        string current, string first, string second, bool stable, bool preview)
    {
        using var fixture = new UpdateFixture(first, second);
        var output = new StringWriter();
        await fixture.Service(current).NotifyAsync(output, CancellationToken.None);

        Assert.Equal(stable, output.ToString().Contains("Update: dotnet tool update Microsoft.Agents.Hosting.Cli --global", StringComparison.Ordinal));
        Assert.Equal(preview, output.ToString().Contains("(prerelease)", StringComparison.Ordinal));
        Assert.Equal(preview, output.ToString().Contains("--global --prerelease", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cache_ReusesMetadataAcrossExecutableVersionsAndExpiresAfterOneDay()
    {
        using var fixture = new UpdateFixture("1.0.0", "1.1.0-beta");
        await fixture.Service("0.9.0").NotifyAsync(new StringWriter(), CancellationToken.None);
        var updatedOutput = new StringWriter();
        await fixture.Service("2.0.0").NotifyAsync(updatedOutput, CancellationToken.None);
        Assert.Empty(updatedOutput.ToString());
        Assert.Equal(2, fixture.Requests.Count);

        fixture.Time.Advance(TimeSpan.FromHours(24));
        await fixture.Service("0.9.0").NotifyAsync(new StringWriter(), CancellationToken.None);
        Assert.Equal(4, fixture.Requests.Count);
    }

    [Fact]
    public async Task FailedCheck_IsSilentAndBackedOffForOneHour()
    {
        using var fixture = new UpdateFixture("1.0.0") { Status = HttpStatusCode.ServiceUnavailable };
        var output = new StringWriter();
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        Assert.Empty(output.ToString());
        Assert.Single(fixture.Requests);

        fixture.Time.Advance(TimeSpan.FromHours(1));
        fixture.Status = HttpStatusCode.OK;
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        Assert.Contains("1.0.0", output.ToString());
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task InvalidBaseline_DoesNotContactFeed()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var output = new StringWriter();
        await fixture.Service("not-a-version").NotifyAsync(output, CancellationToken.None);
        Assert.Empty(output.ToString());
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"packageId":"Microsoft.Agents.Hosting.Cli","source":"https://api.nuget.org/v3/index.json","checkedAt":"1970-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"Microsoft.Agents.Hosting.Cli","source":"https://api.nuget.org/v3/index.json","checkedAt":"2030-01-01T00:00:00+00:00","versions":{"stable":"999.0.0","prerelease":null}}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"Other.Package","source":"https://api.nuget.org/v3/index.json","checkedAt":"1970-01-01T00:00:00+00:00","versions":{"stable":"999.0.0","prerelease":null}}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"Microsoft.Agents.Hosting.Cli","source":"https://other.example/index.json","checkedAt":"1970-01-01T00:00:00+00:00","versions":{"stable":"999.0.0","prerelease":null}}""")]
    [InlineData("""{"schemaVersion":1,"packageId":"Microsoft.Agents.Hosting.Cli","source":"https://api.nuget.org/v3/index.json","checkedAt":"1970-01-01T00:00:00+00:00","versions":{"stable":"bad","prerelease":null}}""")]
    public async Task InvalidCache_IsIgnoredAndRefreshed(string json)
    {
        using var fixture = new UpdateFixture("1.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachePath)!);
        await File.WriteAllTextAsync(fixture.CachePath, json);
        var output = new StringWriter();
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Contains("1.0.0", output.ToString());
        Assert.DoesNotContain("999.0.0", output.ToString());
    }

    [Fact]
    public async Task ExpiredMetadata_WithFailedRefreshDoesNotEmitStaleNotice()
    {
        using var fixture = new UpdateFixture("1.0.0");
        await fixture.Service("0.9.0").NotifyAsync(new StringWriter(), CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromHours(24));
        fixture.Status = HttpStatusCode.ServiceUnavailable;
        var output = new StringWriter();
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        Assert.Empty(output.ToString());
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public async Task UnwritableCache_DoesNotPreventNoticeOrCommand()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var updates = Path.GetDirectoryName(fixture.CachePath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(updates)!);
        await File.WriteAllTextAsync(updates, "not a directory");
        var stderr = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        Assert.Equal(0, await cli.InvokeAsync(["auth", "status"], new StringWriter(), stderr, color: false));
        Assert.Contains("1.0.0", stderr.ToString());
    }

    [Fact]
    public async Task ConcurrentRefreshes_LeaveReusableMetadataAndNoTemporaryFiles()
    {
        using var fixture = new UpdateFixture("1.0.0");
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => fixture.Service("0.9.0").NotifyAsync(new StringWriter(), CancellationToken.None)));
        var requests = fixture.Requests.Count;
        var output = new StringWriter();
        await fixture.Service("0.9.0").NotifyAsync(output, CancellationToken.None);
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Contains("1.0.0", output.ToString());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.CachePath)!));
    }

    [Fact]
    public async Task AdvisoryWriterFailure_DoesNotFailCommand()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        Assert.Equal(0, await cli.InvokeAsync(["auth", "status"], new StringWriter(), new FailingWriter(), color: false));
    }

    [Fact]
    public async Task Host_UserCancellationIsPreservedWithoutRequest()
    {
        using var fixture = new UpdateFixture("1.0.0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cli.InvokeAsync(["auth", "status"], new StringWriter(), new StringWriter(), color: false, cancellation.Token));
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(true, false, null, null, false)]
    [InlineData(false, true, null, null, false)]
    [InlineData(false, false, "true", null, false)]
    [InlineData(false, false, "1", null, false)]
    [InlineData(false, false, "false", null, true)]
    [InlineData(false, false, "0", null, true)]
    [InlineData(false, false, null, "TRUE", false)]
    [InlineData(false, false, null, "1", false)]
    [InlineData(false, false, null, "false", true)]
    public void Policy_SkipsAutomationAndExplicitOptOut(bool stdout, bool stderr, string? ci, string? skip, bool expected) =>
        Assert.Equal(expected, UpdateCheckService.ShouldCheck(stdout, stderr, ci, skip));

    [Theory]
    [InlineData("auth", "status", "--json")]
    [InlineData("auth", "status", "--help", "--json")]
    [InlineData("--help", "--json")]
    [InlineData("--version")]
    [InlineData("--help", "--version")]
    [InlineData("auth")]
    [InlineData("unknown")]
    [InlineData("unknown", "--help")]
    [InlineData("auth", "status", "--unknown")]
    [InlineData("auth", "status", "--unknown", "--help")]
    [InlineData("agent", "eval")]
    [InlineData("auth", "status", "-j")]
    public async Task Host_SkippedPathsMakeNoRequests(params string[] args)
    {
        using var fixture = new UpdateFixture("999.0.0");
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        await cli.InvokeAsync(args, new StringWriter(), new StringWriter(), color: false);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("auth", "--help")]
    [InlineData("auth", "status", "--help")]
    [InlineData("auth", "switch", "-h")]
    [InlineData("-?")]
    [InlineData()]
    public async Task Host_HelpShowsNoticeBeforeOutputAndPreservesHelp(params string[] args)
    {
        using var fixture = new UpdateFixture("1.0.0");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        fixture.BeforeResponse = (_, _) =>
        {
            Assert.Empty(stdout.ToString());
            return Task.CompletedTask;
        };
        var expectedOutput = new StringWriter();
        var baseline = new AgentHostCli(new FakeOperations());
        Assert.Equal(0, await baseline.InvokeAsync(args, expectedOutput, new StringWriter(), color: false));
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));

        Assert.Equal(0, await cli.InvokeAsync(args, stdout, stderr, color: false));

        Assert.Contains("dotnet tool update Microsoft.Agents.Hosting.Cli --global", stderr.ToString());
        Assert.Equal(expectedOutput.ToString(), stdout.ToString());
        Assert.Contains("Usage:", stdout.ToString());
    }

    [Fact]
    public async Task Host_NoticePrecedesHandlerAndPreservesExitAndStdout()
    {
        using var fixture = new UpdateFixture("1.0.0");
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                Assert.Contains("dotnet tool update Microsoft.Agents.Hosting.Cli --global", stderr.ToString());
                return new AuthStatusReport { Status = "signed-out" };
            },
        };
        var cli = new AgentHostCli(operations, telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        Assert.Equal(0, await cli.InvokeAsync(["auth", "status", "--non-interactive"], stdout, stderr, color: false));
        Assert.DoesNotContain("dotnet tool", stdout.ToString());
    }

    [Fact]
    public async Task Host_FeedFailureIsSilentEvenWithDebug()
    {
        using var fixture = new UpdateFixture("1.0.0") { Status = HttpStatusCode.Forbidden };
        var stderr = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        Assert.Equal(0, await cli.InvokeAsync(["auth", "status", "--debug"], new StringWriter(), stderr, color: false));
        Assert.Empty(stderr.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_CorruptCompressedResponseDoesNotPreventCommand(bool corruptIndex)
    {
        using var fixture = new UpdateFixture("1.0.0");
        fixture.Content = uri => (uri.Host == "api.nuget.org") == corruptIndex
            ? new StreamContent(new GZipStream(new MemoryStream(Encoding.UTF8.GetBytes("invalid gzip body")), CompressionMode.Decompress))
            : null;
        var stderr = new StringWriter();
        var cli = new AgentHostCli(new FakeOperations(), telemetryFactory: null, updateCheck: fixture.Service("0.9.0"));
        Assert.Equal(0, await cli.InvokeAsync(["auth", "status", "--debug"], new StringWriter(), stderr, color: false));
        Assert.Empty(stderr.ToString());
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "msagent-update-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly string[] _versions;

        internal UpdateFixture(params string[] versions)
        {
            _versions = versions;
            _http = new HttpClient(new Handler(RespondAsync)) { Timeout = Timeout.InfiniteTimeSpan };
            Client = new NuGetUpdateClient(_http, Time);
        }

        internal ManualTimeProvider Time { get; } = new();
        internal NuGetUpdateClient Client { get; }
        internal List<Uri> Requests { get; } = [];
        internal string CachePath => Path.Combine(_directory, "updates", "version-check.json");
        internal string? Index { get; init; }
        internal string? Search { get; init; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal Func<Uri, CancellationToken, Task>? BeforeResponse { get; set; }
        internal Func<Uri, HttpContent?>? Content { get; set; }
        internal UpdateCheckService Service(string current) => new(Client, _directory, current, Time);

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri);
            }
            Assert.Null(request.Headers.Authorization);
            if (BeforeResponse is not null)
            {
                await BeforeResponse(uri, token);
            }

            var json = uri.Host == "api.nuget.org"
                ? Index ?? """{"resources":[{"@type":"SearchQueryService","@id":"https://azuresearch-usnc.nuget.org/query"}]}"""
                : Search ?? JsonSerializer.Serialize(new
                {
                    totalHits = 1,
                    data = new[]
                    {
                        new
                        {
                            id = "Microsoft.Agents.Hosting.Cli",
                            versions = _versions.Select(version => new { version }),
                        },
                    },
                });
            return new HttpResponseMessage(Status)
            {
                Content = Content?.Invoke(uri) ?? new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        public void Dispose()
        {
            _http.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class BlockingReadStream(TaskCompletionSource reading) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("The advisory output is unavailable."));
    }
}
