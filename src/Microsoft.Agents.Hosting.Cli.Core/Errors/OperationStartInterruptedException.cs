namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>A cancellation that stopped the wait for the answer to a request that starts a service operation, which may still be running.</summary>
public sealed class OperationStartInterruptedException : OperationCanceledException
{
    /// <summary>Creates the exception for a canceled start request.</summary>
    /// <param name="operation">The operation the request starts, as the message names it.</param>
    /// <param name="showCommand">The command that shows whether the operation is still running.</param>
    /// <param name="cancellation">The cancellation that stopped the wait.</param>
    public OperationStartInterruptedException(string operation, string showCommand, OperationCanceledException cancellation) : base(Describe(operation), cancellation, (cancellation ?? throw new ArgumentNullException(nameof(cancellation))).CancellationToken) => Remediation = CheckHint(showCommand);

    /// <summary>The sentence that names the command that shows whether the operation is still running.</summary>
    public string Remediation { get; }

    internal static string Describe(string operation) => $"Stopped waiting for the Agent Management Service to answer; the {operation} may still be running there.";

    internal static string CheckHint(string command) => $"Run `{command}` to check on it.";
}
