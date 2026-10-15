using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;

/// <summary><c>msagent agent package upload</c>.</summary>
[Command("upload", Description = "Upload exact package bytes and confirm a durable AMS Package Revision.", ParentGroup = typeof(AgentPackageGroup), SortPriority = 10)]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentPackageUploadCommand : CommandBase<AgentPackageUploadRequest, AgentPackageUploadResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--agent-id", "Existing repository-bound CodeAgent id.", required: true),
        OptionCatalog.Value("--package", "Exact package file to upload. Never built or inspected by the CLI.", required: true),
        OptionCatalog.Value("--environment-id", "Agent home environment. Defaults to the active environment."),
    ];

    /// <inheritdoc />
    protected override AgentPackageUploadRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentPackageUploadRequest
        {
            AgentId = input.GetString("--agent-id"),
            PackagePath = input.GetString("--package"),
            EnvironmentId = input.GetString("--environment-id"),
        };
    }
}
