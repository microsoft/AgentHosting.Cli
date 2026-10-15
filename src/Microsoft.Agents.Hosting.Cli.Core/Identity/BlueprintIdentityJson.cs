using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal static class BlueprintIdentityJson
{
    public static JsonObject ParseObject(string? body)
    {
        try
        {
            return JsonNode.Parse(body ?? "{}") as JsonObject
                ?? throw new JsonException("Expected a JSON object.");
        }
        catch (JsonException)
        {
            throw new TargetRequestException(
                "The identity service returned malformed JSON.",
                ExitCodes.Failure,
                TargetErrorKind.RequestFailed,
                status: 200);
        }
    }

    public static string RequireString(JsonObject value, string name, string operation) =>
        ReadString(value, name)
        ?? throw new TargetRequestException(
            $"The {operation} response did not include {name}.",
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            status: 200);

    public static string? ReadString(JsonObject? value, string name) =>
        value?[name] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result)
            ? result
            : null;

    public static JsonObject? ReadObject(JsonNode? value, string operation)
    {
        if (value is null)
        {
            return null;
        }

        return value as JsonObject
            ?? throw UnexpectedShape(operation, "an object");
    }

    public static JsonArray ReadArray(JsonNode? value, string operation)
    {
        if (value is null)
        {
            return [];
        }

        return value as JsonArray
            ?? throw UnexpectedShape(operation, "an array");
    }

    public static IReadOnlyList<string> ReadStrings(JsonArray? values)
    {
        if (values is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            if (value is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out var text))
            {
                throw UnexpectedShape("identity service string collection", "string values");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static TargetRequestException UnexpectedShape(string operation, string expected) =>
        new(
            $"The {operation} response had an unexpected shape; expected {expected}.",
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            status: 200);
}
