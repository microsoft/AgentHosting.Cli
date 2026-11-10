using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>Keeps raw Dataverse responses out of a failed command's report by restating them with the sync library's redacted description.</summary>
public static class DataverseFailureRedaction
{
    /// <summary>The failure a command reports: an untyped failure caused by a Dataverse request becomes the redacted service error, a typed failure keeps its codes with every raw response it quotes redacted, and any other failure is returned unchanged.</summary>
    /// <param name="failure">What the command threw.</param>
    /// <param name="operation">The command, named in the service error.</param>
    /// <returns>The failure to report.</returns>
    public static Exception ForReport(Exception failure, string operation)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure is not AgentHostingException typed)
        {
            return DataverseFailureText.RequestFailuresOf(failure) is [var requestFailure, ..] ? CopilotStudioException.ServiceError(operation, requestFailure.Describe()) : failure;
        }

        var message = DataverseFailureText.Of(typed);
        var remediation = typed.Remediation is null ? null : DataverseFailureText.Redact(typed.Remediation);

        return message == typed.Message && remediation == typed.Remediation ? typed : new RedactedFailure(typed, message, remediation);
    }

    private sealed class RedactedFailure(AgentHostingException failure, string message, string? remediation) : AgentHostingException(message, failure.ExitCode, failure.ErrorKind, remediation, errorCode: failure.ErrorCode);
}
