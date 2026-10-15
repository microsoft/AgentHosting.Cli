using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>identity</c> group.</summary>
[CommandGroup("identity", Description = "Create and manage agent identities.", SortPriority = 50, NoCommandUsage = "No identity command specified. Valid commands: create. Run `msagent --help` for usage.")]
[RequiredFeatureFlag(FeatureName.IdentityFeatures)]
public sealed class IdentityGroup;

/// <summary>The <c>identity create</c> group.</summary>
[CommandGroup("create", Description = "Create an agent identity.", ParentGroup = typeof(IdentityGroup), SortPriority = 10, NoCommandUsage = "No identity type specified. Valid commands: blueprint, app. Run `msagent --help` for usage.")]
public sealed class IdentityCreateGroup;
