using System.Globalization;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads Agent Management Service response fields strictly, matching names in any letter case and treating JSON null as absent.</summary>
internal static class AmsJson
{
    /// <summary>The agent types AMS declares, with their numbers.</summary>
    internal static readonly (string Name, int Value)[] AgentTypes = [("CodeAgent", 0)];

    /// <summary>The repository binding states AMS declares, with their numbers.</summary>
    internal static readonly (string Name, int Value)[] RepositoryBindingStatuses = [("NotSpecified", 0), ("Provisioning", 1), ("Bound", 2), ("Failed", 3)];

    /// <summary>The agent languages AMS declares, with their numbers.</summary>
    internal static readonly (string Name, int Value)[] Languages = [("NotSpecified", 0), ("DotNet", 1), ("Python", 2), ("Node", 3)];

    /// <summary>The deployment stages AMS declares, with their numbers.</summary>
    internal static readonly (string Name, int Value)[] DeploymentTypes = [("Dev", 0), ("Test", 1), ("Prod", 2)];

    /// <summary>The deployment identity kinds AMS declares, with their numbers.</summary>
    internal static readonly (string Name, int Value)[] EntraIdTypes = [("AppReg", 0), ("Blueprint", 1), ("Default", 2)];

    /// <summary>The deployment states AMS declares, in their documented order.</summary>
    internal static readonly (string Name, int Value)[] DeploymentStates = [("NotDeployed", 0), ("Deploying", 1), ("Deployed", 2), ("Deleting", 3)];

    /// <summary>Returns a required nonblank string field, or throws a format error naming the field.</summary>
    internal static string GetRequiredString(JsonElement root, string name) =>
        GetOptionalString(root, name) ?? throw new FormatException($"{name} is missing.");

    /// <summary>Returns an optional string field, treating a blank value as absent.</summary>
    internal static string? GetOptionalString(JsonElement root, string name)
    {
        if (GetOptionalProperty(root, name) is not { } value)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name} is invalid.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Returns a required 64-bit integer field.</summary>
    internal static long GetRequiredInt64(JsonElement root, string name) =>
        GetOptionalInt64(root, name) ?? throw new FormatException($"{name} is missing.");

    /// <summary>Returns an optional 64-bit integer field.</summary>
    internal static long? GetOptionalInt64(JsonElement root, string name)
    {
        if (GetOptionalProperty(root, name) is not { } value)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new FormatException($"{name} is invalid.");
        }

        return result;
    }

    /// <summary>Returns a required Boolean field.</summary>
    internal static bool GetRequiredBoolean(JsonElement root, string name) =>
        GetOptionalBoolean(root, name) ?? throw new FormatException($"{name} is missing.");

    /// <summary>Returns an optional Boolean field.</summary>
    internal static bool? GetOptionalBoolean(JsonElement root, string name) =>
        GetOptionalProperty(root, name) is not { } value
            ? null
            : value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new FormatException($"{name} is invalid."),
            };

    /// <summary>Returns the declared name of an enumeration field sent as its name or its number.</summary>
    internal static string GetEnum(
        JsonElement root,
        string name,
        params (string Name, int Value)[] values)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            var named = values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, text, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(named.Name))
            {
                return named.Name;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            var numbered = values.FirstOrDefault(candidate => candidate.Value == numeric);
            if (!string.IsNullOrWhiteSpace(numbered.Name))
            {
                return numbered.Name;
            }
        }

        throw new FormatException($"{name} is invalid.");
    }

    /// <summary>Returns the declared name of an optional enumeration field sent as its name or its number, or the value itself when its name is not declared.</summary>
    internal static string? GetOptionalOpenEnum(JsonElement root, string name, params (string Name, int Value)[] values)
    {
        if (GetOptionalProperty(root, name) is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => null,
            JsonValueKind.String => values.FirstOrDefault(candidate => string.Equals(candidate.Name, value.GetString(), StringComparison.OrdinalIgnoreCase)).Name ?? value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var numeric) => values.FirstOrDefault(candidate => candidate.Value == numeric).Name ?? numeric.ToString(CultureInfo.InvariantCulture),
            _ => throw new FormatException($"{name} is invalid."),
        };
    }

    /// <summary>Returns an optional date and time field.</summary>
    internal static DateTimeOffset? GetOptionalDate(JsonElement root, string name) =>
        GetOptionalString(root, name) is not { } text ? null
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value
        : throw new FormatException($"{name} is invalid.");

    /// <summary>Returns the items of a required array field.</summary>
    internal static IEnumerable<JsonElement> GetRequiredArray(JsonElement root, string name) =>
        GetOptionalArray(root, name) ?? throw new FormatException($"{name} is missing.");

    /// <summary>Returns the items of an optional array field.</summary>
    internal static IEnumerable<JsonElement>? GetOptionalArray(JsonElement root, string name) =>
        GetOptionalProperty(root, name) is not { } value
            ? null
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new FormatException($"{name} is invalid.");

    /// <summary>Returns the nonblank string items of an optional array field.</summary>
    internal static IReadOnlyList<string>? GetOptionalStrings(JsonElement root, string name) =>
        GetOptionalArray(root, name)?.Select(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()) ? item.GetString()! : throw new FormatException($"{name} is invalid.")).ToArray();

    /// <summary>Returns a required field of any kind except null.</summary>
    internal static JsonElement GetRequiredProperty(JsonElement root, string name) =>
        GetOptionalProperty(root, name) ?? throw new FormatException($"{name} is missing.");

    /// <summary>Returns a field of an object in any letter case, or null when the field is absent or null.</summary>
    internal static JsonElement? GetOptionalProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
            }
        }

        return null;
    }
}
