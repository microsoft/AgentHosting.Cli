using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Preserves secret-free Blueprint progress when a typed failure occurs after an application has
/// been discovered or created. The original failure taxonomy remains authoritative.
/// </summary>
public sealed class BlueprintIdentityProvisioningException : AgentHostingException
{
    public BlueprintIdentityProvisioningException(
        AgentHostingException cause,
        BlueprintIdentityReport partialReport)
        : base(
            cause.Message,
            cause.ExitCode,
            cause.ErrorKind,
            cause.Remediation,
            cause)
    {
        ArgumentNullException.ThrowIfNull(partialReport);
        if (partialReport.Configuration is not null)
        {
            throw new ArgumentException(
                "A partial Blueprint report cannot contain generated configuration.",
                nameof(partialReport));
        }

        PartialReport = partialReport;
    }

    public BlueprintIdentityReport PartialReport { get; }
}
