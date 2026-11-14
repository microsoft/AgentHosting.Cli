// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>The checks every verb that registers a standalone workflow shares.</summary>
public static class WorkflowRegistrationGuard
{
    /// <summary>Refuses a workflow name Dataverse would refuse, before anything is read or written.</summary>
    /// <param name="scaffold">Applies the sync library's workflow name rule.</param>
    /// <param name="name">The workflow's name.</param>
    /// <returns>The name, unchanged.</returns>
    public static string RequireValidName(IWorkflowScaffoldPort scaffold, string name)
    {
        ArgumentNullException.ThrowIfNull(scaffold);

        return scaffold.ValidateName(name) is { } reason ? throw ManagedAgentException.WorkflowNameInvalid(reason) : name;
    }

    /// <summary>Reads the workflow id <c>--mcs-clone-workflow-id</c> names, refusing a value that is not one.</summary>
    /// <param name="cloneWorkflowId">The option's value.</param>
    /// <returns>The workflow id.</returns>
    public static Guid ParseCloneWorkflowId(string cloneWorkflowId)
    {
        ArgumentNullException.ThrowIfNull(cloneWorkflowId);

        return Guid.TryParse(cloneWorkflowId.Trim(), out var workflowId) && workflowId != Guid.Empty ? workflowId : throw new UsageException($"--mcs-clone-workflow-id '{cloneWorkflowId}' is not a workflow id.");
    }

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
