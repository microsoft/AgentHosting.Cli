namespace Microsoft.Agents.Hosting.Cli.Core.Packaging.Zip;

/// <summary>
/// CRC-32 (IEEE 802.3, reflected polynomial <c>0xEDB88320</c>) as required by the ZIP format.
///
/// <para>
/// It is implemented here rather than taken from a package because the deterministic writer must not
/// acquire a dependency whose availability decides whether an offline package build succeeds. The
/// algorithm is fixed by the ZIP specification and cannot drift.
/// </para>
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a running CRC-32 over another block.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var value = ~crc;
        foreach (var b in data)
        {
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        }

        return ~value;
    }

    /// <summary>Computes the CRC-32 of the remainder of <paramref name="stream"/>.</summary>
    public static async Task<uint> ComputeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[81920];
        uint crc = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc = Append(crc, buffer.AsSpan(0, read));
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
