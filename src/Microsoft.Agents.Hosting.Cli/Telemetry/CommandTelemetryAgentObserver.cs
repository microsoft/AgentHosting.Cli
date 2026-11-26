using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal sealed class CommandTelemetryAgentObserver(CommandTelemetrySessionAccessor accessor) :
    IAddressedAgentObserver
{
    public bool IsObservationEnabled =>
        accessor.ObservationSession?.IsAgentTypeObservationEnabled == true;

    public void Observe(string agentType) =>
        accessor.ObservationSession?.ObserveAgentType(agentType);

    public void Observe(string agentType, string? mcsAgentSchemaName) =>
        accessor.ObservationSession?.ObserveAgentType(agentType, mcsAgentSchemaName);

    public void ObserveResources(string agentType, string? mcsAgentId, string? mcsWorkflowId, string? environmentId) =>
        accessor.ObservationSession?.ObserveResources(agentType, mcsAgentId, mcsWorkflowId, environmentId);

    public void ObserveDeployment(DeploymentTelemetryObservation observation) =>
        accessor.ObservationSession?.ObserveDeployment(observation);
}
