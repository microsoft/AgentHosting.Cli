using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class FileSystemRetryTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task WhileHeld_MovesADirectoryOnceAFileInsideItIsNoLongerHeld()
    {
        var source = Path.Combine(_workspace.Root, "staging");
        var destination = Path.Combine(_workspace.Root, "workspace");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "agent.mcs.yml"), "kind: AdaptiveDialog");
        using var opened = new SemaphoreSlim(0);
        var scanner = Task.Run(async () =>
        {
            using var stream = new FileStream(Path.Combine(source, "agent.mcs.yml"), FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Release();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        });

        await opened.WaitAsync();
        FileSystemRetry.WhileHeld(() => Directory.Move(source, destination));
        await scanner;

        Assert.False(Directory.Exists(source));
        Assert.Equal("kind: AdaptiveDialog", File.ReadAllText(Path.Combine(destination, "agent.mcs.yml")));
    }

    [Fact]
    public void WhileHeld_RepeatsAChangeRefusedWithAccessDenied()
    {
        var attempts = 0;

        FileSystemRetry.WhileHeld(() =>
        {
            if (++attempts < 3)
            {
                throw new IOException("Access to the path is denied.", unchecked((int)0x80070005));
            }
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void WhileHeld_RethrowsAFailureThatIsNotAHoldAtOnce()
    {
        var attempts = 0;

        var failure = Assert.Throws<IOException>(() => FileSystemRetry.WhileHeld(() =>
        {
            attempts++;
            throw new IOException("Cannot create a file when that file already exists.", unchecked((int)0x800700B7));
        }));

        Assert.Equal(unchecked((int)0x800700B7), failure.HResult);
        Assert.Equal(1, attempts);
    }
}
