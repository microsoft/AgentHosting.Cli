using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static partial class GrsUrls
{
    public static bool IsRepository(Uri gitUrl) => gitUrl.Scheme == Uri.UriSchemeHttps && Route().IsMatch(gitUrl.AbsolutePath);

    public static bool Names(string? gitUrl, string? gitHost, string? tenantId, string? environmentId, string? repositoryId) => Uri.TryCreate(gitUrl, UriKind.Absolute, out var parsed) && IsRepository(parsed) && string.Equals(parsed.Host, gitHost, StringComparison.OrdinalIgnoreCase) && Route().Match(parsed.AbsolutePath) is var route && SameGuid(route.Groups[1].Value, tenantId) && SameGuid(route.Groups[2].Value, environmentId) && AmsIds.Same(route.Groups[3].Value, repositoryId);

    private static bool SameGuid(string first, string? second) => AmsIds.Same(first.Replace("-", string.Empty, StringComparison.Ordinal), second?.Replace("-", string.Empty, StringComparison.Ordinal));

    [GeneratedRegex("^/appframework/tenants/([^/]+)/environments/([^/]+)/git/repositories/([^/]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Route();
}
