using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// The default <see cref="IInteractiveMessagePresenter"/>: writes the interactive sign-in instruction
/// (the device-code verification message) to standard error, keeping it out of the stdout JSON/data
/// stream. PAC writes the same message via its <c>IOutputWindow</c>. This presenter reads no input and
/// never blocks on a prompt.
/// </summary>
public sealed class ConsoleInteractiveMessagePresenter : IInteractiveMessagePresenter
{
    private readonly TextWriter _output;

    /// <summary>Creates a presenter that writes to <paramref name="output"/>.</summary>
    /// <param name="output">The standard-error writer messages are written to.</param>
    public ConsoleInteractiveMessagePresenter(TextWriter output) => _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <inheritdoc />
    public Task PresentAsync(string message, CancellationToken cancellationToken)
    {
        _output.WriteLine(message);
        return Task.CompletedTask;
    }
}
