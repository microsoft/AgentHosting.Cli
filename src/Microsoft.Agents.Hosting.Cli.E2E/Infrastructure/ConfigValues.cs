namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Whether a configured value is usable, and why not when it is not.</summary>
public sealed record ConfigValue(string? Value, bool Supplied, string Detail)
{
    /// <summary>The value when it is usable, otherwise null.</summary>
    public string? Usable => Supplied ? Value : null;
}

/// <summary>
/// Classifies configured settings before anything reaches the network, so a placeholder or a typo
/// costs nothing.
/// </summary>
public static class ConfigValues
{
    private const string PlaceholderGuid = "00000000-0000-0000-0000-000000000000";

    private const string DefaultEnvironmentPrefix = "Default-";

    /// <summary>Accepts an environment id, which is a GUID or a tenant default environment's <c>Default-</c> GUID.</summary>
    public static ConfigValue EnvironmentId(string? value, string settingName, Func<string, string> describe)
    {
        if (value?.Trim() is not { } trimmed || !trimmed.StartsWith(DefaultEnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Guid(value, settingName, describe);
        }

        var tenant = Guid(trimmed[DefaultEnvironmentPrefix.Length..], settingName, describe);

        return tenant.Supplied ? new ConfigValue(trimmed, true, describe(trimmed)) : tenant;
    }

    /// <summary>Accepts a real GUID, rejecting the template placeholder and anything malformed.</summary>
    public static ConfigValue Guid(string? value, string settingName, Func<string, string> describe)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ConfigValue(null, false, $"no {settingName} supplied");
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, PlaceholderGuid, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigValue(null, false, $"{settingName} is still the template placeholder — `msagent env list` prints the real one");
        }

        return !System.Guid.TryParse(trimmed, out var parsed)
            ? new ConfigValue(null, false, $"{settingName} is not a GUID")
            : parsed == System.Guid.Empty
                ? new ConfigValue(null, false, $"{settingName} is an empty GUID")
                : new ConfigValue(trimmed, true, describe(trimmed));
    }

    /// <summary>Accepts any non-blank text, for names and endpoints that are not GUIDs.</summary>
    public static ConfigValue Text(string? value, string settingName, Func<string, string> describe) =>
        string.IsNullOrWhiteSpace(value)
            ? new ConfigValue(null, false, $"no {settingName} supplied")
            : new ConfigValue(value.Trim(), true, describe(value.Trim()));
}
