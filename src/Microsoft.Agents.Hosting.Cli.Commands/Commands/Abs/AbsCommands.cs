using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Abs;

/// <summary>Binds the provisioning options the <c>abs</c> group declares recursively.</summary>
/// <typeparam name="TRequest">The request the verb binds.</typeparam>
public abstract class AbsCommandBase<TRequest> : CommandBase<TRequest, AbsProvisioningReport>
    where TRequest : AbsProvisioningRequest, new()
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    /// <inheritdoc />
    protected override TRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new TRequest
        {
            Tenant = input.GetString("--tenant"),
            Subscription = input.GetString("--subscription"),
            ResourceGroup = input.GetString("--resource-group"),
            Location = input.GetString("--location"),
            BotName = input.GetString("--bot-name"),
            Endpoint = input.GetString("--endpoint"),
            MsaAppId = input.GetString("--msa-app-id"),
            DisplayName = input.GetString("--display-name"),
            Sku = input.GetString("--sku"),
            IdentityMode = input.GetString("--identity-mode"),
            AppObjectId = input.GetString("--app-object-id"),
            DeploymentId = input.GetString("--deployment-id"),
            Force = input.GetSwitch("--force"),
            NonInteractive = input.NonInteractive,
        };
    }
}

/// <summary><c>msagent abs preview</c>.</summary>
[Command("preview", Description = "Plan the Azure Bot Service changes (read-only, never mutates).", ParentGroup = typeof(AbsGroup), SortPriority = 10)]
public sealed class AbsPreviewCommand : AbsCommandBase<AbsPreviewRequest>;

/// <summary><c>msagent abs apply</c>.</summary>
[Command("apply", Description = "Apply the Azure Bot Service plan (requires --force to mutate).", ParentGroup = typeof(AbsGroup), SortPriority = 20)]
public sealed class AbsApplyCommand : AbsCommandBase<AbsApplyRequest>;
