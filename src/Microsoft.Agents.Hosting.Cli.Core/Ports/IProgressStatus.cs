namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Shows the current step of a long-running command on the host's transient status line.</summary>
public interface IProgressStatus
{
    /// <summary>Replaces the step shown on the status line.</summary>
    /// <param name="status">The step, such as "Pushing changes".</param>
    /// <returns>Whether a live status line shows the step, so the caller need not report it another way.</returns>
    bool Report(string status);
}
