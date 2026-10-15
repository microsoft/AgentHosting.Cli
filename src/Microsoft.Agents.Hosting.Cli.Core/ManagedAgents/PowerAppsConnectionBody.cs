using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

public static class PowerAppsConnectionBody
{
    public const int MaxDepth = 32;

    public const int MaxExcerptLength = 400;

    public const string Unreadable = "[unreadable]";

    private static readonly string[] SecretProperties =
    [
        "consentLink",
        "testLinks",
        "token",
        "accessToken",
        "refreshToken",
        "idToken",
        "secret",
        "clientSecret",
        "password",
    ];

    public static JsonDocumentOptions Limits { get; } = new() { MaxDepth = MaxDepth };

    public static bool IsUnreadable(Exception failure) =>
        failure is JsonException or ArgumentException or InvalidOperationException;

    public static string Excerpt(string body) =>
        body.Length <= MaxExcerptLength ? body : body[..MaxExcerptLength];

    public static string Redacted(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(body, documentOptions: Limits);
        }
        catch (Exception failure) when (IsUnreadable(failure))
        {
            return Unreadable;
        }

        if (parsed is null)
        {
            return Unreadable;
        }

        Redact(parsed);

        try
        {
            return Excerpt(parsed.ToJsonString());
        }
        catch (Exception failure) when (IsUnreadable(failure))
        {
            return Unreadable;
        }
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject owner:
                foreach (var property in owner.ToArray())
                {
                    if (SecretProperties.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        owner[property.Key] = "[redacted]";
                    }
                    else if (property.Value is { } value)
                    {
                        Redact(value);
                    }
                }

                break;

            case JsonArray entries:
                foreach (var entry in entries)
                {
                    if (entry is not null)
                    {
                        Redact(entry);
                    }
                }

                break;
        }
    }
}
