using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>test</c> group.</summary>
[CommandGroup("test", Description = "Test agents with live queries.", SortPriority = 25, NoCommandUsage = "No test command specified. Valid commands: ask, playground. Run `msagent test --help` for usage.")]
[RequiredFeatureFlag(FeatureName.LiveTestFeatures)]
public sealed class TestGroup;
