using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Asks the operator to confirm an AMS delete by typing the name of what it deletes.</summary>
internal static class AmsConfirmation
{
    /// <summary>Returns the status of a delete the operator did not confirm, or null when the delete goes on.</summary>
    internal static async Task<string?> StoppedStatusAsync(IConfirmationPrompt prompt, bool nonInteractive, IEnumerable<string> plan, string expectedResponse, CancellationToken cancellationToken) => await ConfirmationPolicy.ConfirmAsync(prompt, nonInteractive, string.Join(Environment.NewLine, plan), expectedResponse, cancellationToken).ConfigureAwait(false) switch
    {
        ConfirmationOutcome.Confirmed => null,
        ConfirmationOutcome.Mismatched => AgentAmsConfirmation.NameMismatchStatus,
        _ => AgentAmsConfirmation.CancelledStatus,
    };
}
