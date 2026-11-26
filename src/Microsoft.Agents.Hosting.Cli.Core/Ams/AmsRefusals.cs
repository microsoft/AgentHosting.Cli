using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;
using AmsRefusal = (string Message, int ExitCode, string ErrorKind, string Remediation);

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The refusals that the AMS agent and deployment routes share.</summary>
internal static class AmsRefusals
{
    /// <summary>The error kind of a route or behavior that AMS does not support yet.</summary>
    internal const string LifecycleUnavailableKind = "deployment-lifecycle-unavailable";

    /// <summary>The refusal for a deployment AMS does not have, shared by every route under a deployment.</summary>
    internal static readonly AmsRefusal DeploymentNotFound = ("AMS has no such deployment of this agent; an earlier delete may already have removed it.", ExitCodes.Failure, "deployment-not-found", "Check --deployment-id. `msagent deployment list` shows the agent's deployments; create one with `msagent deployment create`.");

    /// <summary>The refusal for an agent AMS does not have in the home environment.</summary>
    internal static readonly AmsRefusal AgentNotFound = ("AMS has no such agent in the home environment; an earlier delete may already have removed it.", ExitCodes.Failure, "agent-not-found", "Check --agent-id and --environment-id, the agent's home environment, which defaults to the active one. `msagent agent list --agent-type CodeAgent` shows the environment's code agents.");

    /// <summary>The refusal for a route or behavior that the AMS release serving the environment does not support yet.</summary>
    internal static readonly AmsRefusal LifecycleUnavailable = ("AMS does not support this code-agent command in this environment yet, so nothing changed.", ExitCodes.Failure, LifecycleUnavailableKind, "The AMS release that serves this environment does not support the command yet; ask the AMS team when it will.");

    /// <summary>Returns the refusal of a behavior AMS does not support yet: any 501, or a 404 without an AMS error code on a route that only newer AMS versions serve.</summary>
    internal static AmsRefusal? Unavailable(int status, string? code, bool isNewRoute) => status == (int)HttpStatusCode.NotImplemented || (isNewRoute && status == (int)HttpStatusCode.NotFound && string.IsNullOrWhiteSpace(code)) ? LifecycleUnavailable : null;

    /// <summary>Returns the refusal of an AMS error code that the agent and deployment routes share, naming the record a conflict is about, or null.</summary>
    internal static AmsRefusal? Shared(int status, string? code, string? serviceMessage, string subject) => status switch
    {
        (int)HttpStatusCode.NotFound when Is(code, "AgentNotFound") => AgentNotFound,
        (int)HttpStatusCode.NotFound when Is(code, "DeploymentNotFound") => DeploymentNotFound,
        (int)HttpStatusCode.Conflict when Is(code, "AgentConcurrencyConflict") => ($"The {subject} changed since msagent read it, so AMS changed nothing.", ExitCodes.Failure, $"{subject}-changed", "Run the command again; it reads the current record first."),
        (int)HttpStatusCode.Conflict when Is(code, "AgentDeleting") => ("The agent is being deleted, so AMS refuses new deployments and deploys of it.", ExitCodes.Failure, "agent-deleting", "Finish the deletion with `msagent agent delete`, or use another agent."),
        (int)HttpStatusCode.Conflict when Is(code, "AgentNameInUse") => ($"AMS refused the agent name: {Echo(serviceMessage, "another agent of the environment already uses it.")}", ExitCodes.Usage, "agent-name-in-use", "Choose another name, or use the agent that has it."),
        (int)HttpStatusCode.Conflict when Is(code, "DeploymentNameInUse") => ($"AMS refused the deployment name: {Echo(serviceMessage, "another deployment of the agent already uses it.")}", ExitCodes.Usage, "deployment-name-in-use", "Choose another deployment name. `msagent deployment list` shows the agent's deployments."),
        (int)HttpStatusCode.BadRequest when Is(code, "InvalidAgentFieldValue") => ($"AMS refused a value of the request: {Echo(serviceMessage, "a field is not valid.")}", ExitCodes.Usage, "invalid-field-value", "Correct the option the message names, then run the command again."),
        _ => null,
    };

    /// <summary>Reports whether an AMS error code is the expected one, ignoring letter case.</summary>
    internal static bool Is(string? code, string expected) => string.Equals(code, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns AMS's own message made safe for a terminal, or the fallback when AMS sent none.</summary>
    internal static string Echo(string? serviceMessage, string fallback) => string.IsNullOrWhiteSpace(serviceMessage) ? fallback : TerminalText.Sanitize(serviceMessage.Trim());
}
