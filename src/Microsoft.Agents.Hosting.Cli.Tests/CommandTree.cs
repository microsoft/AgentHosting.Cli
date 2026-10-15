using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Walks the discovered command tree, which several guards need.</summary>
internal static class CommandTree
{
    /// <summary>Every command a group owns, including the subcommands beneath them.</summary>
    /// <param name="group">The group to walk.</param>
    internal static IEnumerable<CommandMetadata> AllCommands(CommandGroupMetadata group) =>
        group.Commands.SelectMany(Descend).Concat(group.Groups.SelectMany(AllCommands));

    /// <summary>A command and every subcommand beneath it.</summary>
    /// <param name="command">The command to walk.</param>
    internal static IEnumerable<CommandMetadata> Descend(CommandMetadata command) =>
        [command, .. command.Commands.SelectMany(Descend)];
}
