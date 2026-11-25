using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>Reads the process's standard input as strict UTF-8, or in the strict Unicode encoding its byte order mark names.</summary>
public sealed class ConsoleStandardInput : IStandardInput
{
    private static readonly (byte[] ByteOrderMark, Encoding Encoding, string Name)[] Encodings =
    [
        ([0xFF, 0xFE, 0x00, 0x00], new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true), "UTF-32"),
        ([0x00, 0x00, 0xFE, 0xFF], new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true), "UTF-32"),
        ([0xEF, 0xBB, 0xBF], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), "UTF-8"),
        ([0xFF, 0xFE], new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), "UTF-16"),
        ([0xFE, 0xFF], new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), "UTF-16"),
        ([], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), "UTF-8"),
    ];

    private readonly Func<Stream> _open;
    private readonly Func<bool> _isRedirected;

    /// <summary>Creates the reader over the stream and redirection state of standard input.</summary>
    public ConsoleStandardInput(Func<Stream> open, Func<bool> isRedirected)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _isRedirected = isRedirected ?? throw new ArgumentNullException(nameof(isRedirected));
    }

    /// <inheritdoc />
    public bool IsRedirected => _isRedirected();

    /// <inheritdoc />
    public async Task<string> ReadToEndAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var buffer = new byte[maximumBytes + 1];
        await using var stream = _open();
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (length > maximumBytes)
        {
            throw new UsageException($"Standard input is longer than {maximumBytes} bytes.");
        }

        var (byteOrderMark, encoding, name) = Encodings.First(candidate => buffer.AsSpan(0, length).StartsWith(candidate.ByteOrderMark));
        try
        {
            return encoding.GetString(buffer, byteOrderMark.Length, length - byteOrderMark.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new UsageException($"Standard input is not valid {name} text.");
        }
    }
}
