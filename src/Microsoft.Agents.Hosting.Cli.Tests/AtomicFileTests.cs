using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);

    private string Target => Path.Combine(_workspace.Root, "record.json");

    public void Dispose() => _workspace.Dispose();

    private string[] TemporaryFiles() => Directory.GetFiles(_workspace.Root, "*.tmp");

    [Fact]
    public void WriteAllText_WhileAReaderBrieflyHoldsTheTarget_ReplacesItOnceTheReaderLetsGo()
    {
        File.WriteAllText(Target, "old");
        using var reader = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? failure = null;
        var writer = new Thread(() =>
        {
            try
            {
                AtomicFile.WriteAllText(Target, "new");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        writer.Start();
        Assert.True(SpinWait.SpinUntil(() => !writer.IsAlive || (TemporaryFiles().Length > 0 && writer.ThreadState.HasFlag(ThreadState.WaitSleepJoin)), TimeSpan.FromSeconds(30)));
        reader.Dispose();
        writer.Join();

        Assert.Null(failure);
        Assert.Equal("new", File.ReadAllText(Target));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void WriteAllText_WhenTheTargetStaysHeld_RethrowsTheReplaceFailureAndLeavesNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Target, "old");
        using var held = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.WriteAllText(Target, "new"));

        Assert.Equal("old", File.ReadAllText(Target));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public void ReadAllText_ReadsAFileThatAnotherHolderHasOpenToWriteOrDelete()
    {
        File.WriteAllText(Target, "content");
        using var holder = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

        Assert.Equal("content", AtomicFile.ReadAllText(Target));
    }
}
