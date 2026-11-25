using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Builds the calls and reads the answers of the AMS routes on the Azure Bot Service instance of a code-agent deployment, with the refusals those routes share.</summary>
internal static class AmsBotServiceCall
{
    /// <summary>Builds a call on a deployment's bot whose refusals fall back to the ones every bot route shares.</summary>
    internal static AmsCall Create(HttpMethod method, Uri uri, AmsDeploymentReadTransportRequest deployment, string permission, string phase, HttpContent? content, Func<int, string?, string?, (string Message, int ExitCode, string ErrorKind, string Remediation)?> refusal, bool outcomeMayBeIndeterminate = false) =>
        new() { Method = method, Uri = uri, ServiceTarget = deployment.ServiceTarget, RequiredPermissions = [permission], Phase = phase, Content = content, OutcomeMayBeIndeterminate = outcomeMayBeIndeterminate, Refusal = (status, code, serviceMessage) => refusal(status, code, serviceMessage) ?? SharedRefusal(status, code) };

    /// <summary>Parses the body of a success answer, or throws the invalid-response error with the call's correlation ids and without the body.</summary>
    internal static T Read<T>(AmsResponse response, string phase, string subject, string resource, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw AgentDeployException.ServiceRejected(AgentDeployException.AddCorrelation($"The {phase} returned a success status without {subject}.", response.ClientRequestId, response.ServiceRequestId), ExitCodes.Failure, "invalid-ams-response", $"Do not rely on this answer. Inspect the service correlation before acting on the {resource}.", ex);
        }
    }

    /// <summary>Reports whether an AMS error code is the expected one, ignoring letter case.</summary>
    internal static bool IsCode(string? code, string expected) => string.Equals(code, expected, StringComparison.OrdinalIgnoreCase);

    private static (string Message, int ExitCode, string ErrorKind, string Remediation)? SharedRefusal(int status, string? code) => status switch
    {
        (int)HttpStatusCode.NotFound when IsCode(code, "DeploymentNotFound") => AmsRefusals.DeploymentNotFound,
        (int)HttpStatusCode.Conflict when IsCode(code, "DeploymentBotServiceRequired") => ("The deployment has no Azure Bot Service instance that AMS manages, so it has no channels or OAuth connections to manage.", ExitCodes.Failure, "deployment-has-no-bot", "AMS creates the bot at the first `msagent agent deploy` of a deployment with an App Registration or Blueprint identity. Deploy it first with `msagent agent deploy`, then check its bot with `msagent deployment show`."),
        (int)HttpStatusCode.Conflict when IsCode(code, "AgentBotServiceConflict") => ("The Azure Bot Service instance bound to the deployment does not belong to it, so AMS did not use it.", ExitCodes.Failure, "agent-bot-service-conflict", "Do not retry. Ask the AMS team to investigate the deployment's bot with the correlation identifiers."),
        _ => null,
    };
}
