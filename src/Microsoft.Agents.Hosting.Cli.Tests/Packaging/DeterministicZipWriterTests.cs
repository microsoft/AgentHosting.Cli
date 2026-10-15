using Microsoft.Agents.Hosting.Cli.Core.Packaging.Zip;

namespace Microsoft.Agents.Hosting.Cli.Tests.Packaging;

/// <summary>
/// The deterministic ZIP writer's guard against source content changing mid-write.
///
/// <para>
/// The builder computes each entry's CRC-32 and length in an earlier pass, then writes a local header
/// carrying both before streaming the content. If the file changes in between, the header describes
/// bytes the archive does not contain — and a package whose digest is stable but whose CRC is wrong
/// is worse than one that fails to build, because it fails later and somewhere else.
/// </para>
///
/// <para>
/// A length comparison alone does not catch it: an edit that replaces bytes in place keeps the
/// length. That is why the writer compares the CRC it computed while copying against the one already
/// written into the header.
/// </para>
/// </summary>
public sealed class DeterministicZipWriterTests
{
    [Fact]
    public async Task Write_EntryContentChangedWithoutChangingLength_Fails()
    {
        var declared = "aaaa"u8.ToArray();
        var actual = "bbbb"u8.ToArray();

        Assert.Equal(declared.Length, actual.Length);

        using var output = new MemoryStream();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DeterministicZipWriter.WriteAsync(
                output,
                [Entry("src/agent.js", declaredContent: declared, actualContent: actual)],
                CancellationToken.None));

        Assert.Contains("changed while the package was being written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_EntryContentChangedLength_Fails()
    {
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DeterministicZipWriter.WriteAsync(
                output,
                [Entry("src/agent.js", declaredContent: "aaaa"u8.ToArray(), actualContent: "aa"u8.ToArray())],
                CancellationToken.None));
    }

    [Fact]
    public async Task Write_UnchangedEntry_Succeeds()
    {
        var content = "aaaa"u8.ToArray();

        using var output = new MemoryStream();

        await DeterministicZipWriter.WriteAsync(
            output,
            [Entry("src/agent.js", declaredContent: content, actualContent: content)],
            CancellationToken.None);

        var entry = Assert.Single(TestZipReader.Read(output.ToArray()));
        Assert.Equal("src/agent.js", entry.Name);
        Assert.Equal(content, entry.Content);
    }

    /// <summary>
    /// An entry whose declared CRC and length come from <paramref name="declaredContent"/> while its
    /// stream yields <paramref name="actualContent"/> — the shape a same-length mutation produces
    /// between the two passes.
    /// </summary>
    private static PackageEntrySource Entry(string name, byte[] declaredContent, byte[] actualContent) =>
        new()
        {
            Name = name,
            Length = declaredContent.Length,
            Crc32 = Crc32.Compute(declaredContent),
            Open = () => new MemoryStream(actualContent, writable: false),
        };
}
