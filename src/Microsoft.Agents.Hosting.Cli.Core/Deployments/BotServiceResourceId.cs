using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Builds and parses <c>Microsoft.BotService/botServices</c> ARM resource ids of the form
/// <c>/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.BotService/botServices/{name}</c>.
/// </summary>
internal static class BotServiceResourceId
{
    internal readonly record struct Parts(
        string SubscriptionId,
        string ResourceGroup,
        string BotName);

    public static string Build(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.BotService/botServices/{name}";

    public static string BuildRequestPath(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{EscapeSegment(subscriptionId)}/resourceGroups/{EscapeSegment(resourceGroup)}" +
        $"/providers/Microsoft.BotService/botServices/{EscapeSegment(name)}";

    public static string ToRequestPath(string resourceId)
    {
        if (!TryParse(resourceId, out var parsed))
        {
            throw DeploymentException.InvalidBotResource();
        }

        return BuildRequestPath(parsed.SubscriptionId, parsed.ResourceGroup, parsed.BotName);
    }

    public static Parts Parse(string resourceId, string deploymentId)
    {
        return TryParse(resourceId, out var parts)
            ? parts
            : throw DeploymentException.InvalidBotResource(deploymentId);
    }

    public static bool TryParse(string? resourceId, out Parts parsed)
    {
        var parts = resourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not
            [
                var subscriptions,
                var subscriptionId,
                var resourceGroups,
                var resourceGroup,
                var providers,
                var providerNamespace,
                var botServices,
                var botName,
            ] ||
            !subscriptions.Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ||
            !resourceGroups.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            !providers.Equals("providers", StringComparison.OrdinalIgnoreCase) ||
            !providerNamespace.Equals("Microsoft.BotService", StringComparison.OrdinalIgnoreCase) ||
            !botServices.Equals("botServices", StringComparison.OrdinalIgnoreCase) ||
            !IsSafeSegment(subscriptionId) ||
            !IsSafeSegment(resourceGroup) ||
            !IsSafeSegment(botName))
        {
            parsed = default;
            return false;
        }

        parsed = new Parts(subscriptionId, resourceGroup, botName);
        return true;
    }

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(['\\', '?', '#', '%']) < 0;

    private static string EscapeSegment(string value) =>
        IsSafeSegment(value)
            ? Uri.EscapeDataString(value)
            : throw DeploymentException.InvalidBotResource();
}
