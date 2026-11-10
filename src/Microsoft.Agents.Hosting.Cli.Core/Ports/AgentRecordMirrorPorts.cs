using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>A change to an agent's .config records that AMS holds a copy of.</summary>
/// <param name="Agent">The agent the change belongs to.</param>
public abstract record AgentRecordChange(ManagedAgentRecord Agent);

/// <summary>A deployment was created.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Deployment">The new deployment.</param>
public sealed record DeploymentCreated(ManagedAgentRecord Agent, AgentDeploymentRecord Deployment) : AgentRecordChange(Agent);

/// <summary>A deployment was renamed.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Deployment">The deployment, with its name before the rename.</param>
/// <param name="NewName">The deployment's new name.</param>
public sealed record DeploymentRenamed(ManagedAgentRecord Agent, AgentDeploymentRecord Deployment, string NewName) : AgentRecordChange(Agent);

/// <summary>A deployment was deleted.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Deployment">The deleted deployment.</param>
public sealed record DeploymentDeleted(ManagedAgentRecord Agent, AgentDeploymentRecord Deployment) : AgentRecordChange(Agent);

/// <summary>An agent was deleted with its deployments.</summary>
/// <param name="Agent">The deleted agent.</param>
public sealed record AgentDeleted(ManagedAgentRecord Agent) : AgentRecordChange(Agent);

/// <summary>What a .config write returned, and the warning mirroring it to AMS left.</summary>
/// <typeparam name="T">What the .config write returns.</typeparam>
/// <param name="Value">What the .config write returned.</param>
/// <param name="Warning">Why AMS wasn't changed, or null.</param>
/// <param name="Mirrored">Whether AMS's copy holds the change.</param>
public sealed record AgentRecordMirrored<T>(T Value, string? Warning, bool Mirrored = false);

/// <summary>Applies a .config change to AMS's copy of the records first, then writes .config, undoing the AMS change when the write fails.</summary>
public interface IAgentRecordMirror
{
    /// <summary>Applies a change to AMS, then writes .config.</summary>
    /// <typeparam name="T">What the .config write returns.</typeparam>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="change">The change.</param>
    /// <param name="writeConfig">Writes the change to .config.</param>
    /// <param name="cancellationToken">Cancels the AMS calls.</param>
    Task<AgentRecordMirrored<T>> ApplyAsync<T>(string projectDirectory, AgentRecordChange change, Func<T> writeConfig, CancellationToken cancellationToken);
}

/// <summary>The mirror that only writes .config, used when AMS mode isn't available.</summary>
public sealed class NoAgentRecordMirror : IAgentRecordMirror
{
    private NoAgentRecordMirror()
    {
    }

    /// <summary>The shared instance.</summary>
    public static NoAgentRecordMirror Instance { get; } = new();

    /// <inheritdoc />
    public Task<AgentRecordMirrored<T>> ApplyAsync<T>(string projectDirectory, AgentRecordChange change, Func<T> writeConfig, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeConfig);

        return Task.FromResult(new AgentRecordMirrored<T>(writeConfig(), null));
    }
}
