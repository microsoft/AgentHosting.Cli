using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Groups;

/// <summary>The <c>auth</c> group.</summary>
[CommandGroup("auth", Description = "Operator authentication.", SortPriority = 10)]
public sealed class AuthGroup;
