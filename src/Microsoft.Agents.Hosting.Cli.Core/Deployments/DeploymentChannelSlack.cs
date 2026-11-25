using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal static class DeploymentChannelSlack
{
    internal const string ChannelName = "slack";

    internal const string AuthorizationGuidance =
        "Authorize Slack using the URL, or configure Slack in Azure portal if no URL is available. " +
        "Authorization may be needed to save settings, but does not guarantee persistence. " +
        "Azure may take a few minutes to process it. " +
        "Check settings with `msagent deployment channel show --channel slack` using the same deployment options. " +
        "The CLI does not verify authorization completion.";

    /// <summary>The Slack guidance for deployments that AMS manages, whose makers get a new authorization URL from an update instead of the Azure portal.</summary>
    internal const string AmsAuthorizationGuidance =
        "Authorize Slack using the URL. If no URL is available or the authorization was not completed, run " +
        "`msagent deployment channel update --channel slack --parameters isEnabled=true` using the same deployment options to get a new URL. " +
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

    /// <summary>Returns only the expected HTTPS Slack OAuth URL of an ARM channel response, preserving its opaque query and callback state.</summary>
    internal static Uri? ExtractAuthorizationUrl(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("properties", out var channelProperties) && channelProperties.ValueKind == JsonValueKind.Object &&
        channelProperties.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
        properties.TryGetProperty("redirectAction", out var redirectAction) && redirectAction.ValueKind == JsonValueKind.String
            ? ParseAuthorizationUrl(redirectAction.GetString())
            : null;

    /// <summary>Returns the text as a URL only when it is the expected HTTPS Slack OAuth URL, preserving its opaque query and callback state.</summary>
    internal static Uri? ParseAuthorizationUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var authorizationUrl) &&
        authorizationUrl.Scheme == Uri.UriSchemeHttps && authorizationUrl.Host == "slack.com" && authorizationUrl.IsDefaultPort &&
        authorizationUrl.AbsolutePath == "/oauth/v2/authorize" && authorizationUrl.UserInfo.Length == 0 && authorizationUrl.Fragment.Length == 0
            ? authorizationUrl
            : null;

    /// <summary>Reports a Slack write as accepted rather than confirmed, because Slack keeps its settings only after the authorization.</summary>
    internal static DeploymentOperationStatus WriteStatus(string channelName, DeploymentOperationStatus completed) =>
        channelName == ChannelName ? DeploymentOperationStatus.WriteAccepted : completed;

    /// <summary>Opens the authorization URL of a Slack result unless the run is non-interactive, and adds the guidance to the result.</summary>
    internal static DeploymentResult<DeploymentChannelRecord> OpenAuthorization(
        DeploymentResult<DeploymentChannelRecord> result,
        bool nonInteractive,
        IBrowserLauncher? browser,
        string guidance)
    {
        if (result.Payload?.ChannelName != ChannelName)
        {
            return result;
        }

        if (result.AuthorizationUrl is { } url && !nonInteractive && browser is not null)
        {
            try
            {
                browser.Launch(url);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                guidance = $"The browser could not be opened. {guidance}";
            }
        }

        return result with { Warnings = [.. result.Warnings, guidance] };
    }
}
