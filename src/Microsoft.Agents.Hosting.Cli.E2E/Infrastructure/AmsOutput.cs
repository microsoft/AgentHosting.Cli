using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Reads the JSON that msagent writes for an Agent Management Service command.</summary>
public static class AmsOutput
{
    /// <summary>The error kind of a command addressing an agent that does not exist.</summary>
    public const string AgentNotFound = "agent-not-found";

    /// <summary>The error kind of a request the service refused for a field value, including a dev endpoint while its gate is closed.</summary>
    public const string InvalidFieldValue = "invalid-field-value";

    /// <summary>The error kind of an agent delete that waits for a deploy that may still run.</summary>
    public const string DeployInProgress = "agent-deploy-in-progress";

    /// <summary>The JSON object on stdout, or null when there is none.</summary>
    public static JsonElement? Document(MsagentResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the command exited zero with a JSON result that does not report a failure.</summary>
    public static bool Succeeded(MsagentResult result) => result.ExitCode == 0 && Document(result) is { } root && !(root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False);

    /// <summary>The error kind of a refused command, or null when it was not refused with one.</summary>
    public static string? ErrorKind(MsagentResult result) => result.ExitCode != 0 && Document(result) is { } root ? Text(root, "errorKind") : null;

    /// <summary>Whether the command was refused with the given error kind.</summary>
    public static bool IsRefused(MsagentResult result, string errorKind) => string.Equals(ErrorKind(result), errorKind, StringComparison.Ordinal);

    /// <summary>The string at a property path, or null when any step is missing or the value is not a string.</summary>
    public static string? Text(JsonElement element, params string[] path) => Find(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>The elements of the array at a property path, or none when there is no array there.</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement element, params string[] path) => Find(element, path) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

    /// <summary>The element at a property path, or null when any step is missing.</summary>
    public static JsonElement? Find(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var property in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
            {
                return null;
            }
        }

        return current;
    }
}
