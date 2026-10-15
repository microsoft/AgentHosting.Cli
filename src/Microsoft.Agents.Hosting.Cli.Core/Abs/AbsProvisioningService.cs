using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// The read-only Azure Bot Service preview core — the naming-independent command that plans the ABS
/// scaffolding without ever mutating Azure. A faithful C# reimplementation of the shipped TypeScript
/// <c>runAbsProvision</c> in <c>preview</c> mode, scoped to the supplied-
/// <c>--msa-app-id</c> zero-Graph path.
///
/// It is <strong>structurally incapable of mutating Azure</strong>: the service depends only on the
/// read-only <see cref="IAbsStateReader"/> and holds no reference to
/// <see cref="IAbsMutationPort"/>, so no code path can create or update a resource. The apply verb and
/// its mutation seam live in the separate <see cref="AbsApplyService"/>.
///
/// The read-only pipeline (missing-context guard, desired-state validation, review/preflight plan) is
/// shared with apply via <see cref="AbsPlanPipeline"/>; preview simply builds the <c>planned</c> report
/// from the result, which asserts it is secret-free (<see cref="SecretMaterialException"/>, exit 1)
/// before returning.
/// </summary>
public sealed class AbsProvisioningService
{
    private readonly IAbsStateReader _reader;

    public AbsProvisioningService(IAbsStateReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>Plans the ABS scaffolding read-only and returns the <c>planned</c> report.</summary>
    public async Task<AbsProvisioningReport> PreviewAsync(
        AbsProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await AbsPlanPipeline
            .PlanAsync(request, _reader, cancellationToken)
            .ConfigureAwait(false);

        return AbsReportBuilder.Build(plan.Desired, plan.Review, plan.SuppliedAppId, AbsStatus.Planned);
    }
}
