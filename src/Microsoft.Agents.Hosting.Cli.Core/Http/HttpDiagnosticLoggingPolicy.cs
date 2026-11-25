using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>
/// Formats bounded HTTP diagnostics, redacting credential fields from structured body copies.
/// </summary>
public static class HttpDiagnosticLoggingPolicy
{
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
        FormatTextBody(body, contentType, isRequest: true, bodyRedactor: null);

    public static string FormatResponseBody(
        ReadOnlySpan<byte> body,
        string? contentType = null,
        string? contentDisposition = null,
        Func<JsonElement, JsonElement>? bodyRedactor = null)
        => GetBodyMarker(contentType, isRequest: false, contentDisposition: contentDisposition) ??
           FormatTextBody(body, contentType, isRequest: false, bodyRedactor);

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
            var value = redact ? SecretRedaction.Mask : string.Join(", ", header.Value);
            result[header.Key] = result.TryGetValue(header.Key, out var existing)
                ? existing + ", " + value
                : value;
        }

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static string FormatTextBody(
        ReadOnlySpan<byte> body, string? contentType, bool isRequest, Func<JsonElement, JsonElement>? bodyRedactor)
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
            var text = encoding.GetString(body);
            using var document = JsonDocument.Parse(text, PowerAppsConnectionBody.Limits);
            var structured = document.RootElement.ValueKind == JsonValueKind.Object ||
                (document.RootElement.ValueKind == JsonValueKind.Array &&
                 document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object));
            return structured ? (bodyRedactor is null
                ? RedactBody(document.RootElement)
                : bodyRedactor(document.RootElement)).GetRawText()
                : "[body omitted: no safe structured representation]";
        }
        catch (DecoderFallbackException)
        {
            return "[body omitted: invalid text encoding]";
        }
        catch (Exception error) when (PowerAppsConnectionBody.IsUnreadable(error))
        {
            return "[body omitted: no safe structured representation]";
        }
    }

    internal static JsonElement RedactBody(
        JsonElement body, Func<JsonElement, JsonElement>? resourceRedactor = null) =>
        JsonSerializer.SerializeToElement(RedactValue(JsonNode.Parse(body.GetRawText()), resourceRedactor));

    private static JsonNode? RedactValue(JsonNode? value, Func<JsonElement, JsonElement>? resourceRedactor)
    {
        if (value is JsonObject fields)
        {
            if (resourceRedactor is not null && fields.Any(property =>
                property.Key.Equals("properties", StringComparison.OrdinalIgnoreCase)))
            {
                return JsonNode.Parse(resourceRedactor(JsonSerializer.SerializeToElement(fields)).GetRawText());
            }

            var parameterKey = fields.FirstOrDefault(property =>
                property.Key.Equals("key", StringComparison.OrdinalIgnoreCase));
            var parameterName = parameterKey.Value is JsonValue key && key.TryGetValue<string>(out var name) &&
                fields.Any(property => property.Key.Equals("value", StringComparison.OrdinalIgnoreCase))
                    ? name : null;
            foreach (var property in fields.ToList())
            {
                var isSecret = parameterName is not null && property.Key.Equals("value", StringComparison.OrdinalIgnoreCase)
                    ? SecretRedaction.IsSecret(parameterName)
                    : !(parameterName is not null && property.Key.Equals("key", StringComparison.OrdinalIgnoreCase)) &&
                        SecretRedaction.IsSecret(property.Key);
                fields[property.Key] = isSecret
                    ? JsonValue.Create(SecretRedaction.Mask)
                    : RedactValue(property.Value, resourceRedactor);
            }
        }
        else if (value is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var original = array[index];
                var redacted = RedactValue(original, resourceRedactor);
                if (!ReferenceEquals(original, redacted))
                {
                    array[index] = redacted;
                }
            }
        }

        return value;
    }
}
