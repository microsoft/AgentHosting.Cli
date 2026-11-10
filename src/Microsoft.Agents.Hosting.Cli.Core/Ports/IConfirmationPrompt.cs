namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

public enum ConfirmationOutcome
{
    /// <summary>The operator typed the expected text.</summary>
    Confirmed,

    /// <summary>The operator answered nothing, which reads as a deliberate decline.</summary>
    Declined,

    /// <summary>The operator typed something, but not the expected text.</summary>
    Mismatched,
}

/// <summary>
/// Asks the operator to confirm a destructive action.
/// </summary>
public interface IConfirmationPrompt
{
    /// <summary>Asks the operator to confirm by typing <paramref name="expectedResponse"/>.</summary>
    /// <param name="detail">What is about to happen, already formatted for display.</param>
    /// <param name="expectedResponse">The text the operator must type to proceed.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>Whether the operator confirmed, and if not, why.</returns>
    Task<ConfirmationOutcome> ConfirmAsync(string detail, string expectedResponse, CancellationToken cancellationToken);
}

/// <summary>Asks the operator a question answered yes or no, where an empty answer means yes.</summary>
public interface IYesNoPrompt
{
    /// <summary>Asks the question and reads the answer.</summary>
    /// <param name="question">The question, ending in [Y/n].</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>Whether the operator answered yes.</returns>
    Task<bool> AskAsync(string question, CancellationToken cancellationToken);
}
/// <summary>
/// Decides whether a destructive action still has to be confirmed.
///
/// Asking not to be prompted is itself the consent: an operator who passes
/// <c>--non-interactive</c> to a delete has said what they want, and there is nobody at a terminal to
/// type the name. Refusing instead, as this once did, left no way to delete from a script at all.
/// </summary>
public static class ConfirmationPolicy
{
    /// <summary>Confirms an action unless the operator already declined to be asked.</summary>
    /// <param name="prompt">The prompt to ask when interaction is allowed.</param>
    /// <param name="nonInteractive">Whether the operator asked not to be prompted.</param>
    /// <param name="detail">What is about to happen, already formatted for display.</param>
    /// <param name="expectedResponse">The text the operator would otherwise have to type.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    /// <returns>Whether the action may proceed.</returns>
    public static Task<ConfirmationOutcome> ConfirmAsync(IConfirmationPrompt prompt, bool nonInteractive, string detail, string expectedResponse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        return nonInteractive
            ? Task.FromResult(ConfirmationOutcome.Confirmed)
            : prompt.ConfirmAsync(detail, expectedResponse, cancellationToken);
    }
}
