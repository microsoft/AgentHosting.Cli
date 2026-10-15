using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Identity;

/// <summary><c>msagent identity create app</c>.</summary>
[Command("app", Description = "Create or reconcile an Entra App Registration identity.", ParentGroup = typeof(IdentityCreateGroup), SortPriority = 20)]
public sealed class IdentityCreateAppCommand : CommandBase<AppRegistrationIdentityRequest, AppRegistrationIdentityReport>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. IdentityOptions.Shared(
            "Display name for the App Registration.",
            "Credential mode: UserManagedIdentity, FederatedCredentials, or ClientSecret.",
            "OAuth redirect URI (requires --oauth or --teams-sso).",
            "Expose access_as_user and preauthorize supported Teams/Office clients (includes --oauth)."),
        OptionCatalog.Value("--app-id", "Application (client) id of an existing App Registration to target."),
        OptionCatalog.Value("--object-id", "Application object id of an existing App Registration to target."),
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrWhiteSpace(input.GetString("--redirect-uri")) && !input.GetSwitch("--oauth") && !input.GetSwitch("--teams-sso"))
        {
            return "--redirect-uri requires --oauth or --teams-sso. Run `msagent identity create app --help` for usage.";
        }

        if (IdentityOptionValues.ParseAppRegistrationAuthType(input.GetString("--auth-type")) is not { } authType)
        {
            return "--auth-type must be UserManagedIdentity, FederatedCredentials, or ClientSecret.";
        }

        if (IdentityOptionValues.ParseConfigFormat(input.GetString("--config-format")) is null)
        {
            return "--config-format must be dotnet or node.";
        }

        var applicationId = input.GetString("--app-id");
        var objectId = input.GetString("--object-id");

        if (!string.IsNullOrWhiteSpace(applicationId) && !Guid.TryParse(applicationId, out _))
        {
            return "--app-id must be a valid application (client) id GUID.";
        }

        if (!string.IsNullOrWhiteSpace(objectId) && !Guid.TryParse(objectId, out _))
        {
            return "--object-id must be a valid application object id GUID.";
        }

        if (!string.IsNullOrWhiteSpace(applicationId) && !string.IsNullOrWhiteSpace(objectId))
        {
            return "--app-id and --object-id cannot be used together.";
        }

        if (authType is IdentityCredentialType.UserManagedIdentity or IdentityCredentialType.FederatedCredentials
            && !IdentityOptions.HasCompleteManagedIdentity(input))
        {
            return $"{authType} requires --subscription, --resource-group, --location, and --managed-identity-name.";
        }

        return authType == IdentityCredentialType.ClientSecret && IdentityOptions.HasAnyManagedIdentity(input)
            ? "ClientSecret must not be combined with --subscription, --resource-group, --location, or --managed-identity-name."
            : null;
    }

    /// <inheritdoc />
    protected override AppRegistrationIdentityRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (subscription, resourceGroup, location, managedIdentityName) = IdentityOptions.ManagedIdentity(input);

        return new AppRegistrationIdentityRequest
        {
            TenantId = input.GetString("--tenant")!,
            DisplayName = input.GetString("--display-name")!,
            AuthType = IdentityOptionValues.ParseAppRegistrationAuthType(input.GetString("--auth-type"))!.Value,
            ConfigFormat = IdentityOptionValues.ParseConfigFormat(input.GetString("--config-format"))!.Value,
            ApplicationId = input.GetString("--app-id"),
            ApplicationObjectId = input.GetString("--object-id"),
            SubscriptionId = subscription,
            ResourceGroup = resourceGroup,
            Location = location,
            ManagedIdentityName = managedIdentityName,
            OAuth = input.GetSwitch("--oauth"),
            RedirectUri = input.GetString("--redirect-uri"),
            TeamsSso = input.GetSwitch("--teams-sso"),
            Observability = input.GetSwitch("--observability"),
            McpDiscovery = input.GetSwitch("--mcp-discovery"),
            DryRun = input.GetSwitch("--dry-run"),
            NonInteractive = input.NonInteractive,
        };
    }
}
