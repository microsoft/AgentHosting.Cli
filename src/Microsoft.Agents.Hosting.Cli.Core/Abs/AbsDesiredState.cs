using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// The validated, fully-derived scaffolding target — the single source the review/preflight planner
/// and the output contract consume, produced entirely before any Azure read or mutation. Mirrors the
/// shipped TypeScript <c>AbsDesiredState</c> for the scoped supplied-<c>--msa-app-id</c> zero-Graph
/// path. Reuses the report's public correlation/context records to keep one shape for both.
/// </summary>
internal sealed record AbsDesiredState
{
    public required AbsAzureContext AzureContext { get; init; }
    public required string BotName { get; init; }
    public required string DisplayName { get; init; }
    public required string Endpoint { get; init; }
    public required string Sku { get; init; }
    public required string MsaAppType { get; init; }
    public required string IdentityMode { get; init; }
    public string? DeploymentId { get; init; }
    public required AbsArmCorrelation ArmCorrelation { get; init; }
    public required AbsGraphCorrelation GraphCorrelation { get; init; }
}

/// <summary>
/// The result of a read-only review-before-apply pass. <c>Status</c> is always <c>planned</c>;
/// apply-time statuses live on <see cref="AbsProvisioningReport"/>. <see cref="HasBlockingIssue"/> is
/// true when any change is a conflict or any preflight failed, so a caller must not proceed to apply
/// unforced. Produced without mutating Azure.
/// </summary>
internal sealed record AbsReviewResult
{
    public required IReadOnlyList<AbsPlannedChange> Changes { get; init; }
    public required IReadOnlyList<AbsPreflightCheck> Preflight { get; init; }
    public required AbsCorrelation Correlation { get; init; }
    public string? ResolvedAppId { get; init; }
    public required bool HasBlockingIssue { get; init; }
}
