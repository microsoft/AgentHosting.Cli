using System.Text.Json.Serialization;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>
/// Explicit Azure Bot Service scaffolding request. Every Azure-context value is supplied by the
/// operator — nothing is inferred or silently switched. Populated by the command host from the
/// <c>abs</c> flags and passed to <see cref="IAgentHostingOperations.PreviewAzureBotAsync"/> /
/// <see cref="IAgentHostingOperations.ApplyAzureBotAsync"/>. The desired-state validation, planner,
/// and report shape are implemented behind this stable host-facing input contract.
/// </summary>
public record AbsProvisioningRequest
{
    /// <summary>Target tenant id for the bot identity (from <c>--tenant</c>).</summary>
    public string? Tenant { get; init; }

    /// <summary>Target subscription id (from <c>--subscription</c>).</summary>
    public string? Subscription { get; init; }

    /// <summary>Target resource group (from <c>--resource-group</c>).</summary>
    public string? ResourceGroup { get; init; }

    /// <summary>Azure region for the control-plane resource (from <c>--location</c>).</summary>
    public string? Location { get; init; }

    /// <summary>Azure Bot Service resource name (from <c>--bot-name</c>).</summary>
    public string? BotName { get; init; }

    /// <summary>HTTPS <c>/api/messages</c> messaging endpoint (from <c>--endpoint</c>).</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Application (client) id of an existing Entra app registration (from <c>--msa-app-id</c>).
    /// Connected via ARM only — the scoped zero-Graph path.
    /// </summary>
    public string? MsaAppId { get; init; }

    /// <summary>Friendly display name (from <c>--display-name</c>); defaults to the bot name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Bot SKU (from <c>--sku</c>): <c>F0</c> (default) or <c>S1</c>.</summary>
    public string? Sku { get; init; }

    /// <summary>Bot identity mode (from <c>--identity-mode</c>); only <c>ClientSecret</c> is supported.</summary>
    public string? IdentityMode { get; init; }

    /// <summary>Known Entra app object id used to disambiguate a name (from <c>--app-object-id</c>).</summary>
    public string? AppObjectId { get; init; }

    /// <summary>Links the connection to an AgentHost deployment (from <c>--deployment-id</c>).</summary>
    public string? DeploymentId { get; init; }

    /// <summary>Confirms the mutating apply (from <c>--force</c>); without it, apply only previews.</summary>
    public bool Force { get; init; }

    /// <summary>Forbid any interactive identity UI (from <c>--non-interactive</c>).</summary>
    public bool NonInteractive { get; init; }
}

/// <summary>Plans the Azure Bot Service changes, distinct from the apply request so each binds its own handler.</summary>
public sealed record AbsPreviewRequest : AbsProvisioningRequest;

/// <summary>Applies the Azure Bot Service plan, distinct from the preview request so each binds its own handler.</summary>
public sealed record AbsApplyRequest : AbsProvisioningRequest;

/// <summary>
/// Versioned Azure Bot Service provisioning report returned by preview and apply. Its property
/// declaration order is the exact order the JSON success envelope emits (STJ source generation writes
/// properties in declaration order): <c>schemaVersion, status, azureContext, abs, correlation,
/// identity, changes, preflight, nextActions</c> (and a trailing <c>error</c> only on a failed
/// report). Wire values are modeled as strings so the camelCase JSON matches the shipped TypeScript
/// property names/enum strings carried by the provisional oracle byte-for-byte.
///
/// No-secret by construction: <see cref="AbsIdentityInfo.SecretMaterialReturned"/> is the literal
/// <see langword="false"/>, <see cref="AbsIdentityInfo.SecretReference"/> carries only a pointer, and
/// the builder runs the secret-material guard over the whole payload before returning it.
/// </summary>
public sealed record AbsProvisioningReport : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Plan/apply outcome status (for example <c>planned</c>, <c>created</c>, <c>updated</c>, <c>unchanged</c>, <c>failed</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The explicit, never-inferred Azure context the report targets.</summary>
    public required AbsAzureContext AzureContext { get; init; }

    /// <summary>The derived Azure Bot Service resource facts.</summary>
    public required AbsDetails Abs { get; init; }

    /// <summary>The split ARM/Graph correlation anchors.</summary>
    public required AbsCorrelation Correlation { get; init; }

    /// <summary>The bot identity mode and the no-secret credential envelope.</summary>
    public required AbsIdentityInfo Identity { get; init; }

    /// <summary>The planned create/update/no-op/conflict operations, in report order.</summary>
    public required IReadOnlyList<AbsPlannedChange> Changes { get; init; }

    /// <summary>The read-only preflight checks, in report order.</summary>
    public required IReadOnlyList<AbsPreflightCheck> Preflight { get; init; }

    /// <summary>Concise, secret-free next-step guidance.</summary>
    public required IReadOnlyList<string> NextActions { get; init; }

    /// <summary>Present only when <see cref="Status"/> is <c>failed</c>; secret-free.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AbsProvisioningError? Error { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        var lines = new List<string>
        {
            $"ABS {Status}: bot \"{Abs.BotName}\" ({Abs.MsaAppType}) in " +
                $"{AzureContext.SubscriptionId}/{AzureContext.ResourceGroup}.",
        };

        if (Changes.Count > 0)
        {
            lines.AddRange(TextGrid.RenderTable(
                "Planned changes",
                ["RESOURCE", "ACTION", "DETAIL"],
                [.. Changes.Select(change => new[] { change.Resource, change.Action, change.Detail })]));
        }

        var notable = Preflight.Where(check => check.Status != "pass").ToArray();

        if (notable.Length > 0)
        {
            lines.AddRange(TextGrid.RenderTable(
                "Preflight",
                ["CHECK", "STATUS", "DETAIL"],
                [.. notable.Select(check => new[] { check.Id, check.Status, check.Detail })]));
        }

        lines.AddRange(NextActions.Select(action => $"\u2192 {action}"));

        return lines;
    }
}

/// <summary>Explicit Azure context. Never silently switched; missing/ambiguous values fail the build.</summary>
public sealed record AbsAzureContext
{
    public required string TenantId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string ResourceGroup { get; init; }
    public required string Location { get; init; }
}

/// <summary>The derived Azure Bot Service resource facts surfaced under the report's <c>abs</c> key.</summary>
public sealed record AbsDetails
{
    public required string BotName { get; init; }
    public required string BotResourceId { get; init; }

    /// <summary>The bot's app (client) id — <see langword="null"/> until an app is created/resolved (written as JSON null).</summary>
    public string? AppId { get; init; }

    public required string TenantId { get; init; }
    public required string MessagingEndpoint { get; init; }
    public required string Sku { get; init; }

    /// <summary>ARM <c>msaAppType</c> (for example <c>SingleTenant</c>), derived from the identity mode.</summary>
    public required string MsaAppType { get; init; }
}

/// <summary>The split ARM/Graph correlation anchors.</summary>
public sealed record AbsCorrelation
{
    public required AbsArmCorrelation Arm { get; init; }
    public required AbsGraphCorrelation Graph { get; init; }
}

/// <summary>ARM idempotency anchor: the deterministic bot resource id, computed before any call.</summary>
public sealed record AbsArmCorrelation
{
    /// <summary>Always <c>resourceId</c> for the resolved design.</summary>
    public required string Mode { get; init; }
    public required string BotResourceId { get; init; }
    public required string Name { get; init; }
}

/// <summary>Graph idempotency anchor. On the zero-Graph path this is always a display-name filter.</summary>
public sealed record AbsGraphCorrelation
{
    /// <summary><c>displayNameFilter</c> on the zero-Graph path; <c>objectId</c>/<c>storedMetadata</c> otherwise.</summary>
    public required string Mode { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The Entra application object id once known; omitted from JSON when absent (matches the oracle's <c>undefined</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; init; }
}

/// <summary>The bot identity mode and the no-secret credential envelope.</summary>
public sealed record AbsIdentityInfo
{
    /// <summary>The bot identity mode (for example <c>ClientSecret</c>).</summary>
    public required string Mode { get; init; }

    /// <summary>Always the literal <see langword="false"/> — scaffolding output never returns secret material.</summary>
    public bool SecretMaterialReturned { get; init; }

    /// <summary>A pointer to a credential — never the material. Written as JSON null unless a credential was provisioned.</summary>
    public AbsSecretReference? SecretReference { get; init; }
}

/// <summary>A pointer to a credential — never the material.</summary>
public sealed record AbsSecretReference
{
    public required string Kind { get; init; }
    public required string AppObjectId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeyId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDateTime { get; init; }
}

/// <summary>One intended create/update/no-op/conflict operation, with a secret-free explanation.</summary>
public sealed record AbsPlannedChange
{
    /// <summary>The resource kind: <c>botService</c>, <c>entraApplication</c>, or <c>servicePrincipal</c>.</summary>
    public required string Resource { get; init; }

    /// <summary>The intended action: <c>create</c>, <c>update</c>, <c>noop</c>, or <c>conflict</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The resource id, object id, or display name the operation targets.</summary>
    public required string Target { get; init; }

    /// <summary>Human-readable, secret-free reason (safe for logs and JSON).</summary>
    public required string Detail { get; init; }
}

/// <summary>One read-only preflight check result.</summary>
public sealed record AbsPreflightCheck
{
    /// <summary>The check id (for example <c>providerRegistration</c>, <c>writeCapability</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The status: <c>pass</c>, <c>fail</c>, <c>warn</c>, or <c>unknown</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Human-readable, secret-free detail.</summary>
    public required string Detail { get; init; }
}

/// <summary>Secret-free failure summary embedded on a <c>failed</c> report.</summary>
public sealed record AbsProvisioningError
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
}
