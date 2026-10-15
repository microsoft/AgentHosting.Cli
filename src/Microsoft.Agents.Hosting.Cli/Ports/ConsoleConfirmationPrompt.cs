using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Ports;

/// <summary>
/// Confirms a destructive action by requiring the operator to type the subject's name.
/// </summary>
public sealed class ConsoleConfirmationPrompt : IConfirmationPrompt
{
    private readonly TextReader _input;
    private readonly TextWriter _output;

    /// <summary>Creates the prompt.</summary>
    /// <param name="input">Where the operator's answer is read from.</param>
    /// <param name="output">Where the prompt is written.</param>
    public ConsoleConfirmationPrompt(TextReader input, TextWriter output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <inheritdoc />
    public async Task<ConfirmationOutcome> ConfirmAsync(string detail, string expectedResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _output.WriteLineAsync(detail).ConfigureAwait(false);
        await _output.WriteAsync($"Confirm by typing ({expectedResponse}): ").ConfigureAwait(false);

        var answer = (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

        return string.Equals(answer, expectedResponse, StringComparison.Ordinal) || string.Equals(Unquote(answer), expectedResponse, StringComparison.Ordinal)
            ? ConfirmationOutcome.Confirmed
            : string.IsNullOrEmpty(answer) ? ConfirmationOutcome.Declined
            : ConfirmationOutcome.Mismatched;
    }

    /// <summary>
    /// Removes one matching pair of surrounding quotes.
    /// </summary>
    /// <param name="answer">What the operator typed.</param>
    /// <returns>The answer without its surrounding quotes.</returns>
    private static string? Unquote(string? answer) => answer is { Length: >= 2 } && (answer[0] == '"' || answer[0] == '\'') && answer[^1] == answer[0] ? answer[1..^1] : answer;
}
