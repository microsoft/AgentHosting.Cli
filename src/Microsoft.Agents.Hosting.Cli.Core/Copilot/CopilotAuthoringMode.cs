using System.Collections.Frozen;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>The scaffold template an agent type resolves to.</summary>
public sealed record CopilotScaffoldTemplate
{
    /// <summary>The canonical authoring mode id.</summary>
    public required string AuthoringMode { get; init; }

    /// <summary>The template name the operator asked for, as reported back.</summary>
    public required string TemplateName { get; init; }

    /// <summary>The embedded resource folder holding the template content.</summary>
    public required string TemplateFolder { get; init; }
}

/// <summary>The scaffold shape <c>--mcs-agent-type</c> selects.</summary>
public enum McsAgentType
{
    /// <summary>The CLI-authored agent, and the default.</summary>
    Cli,

    /// <summary>The classic authoring experience.</summary>
    Classic,
}

/// <summary>
/// Resolves <c>--mcs-agent-type</c> and <c>--init-template</c> to a scaffold template.
/// </summary>
public static class CopilotAuthoringModes
{
    /// <summary>The classic authoring mode id.</summary>
    public const string Classic = "classic";

    /// <summary>The CLI-authored agent mode id, and the default.</summary>
    public const string CliCopilot = "cli-copilot";

    /// <summary>The only template the classic scaffold ships.</summary>
    public const string DefaultTemplateName = "default";

    private static readonly FrozenDictionary<string, string> Modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Classic] = Classic,
        [CliCopilot] = CliCopilot,
        ["cli"] = CliCopilot,
        ["clicopilot"] = CliCopilot,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The agent-type spellings <c>--mcs-agent-type</c> accepts, for error text.</summary>
    public static readonly IReadOnlyList<string> SupportedAgentTypes = ["CLI", "Classic"];

    /// <summary>The message for a value outside the accepted spellings.</summary>
    /// <param name="mcsAgentType">The rejected value.</param>
    public static string UnknownAgentType(string? mcsAgentType) =>
        $"Unknown --mcs-agent-type '{mcsAgentType}'. Supported values: {string.Join(", ", SupportedAgentTypes)}.";

    /// <summary>Matches a caller-supplied agent type against the accepted spellings.</summary>
    /// <param name="mcsAgentType">The caller's spelling, or <see langword="null"/> for the default.</param>
    /// <param name="agentType">The matched type, defaulting to <see cref="McsAgentType.Cli"/>.</param>
    /// <returns><see langword="true"/> when the value is absent or recognised.</returns>
    public static bool TryParse(string? mcsAgentType, out McsAgentType agentType)
    {
        agentType = McsAgentType.Cli;

        if (string.IsNullOrWhiteSpace(mcsAgentType))
        {
            return true;
        }

        if (!Modes.TryGetValue(mcsAgentType.Trim(), out var mode))
        {
            return false;
        }

        agentType = mode == Classic ? McsAgentType.Classic : McsAgentType.Cli;

        return true;
    }

    /// <summary>Resolves the template to scaffold from.</summary>
    /// <param name="mcsAgentType">The requested agent type, or <see langword="null"/> for the default.</param>
    /// <param name="initTemplate">The requested classic template name, or <see langword="null"/> for the default.</param>
    /// <returns>The resolved template.</returns>
    public static CopilotScaffoldTemplate Resolve(string? mcsAgentType, string? initTemplate = null)
    {
        var requested = string.IsNullOrWhiteSpace(mcsAgentType) ? CliCopilot : mcsAgentType.Trim();

        if (!Modes.TryGetValue(requested, out var mode))
        {
            throw new UsageException(UnknownAgentType(mcsAgentType));
        }

        if (mode != Classic && !string.IsNullOrWhiteSpace(initTemplate))
        {
            throw new UsageException($"--init-template applies to --mcs-agent-type Classic. The {CliCopilot} scaffold has a single shape.");
        }

        if (mode == Classic && !string.IsNullOrWhiteSpace(initTemplate) && !DefaultTemplateName.Equals(initTemplate.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw CopilotStudioException.TemplateNotFound(initTemplate);
        }

        return mode == Classic
            ? new CopilotScaffoldTemplate { AuthoringMode = Classic, TemplateName = DefaultTemplateName, TemplateFolder = Classic }
            : CliCopilotTemplate();
    }

    private static CopilotScaffoldTemplate CliCopilotTemplate() => new()
    {
        AuthoringMode = CliCopilot,
        TemplateName = CliCopilot,
        TemplateFolder = CliCopilot,
    };
}
