using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>The stable wire values for <see cref="AbsProvisioningReport.Status"/>.</summary>
internal static class AbsStatus
{
    /// <summary>A read-only preview / apply-without-force plan.</summary>
    public const string Planned = "planned";

    /// <summary>A confirmed apply created the bot.</summary>
    public const string Created = "created";

    /// <summary>A confirmed apply updated the bot in place.</summary>
    public const string Updated = "updated";

    /// <summary>A confirmed apply found the bot already matched the desired state.</summary>
    public const string Unchanged = "unchanged";
}

/// <summary>
/// Builds the versioned, machine-readable <see cref="AbsProvisioningReport"/> from the desired state
/// and the review result. A faithful C# port of the shipped TypeScript <c>buildAbsProvisioningReport</c>
/// It is the single shape the ABS command emits under <c>--json</c>, wrapped by the shared success
/// envelope, and the durable handoff payload.
///
/// The report is no-secret by construction: <see cref="AbsIdentityInfo.SecretMaterialReturned"/> is
/// the literal <see langword="false"/>, <see cref="AbsIdentityInfo.SecretReference"/> carries only a
/// pointer, and the builder runs <see cref="SecretMaterialGuard"/> over the whole payload before
/// returning it, so a leak fails loudly here instead of reaching stdout.
/// </summary>
internal static class AbsReportBuilder
{
    private const string ReportContext = "ABS provisioning report";

    /// <summary>
    /// Builds a report for the given <paramref name="status"/> (<c>planned</c> for preview /
    /// apply-without-force; <c>created</c>/<c>updated</c>/<c>unchanged</c> for a confirmed apply) and
    /// asserts it is secret-free.
    /// </summary>
    public static AbsProvisioningReport Build(
        AbsDesiredState desired,
        AbsReviewResult review,
        string? appId,
        string status)
    {
        var report = new AbsProvisioningReport
        {
            SchemaVersion = 1,
            Status = status,
            AzureContext = desired.AzureContext,
            Abs = new AbsDetails
            {
                BotName = desired.BotName,
                BotResourceId = desired.ArmCorrelation.BotResourceId,
                AppId = appId ?? review.ResolvedAppId,
                TenantId = desired.AzureContext.TenantId,
                MessagingEndpoint = desired.Endpoint,
                Sku = desired.Sku,
                MsaAppType = desired.MsaAppType,
            },
            Correlation = review.Correlation,
            Identity = new AbsIdentityInfo
            {
                Mode = desired.IdentityMode,
                SecretMaterialReturned = false,
                SecretReference = null,
            },
            Changes = review.Changes,
            Preflight = review.Preflight,
            NextActions = DeriveNextActions(status, review),
        };

        SecretMaterialGuard.AssertNoSecretMaterial(report, ReportContext);
        return report;
    }

    /// <summary>Derives concise, secret-free next-step guidance from the status and review.</summary>
    private static List<string> DeriveNextActions(string status, AbsReviewResult review)
    {
        var actions = new List<string>();
        var conflicts = review.Changes.Count(c => c.Action == "conflict");
        var failedChecks = review.Preflight.Count(p => p.Status == "fail");

        if (conflicts > 0)
        {
            actions.Add(
                "Resolve the reported conflict(s) before applying: choose a different bot name or supply " +
                "an explicit app object id to disambiguate.");
        }

        if (failedChecks > 0)
        {
            actions.Add(
                "Address the failed preflight checks listed under `preflight` (e.g. register the " +
                "Microsoft.BotService provider, or grant the missing Azure/Graph permission) before applying.");
        }

        if (conflicts == 0 && failedChecks == 0)
        {
            if (status is AbsStatus.Created or AbsStatus.Updated)
            {
                // The apply already ran: confirm the outcome instead of pointing back at --force
                // (the review still lists the create/update actions that were just applied).
                actions.Add(status == AbsStatus.Created
                    ? "The bot was created; the connection now matches the desired state."
                    : "The bot was updated; the connection now matches the desired state.");
            }
            else
            {
                var willChange = review.Changes.Any(c => c.Action == "create" || c.Action == "update");
                actions.Add(willChange
                    ? "Re-run the command with --force (or confirm the prompt) to apply the planned create/update operations."
                    : "No changes are required; the connection already matches the desired state.");
            }
        }

        return actions;
    }
}
