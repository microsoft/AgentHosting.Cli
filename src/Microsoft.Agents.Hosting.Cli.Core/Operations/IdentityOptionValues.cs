namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Parses the identity option spellings the CLI accepts, keeping wording and acceptance in one place.</summary>
public static class IdentityOptionValues
{
    /// <summary>Parses <c>--config-format</c>.</summary>
    /// <param name="value">The supplied spelling.</param>
    public static IdentityConfigFormat? ParseConfigFormat(string? value) => value?.ToLowerInvariant() switch
    {
        "dotnet" => IdentityConfigFormat.DotNet,
        "node" => IdentityConfigFormat.Node,
        _ => null,
    };

    /// <summary>Parses <c>--auth-type</c> for <c>identity create blueprint</c>.</summary>
    /// <param name="value">The supplied spelling.</param>
    public static IdentityCredentialType? ParseBlueprintAuthType(string? value) => value?.ToLowerInvariant() switch
    {
        "federatedcredentials" => IdentityCredentialType.FederatedCredentials,
        "clientsecret" => IdentityCredentialType.ClientSecret,
        _ => null,
    };

    /// <summary>Parses <c>--auth-type</c> for <c>identity create app</c>.</summary>
    /// <param name="value">The supplied spelling.</param>
    public static IdentityCredentialType? ParseAppRegistrationAuthType(string? value) => value?.ToLowerInvariant() switch
    {
        "usermanagedidentity" => IdentityCredentialType.UserManagedIdentity,
        "federatedcredentials" => IdentityCredentialType.FederatedCredentials,
        "clientsecret" => IdentityCredentialType.ClientSecret,
        _ => null,
    };
}
