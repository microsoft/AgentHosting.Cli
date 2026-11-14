using Microsoft.Agents.Hosting.Cli.Core;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class HeldNamedMutex : IDisposable
{
    private readonly ManualResetEventSlim _acquired = new();
    private readonly ManualResetEventSlim _released = new();
    private readonly Thread _holder;

    private HeldNamedMutex(string namePrefix, string key)
    {
        _holder = new Thread(() =>
        {
            using var held = NamedMutexLock.TryAcquire(namePrefix, key, TimeSpan.FromSeconds(10));
            _acquired.Set();
            _released.Wait();
        })
        {
            IsBackground = true,
        };
        _holder.Start();
        _acquired.Wait();
    }

    public static HeldNamedMutex OnAnotherThread(string namePrefix, string key) => new(namePrefix, key);

    public void Dispose()
    {
        _released.Set();
        _holder.Join();
        _acquired.Dispose();
        _released.Dispose();
    }
}

public sealed class NamedMutexLockTests
{
    private readonly string _prefix = $"ah-test-{Guid.NewGuid():N}-";

    [Fact]
    public void TryAcquire_WhileAnotherThreadHoldsTheMutex_GivesUpOnceItsPatienceRunsOut()
    {
        using var holder = HeldNamedMutex.OnAnotherThread(_prefix, "project");

        Assert.Null(NamedMutexLock.TryAcquire(_prefix, "project", TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void TryAcquire_OfKeysThatDifferOnlyByCase_SharesOneMutex()
    {
        using var holder = HeldNamedMutex.OnAnotherThread(_prefix, @"C:\Projects\Contoso");

        Assert.Null(NamedMutexLock.TryAcquire(_prefix, @"c:\projects\contoso", TimeSpan.Zero));
    }

    [Fact]
    public void TryAcquire_OfAnotherKeyOrPrefix_IsNotHeldUp()
    {
        using var holder = HeldNamedMutex.OnAnotherThread(_prefix, "project");
        using var otherKey = NamedMutexLock.TryAcquire(_prefix, "other project", TimeSpan.Zero);
        using var otherPrefix = NamedMutexLock.TryAcquire($"{_prefix}other-", "project", TimeSpan.Zero);

        Assert.NotNull(otherKey);
        Assert.NotNull(otherPrefix);
    }

    [Fact]
    public void TryAcquire_WaitsWithinItsPatienceForTheHolderToRelease()
    {
        var taken = false;
        using var attempting = new ManualResetEventSlim();
        var contender = new Thread(() =>
        {
            attempting.Set();
            taken = TakeAndRelease(TimeSpan.FromSeconds(30));
        });

        using (HeldNamedMutex.OnAnotherThread(_prefix, "project"))
        {
            contender.Start();
            attempting.Wait();

            Assert.True(SpinWait.SpinUntil(() => !contender.IsAlive || contender.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(30)));
            Assert.True(contender.IsAlive);
        }

        contender.Join();
        Assert.True(taken);
    }

    [Fact]
    public void TryAcquire_OfAMutexWhoseHolderEndedWithoutReleasingIt_TakesIt()
    {
        var holder = new Thread(() => NamedMutexLock.TryAcquire(_prefix, "project", TimeSpan.FromSeconds(10)));
        holder.Start();
        holder.Join();

        using var taken = NamedMutexLock.TryAcquire(_prefix, "project", TimeSpan.FromSeconds(10));

        Assert.NotNull(taken);
    }

    [Fact]
    public void Dispose_ReleasesTheMutexForTheNextHolder()
    {
        NamedMutexLock.TryAcquire(_prefix, "project", TimeSpan.Zero)!.Dispose();

        using var holder = HeldNamedMutex.OnAnotherThread(_prefix, "project");

        Assert.Null(NamedMutexLock.TryAcquire(_prefix, "project", TimeSpan.Zero));
    }

    private bool TakeAndRelease(TimeSpan patience)
    {
        using var taken = NamedMutexLock.TryAcquire(_prefix, "project", patience);

        return taken is not null;
    }
}
