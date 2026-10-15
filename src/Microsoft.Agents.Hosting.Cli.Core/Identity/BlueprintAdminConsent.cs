namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal static class BlueprintAdminConsent
{
    internal const string RedirectUri = "https://entra.microsoft.com/TokenAuthorize";

    public static string BuildUrl(
        string tenantId,
        string applicationId,
        IEnumerable<BlueprintPermission> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentNullException.ThrowIfNull(permissions);

        var scopes = permissions
            .Select(permission =>
                $"{permission.ResourceIdentifierUri ?? $"api://{permission.ResourceAppId}"}/" +
                permission.DelegatedScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Uri.EscapeDataString);

        return $"https://login.microsoftonline.com/{tenantId}/v2.0/adminconsent" +
            $"?client_id={Uri.EscapeDataString(applicationId)}" +
            $"&scope={string.Join("%20", scopes)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={Guid.NewGuid():N}";
    }
}
