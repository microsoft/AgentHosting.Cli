namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Derives trusted environment-scoped Power Platform API endpoints.</summary>
public static class PowerPlatformApiDiscovery
{
    /// <summary>
    /// Resolves the environment-scoped front door using the platform's normalized identifier split.
    /// Public production uses a two-character suffix; other verified targets use one.
    /// China environment routing is not verified and cannot be derived.
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
        Uri globalEndpoint =
            cloudMap.RequireTrusted(target.GlobalEndpoint, "Power Platform API global");
        if (target.Cloud == Auth.CloudInstance.China)
        {
            throw new NotSupportedException("Derived Power Platform API environment routing is not verified for China.");
        }

        string normalized = environmentId
            .Trim()
            .ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal);
        int suffixLength =
            target.Cloud == Auth.CloudInstance.Public && target.Ring == PowerPlatformApiRing.Production ? 2 : 1;
        if (normalized.Length <= suffixLength)
        {
            throw new ArgumentException(
                $"The normalized environment identifier must contain more than {suffixLength} characters.",
                nameof(environmentId));
        }

        if (normalized.Length - suffixLength > 63)
        {
            throw new ArgumentException(
                "The normalized environment identifier prefix must fit in a DNS label of at most 63 characters.",
                nameof(environmentId));
        }

        string prefix = normalized[..^suffixLength];
        string suffix = normalized[^suffixLength..];
        var endpoint = new Uri(
            $"https://{prefix}.{suffix}.environment.{globalEndpoint.Host}");
        return cloudMap.RequireTrusted(
            endpoint,
            "Power Platform API environment");
    }
}
