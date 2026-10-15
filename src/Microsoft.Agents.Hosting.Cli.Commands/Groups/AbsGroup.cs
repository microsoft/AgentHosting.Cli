using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>abs</c> group, whose options are recursive so <c>preview</c> and <c>apply</c> share them.</summary>
[CommandGroup("abs", Description = "Azure Bot Service preview and apply.", SortPriority = 40)]
[RequiredFeatureFlag(FeatureName.AzureBotServiceFeatures)]
public sealed class AbsGroup
{
    /// <summary>The provisioning options both verbs accept.</summary>
    public static IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Recursive("--tenant", "Target tenant id for the bot identity."),
        OptionCatalog.Recursive("--subscription", "Target subscription id."),
        OptionCatalog.Recursive("--resource-group", "Target resource group."),
        OptionCatalog.Recursive("--location", "Azure region for the control-plane resource. Pass global unless the subscription is data-resident."),
        OptionCatalog.Recursive("--bot-name", "Azure Bot Service resource name."),
        OptionCatalog.Recursive("--endpoint", "HTTPS /api/messages messaging endpoint."),
        OptionCatalog.Recursive("--msa-app-id", "Existing Entra app (client) id. Connected via ARM only."),
        OptionCatalog.Recursive("--display-name", "Friendly display name. Defaults to the bot name."),
        OptionCatalog.Recursive("--sku", "Bot SKU: F0 (default) or S1."),
        OptionCatalog.Recursive("--identity-mode", "Bot identity mode. ClientSecret is the only supported value."),
        OptionCatalog.Recursive("--app-object-id", "Entra app object id, which disambiguates a name."),
        OptionCatalog.Recursive("--deployment-id", "AgentHost deployment the connection links to."),
        OptionCatalog.RecursiveSwitch("--force", "Confirm the mutating apply. Without it, apply only previews."),
    ];
}
