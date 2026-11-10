using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class AccessTokenPayload
{
    public static JsonElement? Read(string token)
    {
        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');

        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=')));

            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? ExpiresOn(string token) => Read(token) is { } payload && payload.TryGetProperty("exp", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
