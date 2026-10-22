using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Preserves secret-free App Registration progress when a typed failure occurs after an application
/// has been discovered or created. The original failure taxonomy remains authoritative.
/// </summary>
public sealed class AppRegistrationIdentityProvisioningException : AgentHostingException
{
    public AppRegistrationIdentityProvisioningException(
        AgentHostingException cause,
        AppRegistrationIdentityReport partialReport)
        : base(
            cause.Message,
            cause.ExitCode,
            cause.ErrorKind,
            cause.Remediation,
            cause,
            cause.ErrorCode)
    {
        ArgumentNullException.ThrowIfNull(partialReport);
        if (partialReport.Configuration is not null)
        {
            throw new ArgumentException(
                "A partial App Registration report cannot contain generated configuration.",
                nameof(partialReport));
        }

        PartialReport = partialReport;
    }

    public AppRegistrationIdentityReport PartialReport { get; }
}
