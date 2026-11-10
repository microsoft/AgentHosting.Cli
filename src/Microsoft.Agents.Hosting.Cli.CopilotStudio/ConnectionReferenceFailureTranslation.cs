using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal static class ConnectionReferenceFailureTranslation
{
    /// <summary>Translates one failure, naming each declaration file by its path under the workspace.</summary>
    /// <param name="failure">What the library reported.</param>
    /// <param name="workspacePath">The workspace the declarations were read from.</param>
    /// <returns>The CLI failure naming the same cause.</returns>
    public static CopilotStudioException Translate(DeclaredConnectionReferenceException failure, string workspacePath) => failure.Failure switch
    {
        DeclaredConnectionReferenceFailure.Conflict => CopilotStudioException.ConnectionReferencesConflict(failure.LogicalName ?? string.Empty, InWorkspace(workspacePath, failure.FirstPath), failure.FirstConnectorId ?? string.Empty, InWorkspace(workspacePath, failure.Path), failure.SecondConnectorId ?? string.Empty),
        DeclaredConnectionReferenceFailure.NotConfirmed => CopilotStudioException.ServiceError("provision connection references", failure.Message),
        _ => CopilotStudioException.ConnectionReferencesUnreadable(InWorkspace(workspacePath, failure.Path), failure.Reason ?? failure.Message, failure.InnerException),
    };

    /// <summary>Translates a declared connection reference failure and returns any other failure unchanged.</summary>
    /// <param name="failure">What the library reported.</param>
    /// <param name="workspacePath">The workspace the declarations were read from.</param>
    /// <returns>The failure to report.</returns>
    public static Exception TranslateAny(Exception failure, string workspacePath) => failure is DeclaredConnectionReferenceException declared ? Translate(declared, workspacePath) : failure;

    private static string InWorkspace(string workspacePath, string? relativePath) => Path.Combine(workspacePath, (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
}
