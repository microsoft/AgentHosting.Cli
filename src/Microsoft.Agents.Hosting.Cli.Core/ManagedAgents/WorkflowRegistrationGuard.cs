// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Keeps a project to one record per workflow.</summary>
public static class WorkflowRegistrationGuard
{
    /// <summary>Fails when a project already tracks the workflow.</summary>
    /// <param name="agents">The records the project holds.</param>
    /// <param name="locator">Names the config file a clash is reported against.</param>
    /// <param name="projectDirectory">The project to check.</param>
    /// <param name="workflowId">The workflow being registered.</param>
    public static void RequireUnregistered(IManagedAgentRegistry agents, AgentConfigLocator locator, string projectDirectory, Guid workflowId)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(locator);

        if (agents.List(projectDirectory).FirstOrDefault(agent => string.Equals(agent.McsWorkflowId, workflowId.ToString(), StringComparison.OrdinalIgnoreCase)) is { } already)
        {
            throw ManagedAgentException.AlreadyRegistered(already.DisplayName, already.AgentId, locator.ConfigFilePath(projectDirectory));
        }
    }
}
