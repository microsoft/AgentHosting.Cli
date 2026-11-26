namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Partial deployment facts established by normal product work.</summary>
public sealed record DeploymentTelemetryObservation
{
    /// <summary>The resolved deployment slot's canonical stage, when available.</summary>
    public string? DeploymentType { get; init; }

    /// <summary>Whether the caller explicitly requested publication.</summary>
    public bool? PublishRequested { get; init; }

    /// <summary>The confirmed resource action, independent of later failures.</summary>
    public DeploymentResourceAction? ResourceAction { get; init; }

    /// <summary>The publication operation's latest known outcome.</summary>
    public DeploymentPublishStatus? PublishStatus { get; init; }

    /// <summary>The standalone workflow's latest observed state.</summary>
    public DeploymentWorkflowState? WorkflowState { get; init; }
}

/// <summary>A resource action confirmed during deployment.</summary>
public enum DeploymentResourceAction
{
    /// <summary>A new resource was created.</summary>
    Created,
    /// <summary>An existing resource was updated.</summary>
    Updated,
    /// <summary>An existing workflow was replaced.</summary>
    Replaced,
    /// <summary>An existing agent's workspaces were pushed without local changes.</summary>
    UpdatedNoChange,
}

/// <summary>What is known about this deployment's publication operation.</summary>
public enum DeploymentPublishStatus
{
    /// <summary>No publication or activation was requested.</summary>
    NotRequested,
    /// <summary>Publication was requested but execution has not reached it.</summary>
    NotReached,
    /// <summary>Publication or activation was confirmed.</summary>
    Succeeded,
    /// <summary>Known prerequisites withheld publication or activation.</summary>
    Blocked,
    /// <summary>The publication or activation operation reported a failure.</summary>
    Failed,
    /// <summary>Publication or activation was attempted without a confirmed outcome.</summary>
    Unknown,
}

/// <summary>The portal-facing state of an addressed standalone workflow.</summary>
public enum DeploymentWorkflowState
{
    /// <summary>The workflow was observed as a draft.</summary>
    Draft,
    /// <summary>The workflow was observed as published.</summary>
    Published,
    /// <summary>The workflow's state could not be established.</summary>
    Unknown,
}
