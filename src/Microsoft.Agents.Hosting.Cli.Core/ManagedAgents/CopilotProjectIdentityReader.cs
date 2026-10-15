using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The identity a local MCS project declares for itself.</summary>
public sealed record ProjectIdentity
{
    /// <summary>The project's schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>The project's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The project's description, when it declares one.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Reads a workspace's identity from its settings file.
/// </summary>
public sealed partial class CopilotProjectIdentityReader
{
    private readonly AgentConfigLocator _locator;

    /// <summary>Creates the reader.</summary>
    /// <param name="locator">Resolves workspace directories and their settings files.</param>
    public CopilotProjectIdentityReader(AgentConfigLocator locator) => _locator = locator ?? throw new ArgumentNullException(nameof(locator));

    /// <summary>Reads the identity a workspace declares, failing when there is no readable project.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <param name="agentId">The record the identity is being read for, named in the failure.</param>
    /// <returns>The workspace's declared identity.</returns>
    public ProjectIdentity Require(string workspaceDirectory, string agentId) => Find(workspaceDirectory) ?? throw ManagedAgentException.SchemaNameUnavailable(agentId, _locator.ResolveProjectDirectory(workspaceDirectory));

    /// <summary>Reads the identity a workspace declares, or <see langword="null"/> when there is none.</summary>
    /// <param name="workspaceDirectory">The directory holding the workspace.</param>
    /// <returns>The workspace's declared identity, or <see langword="null"/>.</returns>
    public ProjectIdentity? Find(string workspaceDirectory)
    {
        var resolved = _locator.ResolveProjectDirectory(workspaceDirectory);

        var settingsPath = new[] { AgentConfigLocator.ProjectSettingsFileName, AgentConfigLocator.ClassicProjectFileName }
            .Select(name => Path.Combine(resolved, name))
            .FirstOrDefault(File.Exists);

        if (settingsPath is null)
        {
            return null;
        }

        var content = File.ReadAllText(settingsPath);
        var schemaName = ReadScalar(content, "schemaName");

        return string.IsNullOrWhiteSpace(schemaName)
            ? null
            : new ProjectIdentity
            {
                SchemaName = schemaName,
                DisplayName = ReadScalar(content, "displayName") ?? schemaName,
                Description = ReadScalar(content, "description"),
            };
    }

    private static string? ReadScalar(string content, string key)
    {
        var match = ScalarPattern(key).Match(content);

        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value.Trim();

        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;
    }

    private static Regex ScalarPattern(string key) => new($@"^{Regex.Escape(key)}:[ \t]*(?<value>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
}
