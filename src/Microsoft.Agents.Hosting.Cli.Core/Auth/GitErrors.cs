using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Builds the failures of running git, which <c>auth setup-git</c> and AMS mode share.</summary>
public static class GitErrors
{
    /// <summary>Creates the failure for git missing from PATH.</summary>
    /// <param name="message">What cannot run without git.</param>
    /// <param name="remediation">How to install git and go on.</param>
    public static AuthenticationException NotFound(string message, string remediation) => new(message, ExitCodes.Failure, remediation, ErrorCodes.Authentication.GitNotFound);

    /// <summary>Creates the failure for git refusing to write msagent's credential helper into the global git config.</summary>
    /// <param name="reason">git's reason, or null when it gave none.</param>
    public static AuthenticationException ConfigurationFailed(string? reason) => new($"git could not write msagent's credential helper into your global git config. {Reason(reason)}".TrimEnd(), ExitCodes.Failure, "Fix the problem git reports, then run `msagent auth setup-git` again.", ErrorCodes.Authentication.GitConfigurationFailed);

    /// <summary>git's reason on one line, with terminal control characters removed.</summary>
    /// <param name="reason">The reason git wrote, or null.</param>
    public static string Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : TerminalText.ServiceDetail(EvaluationFormatting.OneLine(reason));
}
