using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal static class McsLiveTargetValidation
{
    public static void ValidateEnvironmentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(c => !(char.IsLetterOrDigit(c) || c == '-')) ||
            value.Trim('-').Length < 3)
        {
            throw new ArgumentException("The environment ID is not a valid public-cloud identifier.", nameof(value));
        }
    }

    public static void ValidateTenantId(string value)
    {
        if (!Guid.TryParse(value, out _))
        {
            throw new ArgumentException("The tenant ID must be a GUID.", nameof(value));
        }
    }

    public static void ValidateConversationId(string value) =>
        ValidatePathSegment(value, nameof(value));

    public static void ValidatePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%'))
        {
            throw new ArgumentException($"The {parameterName} contains an unsafe path value.", parameterName);
        }
    }

    public static bool IsExpectedRequestResource(string resource, McsLiveTestTarget target)
    {
        var endpoint = target.Endpoint ?? GetEndpoint(target);
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) ||
            !IsSameAuthority(uri, endpoint) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !IsSupportedSdkQuery(uri.Query, endpoint.Query, target.AgentType))
        {
            return false;
        }

        var prefix = endpoint.AbsolutePath;
        return uri.AbsolutePath.Equals(prefix, StringComparison.Ordinal) ||
            (uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.Ordinal) &&
             uri.AbsolutePath[(prefix.Length + 1)..].IndexOf('/') < 0);
    }

    public static string GetAuthority(McsLiveTestTarget target)
    {
        var normalized = target.EnvironmentId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return $"{normalized[..^2]}.{normalized[^2..]}.environment.api.powerplatform.com";
    }

    public static Uri GetEndpoint(McsLiveTestTarget target)
    {
        var endpoints = LiveTestTargetEndpointsBuilder.DeriveDirectEngineUrls(target.EnvironmentId, target.SchemaName);
        return target.AgentType switch
        {
            LiveTestAgentType.Standard => endpoints.Standard,
            LiveTestAgentType.GitHubCopilot => endpoints.GitHubCopilot,
            _ => throw new ArgumentOutOfRangeException(nameof(target), "The selected agent type is not supported."),
        };
    }

    private static bool IsSameAuthority(Uri candidate, Uri expected) =>
        string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(candidate.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        candidate.Port == 443;

    public static bool IsSupportedSdkQuery(string actualQuery, string expectedQuery, LiveTestAgentType agentType) =>
        string.Equals(actualQuery, expectedQuery, StringComparison.Ordinal) ||
        agentType == LiveTestAgentType.GitHubCopilot &&
        string.Equals(actualQuery, "?api-version=2022-03-01-preview", StringComparison.Ordinal);

    public static string? GetConversationId(JsonElement activity)
    {
        if (activity.ValueKind != JsonValueKind.Object ||
            !activity.TryGetProperty("conversation", out var conversation) ||
            conversation.ValueKind != JsonValueKind.Object ||
            !conversation.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }
}
