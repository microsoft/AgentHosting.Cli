namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Derives trusted environment-scoped Power Platform API endpoints.</summary>
public static class PowerPlatformApiDiscovery
{
    /// <summary>
    /// Resolves the environment-scoped front door using the platform's normalized identifier split.
    /// Production uses a two-character suffix; Test uses one.
    /// </summary>
    public static Uri GetEnvironmentEndpoint(
        PowerPlatformApiTargetConfig target,
        string environmentId)
    {
        ArgumentNullException.ThrowIfNull(target);
        const string resourcePrefix = "/providers/Microsoft.PowerApps/environments/";
        if (environmentId?.StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            environmentId = environmentId[resourcePrefix.Length..];
        }

        if (string.IsNullOrWhiteSpace(environmentId)
            || environmentId.Any(character =>
                character is not (>= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-')))
        {
            throw new ArgumentException(
                "Environment identifiers may contain only letters, numbers, and dashes.",
                nameof(environmentId));
        }

        var cloudMap = PowerPlatformCloudMap.ForTarget(target);
        var hostSuffix = EnvironmentHostSuffix(target);
        string normalized = environmentId
            .Trim()
            .ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal);
        int suffixLength =
            target.Ring == PowerPlatformApiRing.Production ? 2 : 1;
        if (normalized.Length <= suffixLength)
        {
            throw new ArgumentException(
                $"The normalized environment identifier must contain more than {suffixLength} characters.",
                nameof(environmentId));
        }

        string prefix = normalized[..^suffixLength];
        string suffix = normalized[^suffixLength..];
        var endpoint = new Uri(
            $"https://{prefix}.{suffix}.{hostSuffix}");
        return cloudMap.RequireTrusted(
            endpoint,
            "Power Platform API environment");
    }

    /// <summary>The host suffix every environment-scoped front door of a ring ends with, after its two identifier labels.</summary>
    /// <param name="target">The Power Platform API ring.</param>
    public static string EnvironmentHostSuffix(PowerPlatformApiTargetConfig target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return $"environment.{PowerPlatformCloudMap.ForTarget(target).RequireTrusted(target.GlobalEndpoint, "Power Platform API global").Host}";
    }

    /// <summary>Whether a host has the shape of an environment-scoped front door of a ring: two identifier labels before its environment host suffix.</summary>
    /// <param name="target">The Power Platform API ring.</param>
    /// <param name="host">The host.</param>
    public static bool IsEnvironmentHost(PowerPlatformApiTargetConfig target, string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var suffix = $".{EnvironmentHostSuffix(target)}";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host[..^suffix.Length].Split('.') is [{ Length: > 0 }, { Length: > 0 }];
    }
}
