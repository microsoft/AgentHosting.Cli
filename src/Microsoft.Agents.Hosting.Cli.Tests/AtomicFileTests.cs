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
    public async Task WriteAllText_WhileAReaderBrieflyHoldsTheTarget_ReplacesItOnceTheReaderLetsGo()
    {
        File.WriteAllText(Target, "old");
        using var opened = new SemaphoreSlim(0);
        var reader = Task.Run(async () =>
        {
            using var stream = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Release();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        });

        await opened.WaitAsync();
        AtomicFile.WriteAllText(Target, "new");
        await reader;

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
