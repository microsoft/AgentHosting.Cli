using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Identity;

/// <summary>The option set both <c>msagent identity create</c> verbs share.</summary>
public static class IdentityOptions
{
    /// <summary>Builds the shared options, with the two descriptions that differ per verb.</summary>
    /// <param name="displayNameDescription">Help for <c>--display-name</c>.</param>
    /// <param name="authTypeDescription">Help for <c>--auth-type</c>.</param>
    /// <param name="redirectUriDescription">Help for <c>--redirect-uri</c>.</param>
    /// <param name="teamsSsoDescription">Help for <c>--teams-sso</c>.</param>
    public static IReadOnlyList<CommandSymbol> Shared(
        string displayNameDescription,
        string authTypeDescription,
        string redirectUriDescription = "OAuth redirect URI (requires --oauth).",
        string teamsSsoDescription = "Expose access_as_user and preauthorize supported Teams/Office clients.") =>
    [
        OptionCatalog.Value("--tenant", "Target Entra tenant id.", required: true),
        OptionCatalog.Value("--display-name", displayNameDescription, required: true),
        OptionCatalog.Value("--auth-type", authTypeDescription, required: true),
        OptionCatalog.Value("--config-format", "Generated runtime configuration: dotnet or node.", required: true),
        OptionCatalog.Value("--subscription", "Azure subscription for the user-assigned managed identity."),
        OptionCatalog.Value("--resource-group", "Azure resource group for the user-assigned managed identity."),
        OptionCatalog.Value("--location", "Azure region for the user-assigned managed identity."),
        OptionCatalog.Value("--managed-identity-name", "User-assigned managed identity name."),
        OptionCatalog.Switch("--oauth", "Configure an OAuth redirect URI."),
        OptionCatalog.Value("--redirect-uri", redirectUriDescription),
        OptionCatalog.Switch("--teams-sso", teamsSsoDescription),
        OptionCatalog.Switch("--observability", "Add A365 Observability permissions."),
        OptionCatalog.Switch("--mcp-discovery", "Add A365 MCP Discovery permissions."),
        OptionCatalog.Switch("--dry-run", "Show the mutation plan without changing Entra."),
    ];

    /// <summary>Reports the managed-identity coordinates the caller supplied.</summary>
    /// <param name="input">The parsed command line.</param>
    public static (string? Subscription, string? ResourceGroup, string? Location, string? Name) ManagedIdentity(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return (input.GetString("--subscription"), input.GetString("--resource-group"), input.GetString("--location"), input.GetString("--managed-identity-name"));
    }

    /// <summary>Whether every managed-identity coordinate is present.</summary>
    /// <param name="input">The parsed command line.</param>
    public static bool HasCompleteManagedIdentity(ICommandInput input) =>
        ManagedIdentity(input) is var (subscription, resourceGroup, location, name)
        && !string.IsNullOrWhiteSpace(subscription)
        && !string.IsNullOrWhiteSpace(resourceGroup)
        && !string.IsNullOrWhiteSpace(location)
        && !string.IsNullOrWhiteSpace(name);

    /// <summary>Whether any managed-identity coordinate is present.</summary>
    /// <param name="input">The parsed command line.</param>
    public static bool HasAnyManagedIdentity(ICommandInput input) =>
        ManagedIdentity(input) is var (subscription, resourceGroup, location, name)
        && (!string.IsNullOrWhiteSpace(subscription)
            || !string.IsNullOrWhiteSpace(resourceGroup)
            || !string.IsNullOrWhiteSpace(location)
            || !string.IsNullOrWhiteSpace(name));
}
