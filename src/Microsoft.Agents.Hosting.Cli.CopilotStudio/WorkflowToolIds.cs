using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.ObjectModel;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Refuses a workspace whose workflow tools have an empty workflowId.</summary>
internal static class WorkflowToolIds
{
    /// <summary>Throws when a workflow tool the definition declares has an empty workflowId.</summary>
    /// <param name="definition">The workspace definition about to be pushed.</param>
    public static void RequireNotEmpty(DefinitionBase definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var empty = Components(definition)
            .Where(HasEmptyWorkflowId)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (empty.Length > 0)
        {
            throw CopilotStudioException.WorkflowToolIdEmpty(empty);
        }
    }

    private static ImmutableArray<BotComponentBase> Components(DefinitionBase definition) => definition.Components.IsDefault ? [] : definition.Components;

    private static bool HasEmptyWorkflowId(BotComponentBase component) =>
        component is not null
        && component.DescendantsAndSelf().OfType<WorkflowTool>().Any(tool => !tool.WorkflowId.HasValue || tool.WorkflowId.Value == Guid.Empty);

    private static string Describe(BotComponentBase component)
    {
        var schemaName = component.SchemaNameString;
        var displayName = component.DisplayName;

        if (string.IsNullOrWhiteSpace(schemaName))
        {
            return string.IsNullOrWhiteSpace(displayName) ? "(unnamed)" : displayName;
        }

        return string.IsNullOrWhiteSpace(displayName) ? schemaName : $"{displayName} ({schemaName})";
    }
}
