using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Checks agent display names, and writes new workspaces, through the sync library's scaffolder.</summary>
public sealed class SyncScaffoldAdapter : ICopilotScaffoldPort
{
    /// <inheritdoc />
    public string ValidateDisplayName(string displayName)
    {
        try
        {
            AgentWorkspaceIdentity.ValidateDisplayName(displayName);

            return displayName;
        }
        catch (AgentScaffoldException failure)
        {
            throw new UsageException(failure.Message);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Scaffold(CopilotScaffoldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Translated(() => AgentScaffolder.Scaffold(command.ProjectDirectory, command.TemplateFolder, command.DisplayName, command.SchemaName, command.Instructions));
    }

    private static T Translated<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (AgentScaffoldException failure)
        {
            throw Translate(failure);
        }
    }

    private static Exception Translate(AgentScaffoldException failure) => failure.Failure switch
    {
        AgentScaffoldFailure.TemplateNotFound => CopilotStudioException.TemplateNotFound(failure.Subject ?? string.Empty),
        _ => new UsageException(failure.Message),
    };
}
