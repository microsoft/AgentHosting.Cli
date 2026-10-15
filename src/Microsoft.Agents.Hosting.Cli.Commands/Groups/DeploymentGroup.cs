using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>deployment</c> group.</summary>
[CommandGroup("deployment", Description = "Manage agent deployments, lifecycle, connections, channels and OAuth.", SortPriority = 60)]
public sealed class DeploymentGroup;

/// <summary>The <c>deployment connection</c> group.</summary>
[CommandGroup("connection", Description = "Inspect and bind a deployment's connection references, and create the connections they use. MCS only.", ParentGroup = typeof(DeploymentGroup), SortPriority = 10)]
public sealed class DeploymentConnectionGroup;

/// <summary>The <c>deployment channel</c> group.</summary>
[CommandGroup("channel", Description = "Manage channels configuration on a deployment. CodeAgent Only", ParentGroup = typeof(DeploymentGroup), SortPriority = 20)]
public sealed class DeploymentChannelGroup;

/// <summary>The <c>deployment oauth</c> group.</summary>
[CommandGroup("oauth", Description = "Manage OAuth connection settings on a deployment. CodeAgent Only", ParentGroup = typeof(DeploymentGroup), SortPriority = 30)]
public sealed class DeploymentOAuthGroup;
