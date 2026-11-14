using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ExclusiveFileLockTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);

    private string LockPath => Path.Combine(_workspace.Root, "fork.lock");

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void TryAcquire_WhileAnotherHolderHasTheLock_ReturnsNothing()
    {
        using var held = ExclusiveFileLock.TryAcquire(LockPath);

        Assert.NotNull(held);
        Assert.Null(ExclusiveFileLock.TryAcquire(LockPath));
    }

    [Fact]
    public void TryAcquire_AfterTheHolderDisposedTheLock_TakesIt()
    {
        ExclusiveFileLock.TryAcquire(LockPath)!.Dispose();

        using var again = ExclusiveFileLock.TryAcquire(LockPath);

        Assert.NotNull(again);
    }

    [Fact]
    public async Task Dispose_OnAnotherThread_ReleasesALockTakenOnThisOne()
    {
        var held = ExclusiveFileLock.TryAcquire(LockPath)!;

        await Task.Factory.StartNew(held.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        using var again = ExclusiveFileLock.TryAcquire(LockPath);
        Assert.NotNull(again);
    }

    [Fact]
    public void Dispose_LeavesTheLockFileInPlace()
    {
        ExclusiveFileLock.TryAcquire(LockPath)!.Dispose();

        Assert.True(File.Exists(LockPath));
    }

    [Fact]
    public void TryAcquire_InADirectoryThatDoesNotExist_FailsInsteadOfReportingTheLockHeld()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ExclusiveFileLock.TryAcquire(Path.Combine(_workspace.Root, "missing", "fork.lock")));
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilTheHolderReleases()
    {
        using var held = ExclusiveFileLock.TryAcquire(LockPath)!;
        var waiting = ExclusiveFileLock.AcquireAsync(LockPath, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(waiting.IsCompleted);

        held.Dispose();
        using var acquired = await waiting;

        Assert.Null(ExclusiveFileLock.TryAcquire(LockPath));
    }

    [Fact]
    public async Task AcquireAsync_ThrowsTimeoutExceptionNamingTheFile()
    {
        using var held = ExclusiveFileLock.TryAcquire(LockPath);

        Assert.Contains(LockPath, (await Assert.ThrowsAsync<TimeoutException>(() => ExclusiveFileLock.AcquireAsync(LockPath, TimeSpan.FromMilliseconds(300), CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellation()
    {
        using var held = ExclusiveFileLock.TryAcquire(LockPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExclusiveFileLock.AcquireAsync(LockPath, TimeSpan.FromMinutes(5), cancellation.Token));
    }
}
