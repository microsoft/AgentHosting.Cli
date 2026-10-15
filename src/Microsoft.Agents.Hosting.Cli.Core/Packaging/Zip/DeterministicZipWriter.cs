using System.Buffers.Binary;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.Packaging.Zip;

/// <summary>One entry to be written into a deterministic package archive.</summary>
internal sealed class PackageEntrySource
{
    /// <summary>The normalized, validated archive name.</summary>
    public required string Name { get; init; }

    /// <summary>The exact number of bytes <see cref="Open"/> will produce.</summary>
    public required long Length { get; init; }

    /// <summary>The CRC-32 of those bytes, computed before the header is written.</summary>
    public required uint Crc32 { get; init; }

    /// <summary>Opens the entry content. Called exactly once, after the header is written.</summary>
    public required Func<Stream> Open { get; init; }
}

/// <summary>
/// A minimal ZIP writer whose output depends only on the entry names and bytes it is given.
///
/// <para>
/// <b>Why not <c>ZipArchive</c>.</b> The framework writer stamps the host platform into the
/// central-directory "version made by" field and derives external attributes from local file
/// permissions, so the same input produces different bytes on Windows and Linux. It also selects the
/// deflate implementation of the running runtime. Version zero must be byte-identical across
/// machines, repositories, and toolchains, so the format is written directly with every host-derived
/// field fixed.
/// </para>
///
/// <para>
/// Every entry is <b>stored</b> (method 0). Determinism must not depend on a compression
/// implementation whose output can change across SDK, runtime, or platform versions; version-zero
/// packages are small and closed-gate, so the size cost is accepted in exchange for a reproducible
/// digest. Nothing else varies either: the timestamp is fixed at 1980-01-01T00:00:00Z, no extra
/// fields or comments are emitted, no data descriptors are used, and no ZIP64 records are produced —
/// the version-zero limits keep every count and offset inside the classic fields.
/// </para>
/// </summary>
internal static class DeterministicZipWriter
{
    internal const uint LocalHeaderSignature = 0x04034B50;
    internal const uint CentralHeaderSignature = 0x02014B50;
    internal const uint EndOfCentralDirectorySignature = 0x06054B50;

    internal const int LocalHeaderLength = 30;
    internal const int CentralHeaderLength = 46;
    internal const int EndOfCentralDirectoryLength = 22;

    /// <summary>ZIP 2.0 — the floor for a stored entry with a UTF-8 name.</summary>
    internal const ushort VersionNeededToExtract = 20;

    /// <summary>Host 0 (MS-DOS/FAT) with ZIP 2.0. Host-neutral by construction.</summary>
    internal const ushort VersionMadeBy = 20;

    /// <summary>Bit 11 only: the name is UTF-8. No encryption, no data descriptor.</summary>
    internal const ushort GeneralPurposeFlags = 0x0800;

    /// <summary>Stored — no compression.</summary>
    internal const ushort StoredMethod = 0;

    /// <summary>00:00:00 in MS-DOS time.</summary>
    internal const ushort DosTime = 0x0000;

    /// <summary>1980-01-01 in MS-DOS date: year 0, month 1, day 1.</summary>
    internal const ushort DosDate = 0x0021;

    /// <summary>No owner, permission, or executable bit is carried.</summary>
    internal const uint ExternalAttributes = 0;

    /// <summary>
    /// Writes <paramref name="entries"/> to <paramref name="destination"/> in the order given. The
    /// caller is responsible for ordering and for validating names and limits.
    /// </summary>
    public static async Task WriteAsync(
        Stream destination,
        IReadOnlyList<PackageEntrySource> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(entries);

        var names = new byte[entries.Count][];
        var offsets = new long[entries.Count];
        long position = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            if (nameBytes.Length > ushort.MaxValue)
            {
                throw new InvalidOperationException($"Package entry name is too long: {entry.Name.Length} characters.");
            }

            names[i] = nameBytes;
            offsets[i] = position;

            var header = new byte[LocalHeaderLength];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), LocalHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), VersionNeededToExtract);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), GeneralPurposeFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), StoredMethod);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), entry.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), checked((uint)entry.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), checked((uint)entry.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0);

            await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
            position += LocalHeaderLength + nameBytes.Length;

            var copied = await CopyAsync(entry, destination, cancellationToken).ConfigureAwait(false);
            if (copied.Length != entry.Length || copied.Crc32 != entry.Crc32)
            {
                // The content changed between the hashing pass and the write pass. The length alone
                // does not catch it: an edit that replaces bytes in place passes a length check and
                // leaves a header declaring a CRC for content the archive no longer holds. Both are
                // compared, so a same-length mutation fails here instead of shipping.
                throw new InvalidOperationException(
                    $"Package entry '{entry.Name}' changed while the package was being written.");
            }

            position += copied.Length;
        }

        var centralDirectoryOffset = position;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            var nameBytes = names[i];
            var header = new byte[CentralHeaderLength];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), CentralHeaderSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), VersionNeededToExtract);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), GeneralPurposeFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), StoredMethod);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), entry.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), checked((uint)entry.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)entry.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(38), ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), checked((uint)offsets[i]));

            await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
            position += CentralHeaderLength + nameBytes.Length;
        }

        var centralDirectorySize = position - centralDirectoryOffset;

        var end = new byte[EndOfCentralDirectoryLength];
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(0), EndOfCentralDirectorySignature);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), checked((ushort)entries.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), checked((ushort)entries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), checked((uint)centralDirectorySize));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), checked((uint)centralDirectoryOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(20), 0);

        await destination.WriteAsync(end, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the entry content, reporting what was actually written so the caller can compare it
    /// against what the header already declared.
    /// </summary>
    private static async Task<(long Length, uint Crc32)> CopyAsync(
        PackageEntrySource entry,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var content = entry.Open();
        var buffer = new byte[81920];
        long copied = 0;
        uint crc = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > entry.Length)
            {
                return (copied, crc);
            }

            crc = Crc32.Append(crc, buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return (copied, crc);
    }
}
