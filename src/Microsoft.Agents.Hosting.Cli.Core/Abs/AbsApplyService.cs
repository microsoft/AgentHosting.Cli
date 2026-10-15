using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// The confirmed Azure Bot Service <c>apply</c> core — a faithful C# reimplementation of the shipped
/// TypeScript <c>runAbsProvision</c> in <c>apply</c> mode, scoped to the supplied-
/// <c>--msa-app-id</c> zero-Graph path.
///
/// Apply reuses the read-only <see cref="AbsPlanPipeline"/> (desired-state validation, review/preflight
/// plan) and layers mutation and confirmation on top:
///
///   - <strong>apply-as-preview</strong>: without <c>--force</c> it returns the same <c>planned</c>
///     report as preview and never resolves or calls <see cref="IAbsMutationPort"/>. The command is
///     non-interactive by contract, so there is no TTY confirmation prompt.
///   - <strong>confirmed apply</strong> (<c>--force</c>): it <em>refuses</em> a blocking conflict or a
///     failed preflight with a typed <see cref="ProvisioningException"/> (never bypassing it); an
///     all-no-op plan returns <c>unchanged</c> and performs no PUT; a create/update performs exactly
///     one idempotent ARM upsert through <see cref="IAbsMutationPort"/> and persists the shared-format,
///     non-secret connection metadata.
///
/// The core never rebinds an existing bot to a different app id automatically — that is planned as a
/// blocking <c>conflict</c> by <see cref="AbsReviewPlanner"/> and refused here.
/// </summary>
public sealed class AbsApplyService
{
    private readonly IAbsStateReader _reader;
    private readonly IAbsMutationPort _mutation;
    private readonly IConnectionMetadataStore? _metadataStore;

    public AbsApplyService(
        IAbsStateReader reader,
        IAbsMutationPort mutation,
        IConnectionMetadataStore? metadataStore = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _metadataStore = metadataStore;
    }

    /// <summary>
    /// Runs the read-only plan and, when <see cref="AbsProvisioningRequest.Force"/> is set, applies it;
    /// otherwise returns the <c>planned</c> report without touching the mutation port.
    /// </summary>
    public async Task<AbsProvisioningReport> ApplyAsync(
        AbsProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await AbsPlanPipeline
            .PlanAsync(request, _reader, cancellationToken)
            .ConfigureAwait(false);

        // apply-as-preview: without --force, mirror preview exactly and never resolve the mutation port.
        if (!request.Force)
        {
            return AbsReportBuilder.Build(plan.Desired, plan.Review, plan.SuppliedAppId, AbsStatus.Planned);
        }

        // Confirmed apply refuses any blocking conflict or failed preflight before mutating anything.
        var blocking = AbsApplyGuards.BlockingErrorFromReview(plan.Review);
        if (blocking is not null)
        {
            throw blocking;
        }

        var botChange = plan.Review.Changes.First(c => c.Resource == "botService");
        var willChange = botChange.Action is "create" or "update";
        if (!willChange)
        {
            // An all-no-op plan is a confirmed no-op: no PUT, status unchanged.
            return AbsReportBuilder.Build(plan.Desired, plan.Review, plan.SuppliedAppId, AbsStatus.Unchanged);
        }

        await MutateAsync(plan, cancellationToken).ConfigureAwait(false);

        var status = botChange.Action == "create" ? AbsStatus.Created : AbsStatus.Updated;
        return AbsReportBuilder.Build(plan.Desired, plan.Review, plan.SuppliedAppId, status);
    }

    /// <summary>Performs the single idempotent ARM upsert and persists non-secret connection metadata.</summary>
    private async Task MutateAsync(AbsPlan plan, CancellationToken cancellationToken)
    {
        var desired = plan.Desired;

        await _mutation.UpsertBotAsync(
            new AbsBotUpsert
            {
                SubscriptionId = desired.AzureContext.SubscriptionId,
                ResourceGroup = desired.AzureContext.ResourceGroup,
                Name = desired.BotName,
                Endpoint = desired.Endpoint,
                MsaAppId = plan.SuppliedAppId,
                TenantId = desired.AzureContext.TenantId,
                DisplayName = desired.DisplayName,
                Sku = desired.Sku,
                Location = desired.AzureContext.Location,
            },
            cancellationToken).ConfigureAwait(false);

        if (_metadataStore is null)
        {
            return;
        }

        var reference = new ConnectionMetadataRef
        {
            SubscriptionId = desired.AzureContext.SubscriptionId,
            ResourceGroup = desired.AzureContext.ResourceGroup,
            BotName = desired.BotName,
        };
        var existing = await _metadataStore.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow.ToString("o");

        await _metadataStore.WriteAsync(
            new ConnectionMetadata
            {
                SchemaVersion = 1,
                SubscriptionId = desired.AzureContext.SubscriptionId,
                ResourceGroup = desired.AzureContext.ResourceGroup,
                BotName = desired.BotName,
                BotResourceId = desired.ArmCorrelation.BotResourceId,
                AppId = plan.SuppliedAppId,
                AppObjectId = null,
                MsaAppType = desired.MsaAppType,
                IdentityMode = desired.IdentityMode,
                Endpoint = desired.Endpoint,
                CreatedAt = existing?.CreatedAt ?? timestamp,
                UpdatedAt = timestamp,
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Maps a blocking review to the most actionable typed <see cref="ProvisioningException"/> an
/// <c>apply</c> refuses with. A faithful C# port of the shipped TypeScript
/// <c>blockingErrorFromReview</c>: it prefers a naming/identity conflict, then a failed preflight check
/// (provider registration → scope permission → generic), returning <see langword="null"/> when the
/// review carries no blocking issue.
/// </summary>
internal static class AbsApplyGuards
{
    private const string ConflictRemediation =
        "Choose a different bot name, or supply an explicit app object id (--app-object-id) to " +
        "disambiguate, then re-run preview to confirm the plan before applying.";

    public static ProvisioningException? BlockingErrorFromReview(AbsReviewResult review)
    {
        if (!review.HasBlockingIssue)
        {
            return null;
        }

        var conflict = review.Changes.FirstOrDefault(c => c.Action == "conflict");
        if (conflict is not null)
        {
            return new ProvisioningException(
                ProvisioningErrorKind.NamingConflict,
                $"Cannot apply: {DescribeConflict(conflict)}",
                ConflictRemediation);
        }

        var failed = review.Preflight.FirstOrDefault(p => p.Status == "fail");
        if (failed is not null)
        {
            return PreflightError(failed);
        }

        // Blocking flag set with no conflict/fail located — surface a generic, still-actionable error.
        return new ProvisioningException(
            ProvisioningErrorKind.PermissionDenied,
            "Cannot apply: the plan has a blocking issue.",
            "Run the preview command and resolve the reported changes/preflight issues, then retry.");
    }

    private static string DescribeConflict(AbsPlannedChange conflict) =>
        $"{conflict.Resource} at {conflict.Target} conflicts with the request. {conflict.Detail}";

    private static ProvisioningException PreflightError(AbsPreflightCheck check) => check.Id switch
    {
        "providerRegistration" => new ProvisioningException(
            ProvisioningErrorKind.ProviderNotRegistered,
            $"Cannot apply: {check.Detail}",
            "Register the Microsoft.BotService resource provider on the subscription " +
            "(`az provider register --namespace Microsoft.BotService`, or via the portal), then retry."),
        "subscriptionAccess" or "resourceGroupAccess" => new ProvisioningException(
            ProvisioningErrorKind.PermissionDenied,
            $"Cannot apply: {check.Detail}",
            "Grant the operator at least Reader on the scope for preflight and Contributor to apply, then retry."),
        _ => new ProvisioningException(
            ProvisioningErrorKind.PermissionDenied,
            $"Cannot apply: preflight check '{check.Id}' failed. {check.Detail}",
            "Resolve the failed preflight check reported above, then retry."),
    };
}
