using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>
/// Formats HTTP diagnostics using Azure CLI's network logging policy.
/// Ordinary headers and text bodies can contain sensitive data.
/// </summary>
public static class HttpDiagnosticLoggingPolicy
{
    public const string Mask = "*****";
    public const int MaximumBodyBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyDictionary<string, string> FormatRequestHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        FormatHeaders(headers, isRequest: true);

    public static IReadOnlyDictionary<string, string> FormatResponseHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        FormatHeaders(headers, isRequest: false);

    public static string FormatRequestBody(
        ReadOnlySpan<byte> body,
        string? contentType = null,
        bool isStreaming = false) =>
        GetBodyMarker(contentType, isRequest: true, isStreaming) ??
        FormatTextBody(body, contentType, isRequest: true);

    public static string FormatResponseBody(
        ReadOnlySpan<byte> body,
        string? contentType = null,
        string? contentDisposition = null)
        => GetBodyMarker(contentType, isRequest: false, contentDisposition: contentDisposition) ??
           FormatTextBody(body, contentType, isRequest: false);

    internal static string? GetBodyMarker(
        string? contentType, bool isRequest, bool isStreaming = false, string? contentDisposition = null)
    {
        if (!isRequest && ContentDispositionHeaderValue.TryParse(contentDisposition, out var disposition) &&
            disposition.DispositionType.Equals("attachment", StringComparison.OrdinalIgnoreCase))
        {
            return "File attachments: " + (disposition.FileNameStar ?? disposition.FileName)?.Trim('"');
        }

        if (isRequest && isStreaming)
        {
            return "File upload";
        }

        var mediaType = MediaTypeHeaderValue.TryParse(contentType, out var parsedMediaType)
            ? parsedMediaType
            : null;
        if (mediaType?.MediaType?.EndsWith("octet-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Body contains binary data.";
        }

        if (mediaType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Body contains image data.";
        }

        if (isStreaming ||
            string.Equals(mediaType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return isRequest ? "File upload" : "Body is streamable";
        }

        return null;
    }

    private static ReadOnlyDictionary<string, string> FormatHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers,
        bool isRequest)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            var redact = isRequest && (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("x-ms-authorization-auxiliary", StringComparison.OrdinalIgnoreCase));
            var value = redact ? Mask : string.Join(", ", header.Value);
            result[header.Key] = result.TryGetValue(header.Key, out var existing)
                ? existing + ", " + value
                : value;
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string FormatTextBody(ReadOnlySpan<byte> body, string? contentType, bool isRequest)
    {
        if (body.Length > MaximumBodyBytes)
        {
            return "[body omitted: capture limit exceeded]";
        }

        if (body.IsEmpty)
        {
            return isRequest ? "This request has no body" : string.Empty;
        }

        Encoding encoding;
        try
        {
            var mediaType = MediaTypeHeaderValue.TryParse(contentType, out var parsedMediaType)
                ? parsedMediaType
                : null;
            encoding = mediaType?.CharSet is { } charset
                ? Encoding.GetEncoding(charset.Trim('"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                : StrictUtf8;
        }
        catch (ArgumentException)
        {
            return "[body omitted: unsupported encoding]";
        }

        try
        {
            return encoding.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return "[body omitted: invalid text encoding]";
        }
    }
}
