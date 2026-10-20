using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal static class DeploymentChannelSlack
{
    internal const string AuthorizationGuidance =
        "Authorize Slack using the URL, or configure Slack in Azure portal if no URL is available. " +
        "Authorization may be needed to save settings, but does not guarantee persistence. " +
        "Azure may take a few minutes to process it. " +
        "Check settings with `msagent deployment channel show --channel slack` using the same deployment options. " +
        "The CLI does not verify authorization completion.";

    /// <summary>Checks Slack's alternative create credentials.</summary>
    internal static void Validate(IReadOnlyDictionary<string, JsonElement> parameters, bool isCreate)
    {
        if (isCreate && !parameters.ContainsKey("signingSecret") && !parameters.ContainsKey("verificationToken"))
        {
            throw new UsageException("Creating the 'slack' channel requires at least one of signingSecret or verificationToken.");
        }
    }

    /// <summary>Enables the service authorization flow without mutating the caller's validated parameters.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> WithRegistration(
        IReadOnlyDictionary<string, JsonElement> parameters) =>
        new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["registerBeforeOAuthFlow"] = JsonSerializer.SerializeToElement(true),
        };

    /// <summary>Returns only the expected HTTPS Slack OAuth URL, preserving its opaque query and callback state.</summary>
    internal static Uri? ExtractAuthorizationUrl(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("properties", out var channelProperties) || channelProperties.ValueKind != JsonValueKind.Object ||
            !channelProperties.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty("redirectAction", out var redirectAction) ||
            redirectAction.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(redirectAction.GetString(), UriKind.Absolute, out var authorizationUrl) ||
            authorizationUrl.Scheme != Uri.UriSchemeHttps || authorizationUrl.Host != "slack.com" || !authorizationUrl.IsDefaultPort ||
            authorizationUrl.AbsolutePath != "/oauth/v2/authorize" || authorizationUrl.UserInfo.Length != 0 || authorizationUrl.Fragment.Length != 0)
        {
            return null;
        }

        return authorizationUrl;
    }
}
