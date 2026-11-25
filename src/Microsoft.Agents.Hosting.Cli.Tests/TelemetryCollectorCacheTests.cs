using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TelemetryCollectorCacheTests
{
    private const string ApiKey = "00000000000000000000000000000000-11111111-2222-3333-4444-555555555555-6666";
    private static readonly ResolvedEnvironmentTarget Environment = new("environment-one", "tenant-one");

    [Theory]
    [InlineData("europe", "Europe")]
    [InlineData(" United_States EUAP ", "Global")]
    public async Task ConfirmedDecisionSurvivesNewInstancesAndSkipsLookup(string geography, string expected)
    {
        using var fixture = new Fixture();
        var client = new GeographyClient((_, _) => Task.FromResult<string?>(geography));
        Assert.Equal(expected, (await fixture.ResolveAsync(client))!.Location.ToString());
        var trace = new StringWriter();
        var diagnostics = new TelemetryRoutingDiagnostics(trace);

        Assert.Equal(expected, (await fixture.ResolveAsync(client, diagnostics: diagnostics))!.Location.ToString());

        Assert.Equal(1, client.Calls);
        Assert.Contains("stage=cache-hit collector=" + (expected == "Europe" ? "EU" : "Global"), trace.ToString());
        string json = await File.ReadAllTextAsync(fixture.CachePath);
        Assert.DoesNotContain(Environment.EnvironmentId, json);
        Assert.DoesNotContain(Environment.TenantId, json);
        Assert.DoesNotContain(ApiKey, json);
        Assert.DoesNotContain("http", json);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.CachePath)!));
    }

    [Fact]
    public async Task CachedDecisionExpiresAtExactlyTwentyFourHours()
    {
        using var fixture = new Fixture();
        var client = new GeographyClient((_, _) => Task.FromResult<string?>("europe"));
        await fixture.ResolveAsync(client);
        fixture.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        await fixture.ResolveAsync(client);
        Assert.Equal(1, client.Calls);

        fixture.Time.Advance(TimeSpan.FromTicks(1));
        var trace = new StringWriter();
        var result = await fixture.ResolveAsync(
            new GeographyClient((_, _) => Task.FromResult<string?>(null)), diagnostics: new(trace));

        Assert.Equal(TelemetryLocation.Global, result!.Location);
        Assert.Contains("stage=cache-expired", trace.ToString());
        Assert.Contains("geography=missing", trace.ToString());
        Assert.Null(await fixture.Cache().ReadAsync(PowerPlatformApiTargetConfig.Public, Environment, default));
    }

    [Fact]
    public async Task CacheIsScopedByCloudRingTenantAndEnvironment()
    {
        using var fixture = new Fixture();
        var cache = fixture.Cache();
        await cache.WriteAsync(PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Europe, default);

        Assert.Null(await cache.ReadAsync(PowerPlatformApiTargetConfig.Test, Environment, default));
        Assert.Null(await cache.ReadAsync(PowerPlatformApiTargetConfig.Public with { Cloud = CloudInstance.UsGov }, Environment, default));
        Assert.Null(await cache.ReadAsync(PowerPlatformApiTargetConfig.Public, new(Environment.EnvironmentId, "tenant-two"), default));
        Assert.Null(await cache.ReadAsync(PowerPlatformApiTargetConfig.Public, new("environment-two", Environment.TenantId), default));
        Assert.Equal(TelemetryLocation.Europe, await cache.ReadAsync(PowerPlatformApiTargetConfig.Public,
            new(Environment.EnvironmentId.ToUpperInvariant(), Environment.TenantId.ToUpperInvariant()), default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-private-geography")]
    [InlineData("throw")]
    public async Task FallbackDecisionsAreNeverCached(string? geography)
    {
        using var fixture = new Fixture();
        var client = new GeographyClient((_, _) => geography == "throw"
            ? throw new IOException("private-failure")
            : Task.FromResult(geography));
        await fixture.ResolveAsync(client);
        await fixture.ResolveAsync(client);

        Assert.Equal(2, client.Calls);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task CachedGlobalAndFreshEuropeRetainAnyEuropeAggregation()
    {
        using var fixture = new Fixture();
        await fixture.Cache().WriteAsync(PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Global, default);
        var second = new ResolvedEnvironmentTarget("environment-two", Environment.TenantId);
        var client = new GeographyClient((target, _) =>
        {
            Assert.Equal(second, target);
            return Task.FromResult<string?>("europe");
        });

        var result = await fixture.ResolveAsync(client, target: TelemetryEnvironmentTarget.Resolved([Environment, second]));

        Assert.Equal(TelemetryLocation.Europe, result!.Location);
        Assert.Equal(1, client.Calls);
        Assert.Equal(TelemetryLocation.Europe, await fixture.Cache().ReadAsync(PowerPlatformApiTargetConfig.Public, second, default));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("nonproduction")]
    [InlineData("sovereign")]
    [InlineData("invalid-key")]
    public async Task CacheCannotBypassExistingRoutingGates(string gate)
    {
        using var fixture = new Fixture();
        await fixture.Cache().WriteAsync(PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Europe, default);
        var output = new StringWriter();
        var client = new GeographyClient((_, _) => throw new InvalidOperationException("No lookup expected."));
        var result = await fixture.ResolveAsync(client, diagnostics: new(output),
            target: gate == "incomplete" ? TelemetryEnvironmentTarget.Unknown : null,
            apiTarget: gate switch
            {
                "nonproduction" => PowerPlatformApiTargetConfig.Test,
                "sovereign" => PowerPlatformApiTargetConfig.Public with { Cloud = CloudInstance.UsGov },
                _ => null,
            },
            apiKey: gate == "invalid-key" ? "" : ApiKey);

        Assert.Equal(0, client.Calls);
        Assert.DoesNotContain("stage=cache-", output.ToString());
        if (gate is "sovereign" or "invalid-key")
            Assert.Null(result);
        else
            Assert.Equal(TelemetryLocation.Global, result!.Location);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":2,"policyVersion":1,"entries":[]}""")]
    [InlineData("""{"schemaVersion":1,"policyVersion":2,"entries":[]}""")]
    [InlineData("""{"schemaVersion":1,"policyVersion":1,"entries":null}""")]
    public async Task InvalidCacheIsRefreshedWithoutChangingLiveRoute(string json)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachePath)!);
        await File.WriteAllTextAsync(fixture.CachePath, json);
        var client = new GeographyClient((_, _) => Task.FromResult<string?>("europe"));
        await fixture.ResolveAsync(client);
        await fixture.ResolveAsync(client);

        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("invalid-collector")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public async Task InvalidPersistedEntriesCannotOverrideLiveDecision(string invalid)
    {
        using var fixture = new Fixture();
        await fixture.Cache().WriteAsync(PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Europe, default);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!;
        var entry = document["entries"]![0]!;
        switch (invalid)
        {
            case "future":
                entry["checkedAt"] = fixture.Time.GetUtcNow().AddDays(1);
                break;
            case "invalid-collector":
                entry["collector"] = "https://private.example.invalid";
                break;
            case "duplicate":
                document["entries"]!.AsArray().Add(entry.DeepClone());
                break;
            case "oversized":
                document["padding"] = new string('x', 65536);
                break;
        }
        await File.WriteAllTextAsync(fixture.CachePath, document.ToJsonString());

        var client = new GeographyClient((_, _) => Task.FromResult<string?>("unitedstates"));
        Assert.Equal(TelemetryLocation.Global, (await fixture.ResolveAsync(client))!.Location);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task UnwritableCacheDoesNotChangeLiveRouteOrExposeFailureDetails()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(Path.GetDirectoryName(fixture.CachePath)!, "not a directory");
        var output = new StringWriter();
        var client = new GeographyClient((_, _) => Task.FromResult<string?>("europe"));

        Assert.Equal(TelemetryLocation.Europe, (await fixture.ResolveAsync(client, diagnostics: new(output)))!.Location);

        Assert.Contains("stage=cache-write-failed", output.ToString());
        Assert.DoesNotContain(fixture.Directory.Path, output.ToString());
        Assert.DoesNotContain(Environment.EnvironmentId, output.ToString());
        Assert.DoesNotContain(Environment.TenantId, output.ToString());
    }

    [Fact]
    public async Task CancellationAfterServiceReturnDoesNotPersistAResult()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var client = new GeographyClient((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<string?>("europe");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.ResolveAsync(client, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(fixture.CachePath));
    }

    [Fact]
    public async Task ConcurrentWritersLeaveAValidReusableCacheWithoutTemporaryFiles()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Cache().WriteAsync(
            PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Europe, default)));

        Assert.Equal(TelemetryLocation.Europe, await fixture.Cache().ReadAsync(PowerPlatformApiTargetConfig.Public, Environment, default));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.CachePath)!));
    }

    [Fact]
    public async Task CacheEvictsOldestEntriesAndPrunesExpiredOnWrite()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 129; index++)
        {
            await fixture.Cache().WriteAsync(PowerPlatformApiTargetConfig.Public,
                new("environment-" + index, Environment.TenantId), TelemetryLocation.Europe, default);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Null(await fixture.Cache().ReadAsync(PowerPlatformApiTargetConfig.Public, new("environment-0", Environment.TenantId), default));
        Assert.Equal(128, JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!["entries"]!.AsArray().Count);
        Assert.True(new FileInfo(fixture.CachePath).Length <= 65536);

        fixture.Time.Advance(TimeSpan.FromDays(1));
        await fixture.Cache().WriteAsync(PowerPlatformApiTargetConfig.Public, Environment, TelemetryLocation.Global, default);
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(fixture.CachePath))!["entries"]!.AsArray());
    }

    private sealed class Fixture : IDisposable
    {
        internal TempDir Directory { get; } = new();
        internal ManualTimeProvider Time { get; } = new();
        internal string CachePath => Path.Combine(Directory.Path, "telemetry", "collector-cache.json");
        internal FileTelemetryCollectorCache Cache(TelemetryRoutingDiagnostics? diagnostics = null) =>
            new(Directory.Path, Time, diagnostics);

        internal ValueTask<TelemetryDestination?> ResolveAsync(IEnvironmentGeographyClient geography,
            TelemetryRoutingDiagnostics? diagnostics = null, TelemetryEnvironmentTarget? target = null,
            PowerPlatformApiTargetConfig? apiTarget = null, string apiKey = ApiKey, CancellationToken cancellationToken = default) =>
            new TelemetryDestinationResolver(apiTarget ?? PowerPlatformApiTargetConfig.Public,
                geography, apiKey, apiKey, diagnostics: diagnostics, collectorCache: Cache(diagnostics))
                .ResolveAsync(target ?? TelemetryEnvironmentTarget.Resolved([Environment]), cancellationToken);

        public void Dispose() => Directory.Dispose();
    }

    private sealed class GeographyClient(Func<ResolvedEnvironmentTarget, CancellationToken, Task<string?>> read) : IEnvironmentGeographyClient
    {
        internal int Calls { get; private set; }
        public Task<string?> GetGeographyAsync(ResolvedEnvironmentTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            return read(target, cancellationToken);
        }
    }
}
