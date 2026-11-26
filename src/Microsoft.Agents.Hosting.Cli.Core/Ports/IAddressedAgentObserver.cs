using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Observes the type of an agent addressed by normal product work.</summary>
public interface IAddressedAgentObserver
{
    /// <summary>Whether the current operation accepts agent-type observations.</summary>
    bool IsObservationEnabled { get; }

    /// <summary>Observes a canonical agent type without its name or identifier.</summary>
    /// <param name="agentType">The type of the resolved agent.</param>
    void Observe(string agentType);

    /// <summary>Observes a resolved agent type and its available MCS schema name.</summary>
    /// <param name="agentType">The type of the resolved agent.</param>
    /// <param name="mcsAgentSchemaName">The schema name obtained by normal target resolution.</param>
    void Observe(string agentType, string? mcsAgentSchemaName) => Observe(agentType);

    /// <summary>Observes cloud identifiers established by normal addressed-target resolution.</summary>
    /// <param name="agentType">The canonical addressed agent type.</param>
    /// <param name="mcsAgentId">The resolved cloud bot target, when applicable.</param>
    /// <param name="mcsWorkflowId">The addressed standalone workflow, when available.</param>
    /// <param name="environmentId">The normally resolved target environment, when available.</param>
    void ObserveResources(string agentType, string? mcsAgentId, string? mcsWorkflowId, string? environmentId)
    {
    }

    /// <summary>Observes deployment milestones without changing the product operation.</summary>
    /// <param name="observation">The partial facts established by normal deployment work.</param>
    void ObserveDeployment(DeploymentTelemetryObservation observation)
    {
    }
}

internal static class AddressedAgentObservation
{
    internal static void ObserveDeployment(IAddressedAgentObserver? observer, DeploymentTelemetryObservation observation)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            if (observer.IsObservationEnabled)
            {
                observer.ObserveDeployment(observation);
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(OptionalMeasurementFailureCategory.TelemetryAgentObservation);
        }
    }

    internal static void ObserveResources(
        IAddressedAgentObserver? observer,
        string agentType,
        string? mcsAgentId = null,
        string? mcsWorkflowId = null,
        string? environmentId = null)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            if (observer.IsObservationEnabled)
            {
                observer.ObserveResources(agentType, mcsAgentId, mcsWorkflowId, environmentId);
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(OptionalMeasurementFailureCategory.TelemetryAgentObservation);
        }
    }

    internal static void Observe(
        IAddressedAgentObserver? observer,
        string agentType,
        string? mcsAgentSchemaName = null)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            if (observer.IsObservationEnabled)
            {
                observer.Observe(agentType, mcsAgentSchemaName);
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(OptionalMeasurementFailureCategory.TelemetryAgentObservation);
        }
    }
}
