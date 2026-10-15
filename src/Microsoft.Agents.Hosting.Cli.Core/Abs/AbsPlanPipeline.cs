using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// The read-only planning core shared by <see cref="AbsProvisioningService"/> (preview) and
/// <see cref="AbsApplyService"/> (apply). It runs the naming-independent, never-mutating pipeline the
/// TypeScript <c>runAbsProvision</c> performs before it branches on mode:
/// (1) require the never-guessed Azure context (missing-context, exit 2);
/// (2) validate and derive the desired state (<see cref="UsageException"/>, exit 2, on bad format);
/// (3) run the read-only zero-Graph review/preflight plan.
///
/// It touches only the read-only <see cref="IAbsStateReader"/> — it holds no reference to
/// <see cref="IAbsMutationPort"/>, so the plan itself can never create or update a resource. The reader
/// acquires its own ARM tokens scoped to the requested tenant (<c>WithTenantId</c>); a target-tenant
/// silent-acquisition failure surfaces there as an interaction-required auth error (exit 3), so the
/// pipeline needs no separate token step. Apply-time mutation is layered on top of the returned
/// <see cref="AbsPlan"/> by <see cref="AbsApplyService"/>.
/// </summary>
internal static class AbsPlanPipeline
{
    /// <summary>Runs the read-only pipeline and returns the desired state, review, and supplied app id.</summary>
    public static async Task<AbsPlan> PlanAsync(
        AbsProvisioningRequest request,
        IAbsStateReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireProvisionArgs(request);

        var desired = AbsDesiredStateBuilder.Build(request);
        var suppliedAppId = request.MsaAppId!.Trim();

        var review = await AbsReviewPlanner
            .PlanZeroGraphAsync(desired, suppliedAppId, reader, cancellationToken)
            .ConfigureAwait(false);

        return new AbsPlan(desired, review, suppliedAppId);
    }

    /// <summary>
    /// Validates presence of the required, never-guessed Azure context, including
    /// <c>--msa-app-id</c> for the scoped C# zero-Graph path. A typed <c>missing-context</c> error
    /// (exit 2) names every absent flag. Deeper format validation is deferred to
    /// <see cref="AbsDesiredStateBuilder"/>.
    /// </summary>
    private static void RequireProvisionArgs(AbsProvisioningRequest request)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Tenant))
        {
            missing.Add("--tenant");
        }

        if (string.IsNullOrWhiteSpace(request.Subscription))
        {
            missing.Add("--subscription");
        }

        if (string.IsNullOrWhiteSpace(request.ResourceGroup))
        {
            missing.Add("--resource-group");
        }

        if (string.IsNullOrWhiteSpace(request.Location))
        {
            missing.Add("--location");
        }

        if (string.IsNullOrWhiteSpace(request.BotName))
        {
            missing.Add("--bot-name");
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint))
        {
            missing.Add("--endpoint");
        }

        if (string.IsNullOrWhiteSpace(request.MsaAppId))
        {
            missing.Add("--msa-app-id");
        }

        if (missing.Count > 0)
        {
            var joined = string.Join(", ", missing);
            throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                $"Missing required argument(s): {joined}. ABS scaffolding never guesses or silently switches Azure context.",
                $"Provide {joined} explicitly, then re-run.");
        }
    }
}

/// <summary>The read-only planning result: the derived desired state, the review, and the supplied app id.</summary>
internal sealed record AbsPlan(AbsDesiredState Desired, AbsReviewResult Review, string SuppliedAppId);
