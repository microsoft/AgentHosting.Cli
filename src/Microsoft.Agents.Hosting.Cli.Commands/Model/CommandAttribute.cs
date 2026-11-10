namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>Marks a class as one CLI command, discovered by <see cref="CommandReflector"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>Declares the command.</summary>
    /// <param name="name">The verb as typed, lowercase.</param>
    public CommandAttribute(string name) => Name = name;

    /// <summary>The verb as typed.</summary>
    public string Name { get; }

    /// <summary>One-line help text.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The group this command hangs from.</summary>
    public Type? ParentGroup { get; init; }

    /// <summary>The command this command nests under, for a command that is itself a parent.</summary>
    public Type? ParentCommand { get; init; }

    /// <summary>Orders siblings in help; ties break on ordinal name.</summary>
    public int SortPriority { get; init; }

    /// <summary>The progress spinner text shown while the command runs, such as "Deploying agent".</summary>
    public string? ProgressLabel { get; init; }

    /// <summary>Keeps the progress spinner hidden while the command runs.</summary>
    public bool SuppressProgress { get; init; }

    /// <summary>Leaves the command out of help and of the commands a group lists, for a command another program runs.</summary>
    public bool Hidden { get; init; }

    /// <summary>Suppresses command telemetry for this command and every command nested under it.</summary>
    public bool SuppressTelemetry { get; init; }
}

/// <summary>Marks a class as a command group — a node that owns children rather than an action.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandGroupAttribute : Attribute
{
    /// <summary>Declares the group.</summary>
    /// <param name="name">The group name as typed, lowercase.</param>
    public CommandGroupAttribute(string name) => Name = name;

    /// <summary>The group name as typed.</summary>
    public string Name { get; }

    /// <summary>One-line help text.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The group this group hangs from, or null for a root group.</summary>
    public Type? ParentGroup { get; init; }

    /// <summary>Orders siblings in help; ties break on ordinal name.</summary>
    public int SortPriority { get; init; }

    /// <summary>Overrides the generated "no command specified" line.</summary>
    public string? NoCommandUsage { get; init; }

    /// <summary>
    /// Suppresses command telemetry for this group and every descendant.
    /// </summary>
    public bool SuppressTelemetry { get; init; }
}
