using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>agent</c> group.</summary>
[CommandGroup("agent", Description = "Agent Hosting agent operations.", SortPriority = 30)]
public sealed class AgentGroup;

/// <summary>The <c>agent package</c> group.</summary>
[CommandGroup("package", Description = "Agent Hosting package operations.", ParentGroup = typeof(AgentGroup), SortPriority = 60, NoCommandUsage = "No package command specified. Valid commands: upload. Run `msagent agent package --help` for usage.")]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentPackageGroup;

/// <summary>The <c>agent eval</c> group.</summary>
[CommandGroup("eval", Description = "Run Copilot Studio evaluations against an MCS agent. MCS only.", ParentGroup = typeof(AgentGroup), SortPriority = 100, NoCommandUsage = "No eval command specified. Valid subgroups: testset. Valid commands: run, list, show, cancel, delete, generate. Run `msagent agent eval --help` for usage.")]
[RequiredFeatureFlag(FeatureName.EvaluationFeatures)]
public sealed class AgentEvalGroup;

/// <summary>The <c>agent eval testset</c> group.</summary>
[CommandGroup("testset", Description = "Manage the conversation sets an evaluation runs.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 10, NoCommandUsage = "No testset command specified. Valid commands: list, show, create, update, export, delete. Run `msagent agent eval testset --help` for usage.")]
[RequiredFeatureFlag(FeatureName.EvaluationFeatures)]
public sealed class AgentEvalTestSetGroup;