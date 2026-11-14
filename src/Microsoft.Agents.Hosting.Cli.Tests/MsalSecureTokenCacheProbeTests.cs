using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class MsalSecureTokenCacheProbeTests : IDisposable
{
    private const string Verified = "verify";

    private const string Released = "release";

    private readonly TempDir _directory = new();

    private readonly List<string> _events = [];

    private readonly List<TimeSpan> _waits = [];

    private AgentHostCacheDescriptor Descriptor => AgentHostCacheDescriptor.Create(_directory.Path);

    private string Acquired => $"acquire {Descriptor.CacheFilePath}.test.lock";

    public void Dispose() => _directory.Dispose();

    private MsalSecureTokenCacheProbe Probe(params Exception?[] failures)
    {
        var attempt = 0;

        return new MsalSecureTokenCacheProbe(
            _ =>
            {
                _events.Add(Verified);

                if (failures.ElementAtOrDefault(attempt++) is { } failure)
                {
                    throw failure;
                }
            },
            (path, _) =>
            {
                _events.Add($"acquire {path}");

                return Task.FromResult<IDisposable>(new Release(_events));
            },
            (wait, _) =>
            {
                _waits.Add(wait);

                return Task.CompletedTask;
            });
    }

    private static MsalCachePersistenceException Unavailable() => new("The secret store is unavailable.");

    [Fact]
    public async Task AProbeThatSucceedsReportsAvailableAndReleasesTheLock()
    {
        Assert.True(await Probe().IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None));

        Assert.Equal([Acquired, Verified, Released], _events);
        Assert.Empty(_waits);
        Assert.True(Directory.Exists(Descriptor.CacheDirectory));
    }

    [Fact]
    public async Task AProbeThatFailsOnceIsRetriedAndReportsAvailable()
    {
        Assert.True(await Probe(Unavailable()).IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None));

        Assert.Equal(2, _events.Count(entry => entry == Verified));
        Assert.Equal([TimeSpan.FromMilliseconds(100)], _waits);
    }

    [Fact]
    public async Task AProbeThatFailsOnEveryAttemptReportsUnavailable()
    {
        Assert.False(await Probe(Unavailable(), Unavailable(), Unavailable()).IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None));

        Assert.Equal(3, _events.Count(entry => entry == Verified));
        Assert.Equal([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], _waits);
        Assert.Equal(Released, _events[^1]);
    }

    [Fact]
    public async Task TheVerifierRunsOnlyWhileTheLockIsHeld()
    {
        await Probe(Unavailable(), Unavailable()).IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None);

        Assert.Equal([Acquired, Verified, Verified, Verified, Released], _events);
    }

    [Fact]
    public async Task AFailureOtherThanAnUnavailableStoreIsNotRetriedAndReleasesTheLock()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Probe(new UnauthorizedAccessException("denied")).IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None));

        Assert.Equal([Acquired, Verified, Released], _events);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task ALockThatTimesOutIsNotReportedAsUnavailable()
    {
        var verified = false;
        var timeout = new TimeoutException("The lock file was still locked after 30 seconds.");

        Assert.Same(timeout, await Assert.ThrowsAsync<TimeoutException>(() => new MsalSecureTokenCacheProbe(_ => verified = true, (_, _) => Task.FromException<IDisposable>(timeout), (_, _) => Task.CompletedTask).IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None)));

        Assert.False(verified);
    }

    [Fact]
    public async Task ParallelProbesAllReportAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var probe = new MsalSecureTokenCacheProbe();

        Assert.All(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => probe.IsProtectedPersistenceAvailableAsync(Descriptor, CancellationToken.None)))), Assert.True);

        Assert.True(File.Exists($"{Descriptor.CacheFilePath}.test.lock"));
    }

    private sealed class Release(List<string> events) : IDisposable
    {
        public void Dispose() => events.Add(Released);
    }
}
