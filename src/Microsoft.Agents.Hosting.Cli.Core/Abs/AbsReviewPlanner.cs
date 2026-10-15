using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// Produces a review-before-apply plan for the scoped supplied-<c>--msa-app-id</c> zero-Graph path,
/// consuming only the read-only <see cref="IAbsStateReader"/> seam, so it is structurally impossible
/// for review or preview to create or update anything. It is a faithful C# reimplementation of the
/// shipped TypeScript <c>planAbsProvisioning</c> composed with its
/// <c>asZeroGraphReview</c> transform: the Entra application and service
/// principal changes are always operator-supplied no-ops, the <c>existingAppConflict</c> preflight is
/// neutralized, and the Graph correlation is a display-name filter — no Graph read is issued (which is
/// why the C# runtime carries no Graph port at all). The status is always <c>planned</c>; apply-time
/// mutation and conflict refusal are handled by <see cref="AbsApplyService"/>.
/// </summary>
internal static class AbsReviewPlanner
{
    private const string BotServiceProvider = "Microsoft.BotService";

    private const string ZeroGraphChangeDetail =
        "Operator supplied an explicit app id (msaAppId); zero-Graph — no Entra application or " +
        "service principal is created or modified.";

    public static async Task<AbsReviewResult> PlanZeroGraphAsync(
        AbsDesiredState desired,
        string suppliedAppId,
        IAbsStateReader reader,
        CancellationToken cancellationToken)
    {
        var subscriptionId = desired.AzureContext.SubscriptionId;
        var resourceGroup = desired.AzureContext.ResourceGroup;
        var tenantId = desired.AzureContext.TenantId;

        var preflight = new List<AbsPreflightCheck>();
        var changes = new List<AbsPlannedChange>();

        preflight.Add(ProviderCheck(
            await reader.GetProviderRegistrationAsync(subscriptionId, BotServiceProvider, tenantId, cancellationToken)
                .ConfigureAwait(false)));
        preflight.Add(SubscriptionCheck(
            await reader.GetSubscriptionAccessAsync(subscriptionId, tenantId, cancellationToken).ConfigureAwait(false),
            subscriptionId));
        preflight.Add(ResourceGroupCheck(
            await reader.GetResourceGroupAccessAsync(subscriptionId, resourceGroup, tenantId, cancellationToken)
                .ConfigureAwait(false),
            resourceGroup));

        // The operator-supplied id is authoritative for detecting an existing bot bound to a
        // *different* app id (a destructive rebind refused as a conflict).
        var existingBot = await reader.GetBotAsync(desired.ArmCorrelation.BotResourceId, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var (botChange, botPreflight) = PlanBotService(desired, existingBot, suppliedAppId);

        // Reported change order is bot, then Entra, then service principal (matches the oracle).
        changes.Add(botChange);
        preflight.Add(botPreflight);
        changes.Add(new AbsPlannedChange
        {
            Resource = "entraApplication",
            Action = "noop",
            Target = suppliedAppId,
            Detail = ZeroGraphChangeDetail,
        });
        changes.Add(new AbsPlannedChange
        {
            Resource = "servicePrincipal",
            Action = "noop",
            Target = suppliedAppId,
            Detail = ZeroGraphChangeDetail,
        });
        preflight.Add(new AbsPreflightCheck
        {
            Id = "existingAppConflict",
            Status = "pass",
            Detail = "Operator supplied an explicit app id; Entra app correlation is not used.",
        });

        // The ARM reads above already exercised the only provisioning audience used by this
        // supplied-app path; a missing/wrong token would have thrown before reaching here.
        preflight.Add(new AbsPreflightCheck
        {
            Id = "audienceReachability",
            Status = "pass",
            Detail = "ARM read calls succeeded, proving the provisioning audience is reachable with the " +
                "operator token. The supplied-app path requires no Microsoft Graph call.",
        });

        // Checks that inherently cannot run before mutation.
        preflight.Add(new AbsPreflightCheck
        {
            Id = "writeCapability",
            Status = "unknown",
            Detail = "ARM write capability cannot be confirmed without a mutating probe; it is " +
                "validated at apply time and surfaces as a permission error if missing.",
        });
        preflight.Add(new AbsPreflightCheck
        {
            Id = "skuQuota",
            Status = "unknown",
            Detail = $"No non-mutating quota check exists for SKU {desired.Sku}; if capacity is denied at " +
                "apply, retry with S1.",
        });
        preflight.Add(new AbsPreflightCheck
        {
            Id = "azurePolicy",
            Status = "unknown",
            Detail = "Azure Policy denials have no read-only simulation for a single PUT; they surface as " +
                "a governance error at apply.",
        });

        var hasBlockingIssue = changes.Any(c => c.Action == "conflict") || preflight.Any(p => p.Status == "fail");

        return new AbsReviewResult
        {
            Changes = changes,
            Preflight = preflight,
            Correlation = new AbsCorrelation
            {
                Arm = desired.ArmCorrelation,
                Graph = new AbsGraphCorrelation
                {
                    Mode = "displayNameFilter",
                    DisplayName = desired.GraphCorrelation.DisplayName,
                },
            },
            ResolvedAppId = suppliedAppId,
            HasBlockingIssue = hasBlockingIssue,
        };
    }

    private static (AbsPlannedChange Change, AbsPreflightCheck Preflight) PlanBotService(
        AbsDesiredState desired,
        AbsBotSnapshot? existing,
        string expectedAppId)
    {
        var resourceId = desired.ArmCorrelation.BotResourceId;

        if (existing is null)
        {
            return (
                new AbsPlannedChange
                {
                    Resource = "botService",
                    Action = "create",
                    Target = resourceId,
                    Detail = "No existing bot at this resource id; a new Azure Bot Service resource will be created.",
                },
                new AbsPreflightCheck
                {
                    Id = "existingBotConflict",
                    Status = "pass",
                    Detail = "No existing bot conflicts with the requested resource id.",
                });
        }

        if (!string.IsNullOrEmpty(existing.MsaAppType) && existing.MsaAppType != desired.MsaAppType)
        {
            return (
                new AbsPlannedChange
                {
                    Resource = "botService",
                    Action = "conflict",
                    Target = existing.BotResourceId,
                    Detail =
                        $"Existing bot uses msaAppType \"{existing.MsaAppType}\" but the request derives " +
                        $"\"{desired.MsaAppType}\" from identity mode {desired.IdentityMode}. Changing the " +
                        "identity type is destructive; re-run with a matching identity mode or a different bot name.",
                },
                new AbsPreflightCheck
                {
                    Id = "existingBotConflict",
                    Status = "fail",
                    Detail = $"Existing bot msaAppType \"{existing.MsaAppType}\" conflicts with the requested " +
                        $"\"{desired.MsaAppType}\".",
                });
        }

        if (!string.IsNullOrEmpty(expectedAppId) && !string.IsNullOrEmpty(existing.AppId) &&
            existing.AppId != expectedAppId)
        {
            return (
                new AbsPlannedChange
                {
                    Resource = "botService",
                    Action = "conflict",
                    Target = existing.BotResourceId,
                    Detail =
                        $"Existing bot is bound to app id {existing.AppId}, but this request targets app id " +
                        $"{expectedAppId}. Rebinding an existing bot to a different app changes its identity " +
                        "and is destructive, so it is not done automatically; use a different bot name, or " +
                        "repoint the bot deliberately after confirming the change is intended.",
                },
                new AbsPreflightCheck
                {
                    Id = "existingBotConflict",
                    Status = "fail",
                    Detail = $"Existing bot is bound to app id {existing.AppId}, which differs from the " +
                        $"requested app id {expectedAppId}.",
                });
        }

        var diffs = new List<string>();
        if (existing.MessagingEndpoint is null)
        {
            diffs.Add("set the messaging endpoint");
        }
        else if (existing.MessagingEndpoint != desired.Endpoint)
        {
            diffs.Add($"endpoint {existing.MessagingEndpoint} → {desired.Endpoint}");
        }

        if (existing.Sku is not null && existing.Sku != desired.Sku)
        {
            diffs.Add($"SKU {existing.Sku} → {desired.Sku}");
        }

        if (diffs.Count > 0)
        {
            return (
                new AbsPlannedChange
                {
                    Resource = "botService",
                    Action = "update",
                    Target = existing.BotResourceId,
                    Detail = $"Existing bot will be updated in place (idempotent ARM upsert): {string.Join("; ", diffs)}.",
                },
                new AbsPreflightCheck
                {
                    Id = "existingBotConflict",
                    Status = "pass",
                    Detail = "Existing bot resolved by resource id; it will be updated in place, not duplicated.",
                });
        }

        return (
            new AbsPlannedChange
            {
                Resource = "botService",
                Action = "noop",
                Target = existing.BotResourceId,
                Detail = "Existing bot already matches the desired endpoint and SKU; no ARM change is needed.",
            },
            new AbsPreflightCheck
            {
                Id = "existingBotConflict",
                Status = "pass",
                Detail = "Existing bot already matches the desired state.",
            });
    }

    private static AbsPreflightCheck ProviderCheck(AbsProviderRegistration registration) =>
        registration.RegistrationState switch
        {
            "Registered" => new AbsPreflightCheck
            {
                Id = "providerRegistration",
                Status = "pass",
                Detail = "The Microsoft.BotService resource provider is registered on the subscription.",
            },
            "NotRegistered" => new AbsPreflightCheck
            {
                Id = "providerRegistration",
                Status = "fail",
                Detail = "The Microsoft.BotService resource provider is not registered on the subscription. " +
                    "Register it before applying.",
            },
            "Registering" => new AbsPreflightCheck
            {
                Id = "providerRegistration",
                Status = "warn",
                Detail = "The Microsoft.BotService resource provider registration is in progress; wait for it " +
                    "to complete before applying.",
            },
            _ => new AbsPreflightCheck
            {
                Id = "providerRegistration",
                Status = "unknown",
                Detail = "The Microsoft.BotService provider registration state could not be determined.",
            },
        };

    private static AbsPreflightCheck SubscriptionCheck(AbsScopeAccess access, string subscriptionId)
    {
        if (!access.Exists)
        {
            return new AbsPreflightCheck
            {
                Id = "subscriptionAccess",
                Status = "fail",
                Detail = $"Subscription {subscriptionId} was not found or is not visible to the signed-in operator.",
            };
        }

        if (!access.HasReadAccess)
        {
            return new AbsPreflightCheck
            {
                Id = "subscriptionAccess",
                Status = "fail",
                Detail = $"The signed-in operator cannot read subscription {subscriptionId}; grant at least the Reader role.",
            };
        }

        return new AbsPreflightCheck
        {
            Id = "subscriptionAccess",
            Status = "pass",
            Detail = $"Subscription {subscriptionId} exists and is readable by the operator.",
        };
    }

    private static AbsPreflightCheck ResourceGroupCheck(AbsScopeAccess access, string resourceGroup)
    {
        if (!access.Exists)
        {
            return new AbsPreflightCheck
            {
                Id = "resourceGroupAccess",
                Status = "warn",
                Detail = $"Resource group {resourceGroup} does not exist; it must be pre-created or created at " +
                    "apply time (needs subscription-level create rights).",
            };
        }

        if (!access.HasReadAccess)
        {
            return new AbsPreflightCheck
            {
                Id = "resourceGroupAccess",
                Status = "fail",
                Detail = $"Resource group {resourceGroup} exists but is not readable by the operator; grant " +
                    "Reader (preflight) / Contributor (apply).",
            };
        }

        return new AbsPreflightCheck
        {
            Id = "resourceGroupAccess",
            Status = "pass",
            Detail = $"Resource group {resourceGroup} exists and is readable by the operator.",
        };
    }
}
