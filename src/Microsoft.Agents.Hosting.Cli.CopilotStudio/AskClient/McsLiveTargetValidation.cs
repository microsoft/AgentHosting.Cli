using System.Text.Json;

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

    public static string GetAuthority(McsLiveTestTarget target)
    {
        var normalized = target.EnvironmentId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return $"{normalized[..^2]}.{normalized[^2..]}.environment.api.powerplatform.com";
    }

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
