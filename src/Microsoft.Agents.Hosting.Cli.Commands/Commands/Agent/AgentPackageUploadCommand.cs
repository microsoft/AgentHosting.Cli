using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent package upload</c>.</summary>
[Command("upload", Description = "Upload exact package bytes and confirm a durable AMS Package Revision and its commit.", ParentGroup = typeof(AgentPackageGroup), SortPriority = 10, ProgressLabel = "Uploading package")]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentPackageUploadCommand : CommandBase<AgentPackageUploadRequest, AgentPackageUploadResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. AmsRoute.AddressSymbols(withDeployment: false),
        OptionCatalog.Value("--package", "Exact package file to upload. Never built or inspected by the CLI.", required: true),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes => [AmsRoute.Usage("Upload a package of an AMS code agent", withDeployment: false, "--package")];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input) => AmsRoute.Problem(input, new AmsRouteRules("agent package upload"));

    /// <inheritdoc />
    protected override AgentPackageUploadRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return AmsRoute.Address(input, new AgentPackageUploadRequest { PackagePath = input.GetString("--package") });
    }
}
