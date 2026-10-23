using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Identity;

/// <summary><c>msagent identity create blueprint</c>.</summary>
[Command("blueprint", Description = "Create or reconcile an Entra Agent Identity Blueprint.", ParentGroup = typeof(IdentityCreateGroup), SortPriority = 10, ProgressLabel = "Creating identity")]
public sealed class IdentityCreateBlueprintCommand : CommandBase<BlueprintIdentityRequest, BlueprintIdentityReport>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => IdentityOptions.Shared(
        "Display name for the Agent Identity Blueprint.",
        "Credential mode: FederatedCredentials or ClientSecret.");

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrWhiteSpace(input.GetString("--redirect-uri")) && !input.GetSwitch("--oauth"))
        {
            return "--redirect-uri requires --oauth. Run `msagent identity create blueprint --help` for usage.";
        }

        if (IdentityOptionValues.ParseBlueprintAuthType(input.GetString("--auth-type")) is not { } authType)
        {
            return "--auth-type must be FederatedCredentials or ClientSecret.";
        }

        if (IdentityOptionValues.ParseConfigFormat(input.GetString("--config-format")) is null)
        {
            return "--config-format must be dotnet or node.";
        }

        return authType == IdentityCredentialType.FederatedCredentials && !IdentityOptions.HasCompleteManagedIdentity(input)
            ? "FederatedCredentials requires --subscription, --resource-group, --location, and --managed-identity-name."
            : null;
    }

    /// <inheritdoc />
    protected override BlueprintIdentityRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (subscription, resourceGroup, location, managedIdentityName) = IdentityOptions.ManagedIdentity(input);

        return new BlueprintIdentityRequest
        {
            TenantId = input.GetString("--tenant")!,
            DisplayName = input.GetString("--display-name")!,
            AuthType = IdentityOptionValues.ParseBlueprintAuthType(input.GetString("--auth-type"))!.Value,
            ConfigFormat = IdentityOptionValues.ParseConfigFormat(input.GetString("--config-format"))!.Value,
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
