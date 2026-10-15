using System.Buffers.Binary;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Tests.Packaging;

/// <summary>One archive entry as both of its headers describe it, plus its stored bytes.</summary>
internal sealed record TestZipEntry
{
    public required string Name { get; init; }

    public required ushort Method { get; init; }

    public required ushort Flags { get; init; }

    public required ushort DosTime { get; init; }

    public required ushort DosDate { get; init; }

    public required ushort VersionMadeBy { get; init; }

    public required uint ExternalAttributes { get; init; }

    public required ushort LocalMethod { get; init; }

    public required ushort LocalFlags { get; init; }

    public required ushort LocalDosTime { get; init; }

    public required ushort LocalDosDate { get; init; }

    public required ushort LocalExtraLength { get; init; }

    public required byte[] Content { get; init; }
}

/// <summary>
/// A test-side ZIP reader that reports what is actually on disk — the compression method, the flags,
/// the timestamp fields, and the host-derived metadata — rather than what a friendly archive API
/// chooses to surface. Determinism claims about the package format have to be checked against the
/// bytes.
/// </summary>
internal static class TestZipReader
{
    public static IReadOnlyList<TestZipEntry> Read(byte[] archive)
    {
        var endOffset = archive.Length - 22;
        Assert.Equal(0x06054B50u, BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(endOffset)));

        var count = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(endOffset + 10));
        var directoryOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(endOffset + 16));

        var entries = new List<TestZipEntry>(count);
        var position = directoryOffset;

        for (var i = 0; i < count; i++)
        {
            var header = archive.AsSpan(position);
            Assert.Equal(0x02014B50u, BinaryPrimitives.ReadUInt32LittleEndian(header));

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var localOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            var name = Encoding.UTF8.GetString(archive, position + 46, nameLength);

            var local = archive.AsSpan(localOffset);
            Assert.Equal(0x04034B50u, BinaryPrimitives.ReadUInt32LittleEndian(local));
            var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);
            var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);
            var dataOffset = localOffset + 30 + localNameLength + localExtraLength;

            entries.Add(new TestZipEntry
            {
                Name = name,
                Method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]),
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]),
                DosTime = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]),
                DosDate = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]),
                VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]),
                ExternalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(header[38..]),
                LocalMethod = BinaryPrimitives.ReadUInt16LittleEndian(local[8..]),
                LocalFlags = BinaryPrimitives.ReadUInt16LittleEndian(local[6..]),
                LocalDosTime = BinaryPrimitives.ReadUInt16LittleEndian(local[10..]),
                LocalDosDate = BinaryPrimitives.ReadUInt16LittleEndian(local[12..]),
                LocalExtraLength = localExtraLength,
                Content = archive[dataOffset..(dataOffset + compressedSize)],
            });

            position += 46 + nameLength;
        }

        return entries;
    }
}
