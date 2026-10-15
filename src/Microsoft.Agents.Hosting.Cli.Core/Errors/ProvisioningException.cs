namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Category of an Azure Bot Service scaffolding failure. Each kind is a distinct, agent-loop
/// actionable condition and maps to a process exit code via the shared 3/2/1 taxonomy. This mirrors
/// the TypeScript <c>ProvisioningErrorKind</c> so both implementations expose the same stable error
/// categories.
/// </summary>
public enum ProvisioningErrorKind
{
    /// <summary>No Azure sign-in and interactive login is not available (exit 3).</summary>
    MissingAzureAuth,

    /// <summary>A required Azure context value (tenant/subscription/resource-group/endpoint) is absent (exit 2).</summary>
    MissingContext,

    /// <summary>Context resolved to more than one candidate and none was chosen (exit 2).</summary>
    AmbiguousContext,

    /// <summary>An existing bot/app conflicts with the request in a way a rerun cannot safely resolve (exit 2).</summary>
    NamingConflict,

    /// <summary>The <c>Microsoft.BotService</c> resource provider is not registered on the subscription (exit 1).</summary>
    ProviderNotRegistered,

    /// <summary>The requested SKU/quota is unavailable in the target region (exit 1).</summary>
    SkuQuota,

    /// <summary>An Azure Policy assignment denied the operation (exit 1).</summary>
    PolicyDenied,

    /// <summary>The operator lacks the required Azure RBAC / Entra permission (exit 1).</summary>
    PermissionDenied,

    /// <summary>
    /// A downstream Azure response did not match the expected resource schema. API/integration fault (exit 1).
    /// </summary>
    InvalidResponse,
}

/// <summary>Maps a <see cref="ProvisioningErrorKind"/> to its stable wire string and exit code.</summary>
public static class ProvisioningErrorKindExtensions
{
    /// <summary>The stable kebab-case string emitted as <c>errorKind</c> in the JSON failure envelope.</summary>
    public static string ToWireString(this ProvisioningErrorKind kind) => kind switch
    {
        ProvisioningErrorKind.MissingAzureAuth => "missing-azure-auth",
        ProvisioningErrorKind.MissingContext => "missing-context",
        ProvisioningErrorKind.AmbiguousContext => "ambiguous-context",
        ProvisioningErrorKind.NamingConflict => "naming-conflict",
        ProvisioningErrorKind.ProviderNotRegistered => "provider-not-registered",
        ProvisioningErrorKind.SkuQuota => "sku-quota",
        ProvisioningErrorKind.PolicyDenied => "policy-denied",
        ProvisioningErrorKind.PermissionDenied => "permission-denied",
        ProvisioningErrorKind.InvalidResponse => "invalid-response",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provisioning error kind."),
    };

    /// <summary>The process exit code for the kind, per the shared 3/2/1 taxonomy.</summary>
    public static int ToExitCode(this ProvisioningErrorKind kind) => kind switch
    {
        ProvisioningErrorKind.MissingAzureAuth => ExitCodes.NotSignedIn,
        ProvisioningErrorKind.MissingContext
            or ProvisioningErrorKind.AmbiguousContext
            or ProvisioningErrorKind.NamingConflict => ExitCodes.Usage,
        ProvisioningErrorKind.ProviderNotRegistered
            or ProvisioningErrorKind.SkuQuota
            or ProvisioningErrorKind.PolicyDenied
            or ProvisioningErrorKind.PermissionDenied
            or ProvisioningErrorKind.InvalidResponse => ExitCodes.Failure,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provisioning error kind."),
    };
}

/// <summary>
/// A typed Azure Bot Service scaffolding failure. Carries a <see cref="ProvisioningErrorKind"/> and a
/// concrete <see cref="AgentHostingException.Remediation"/>; the exit code and <c>errorKind</c> wire
/// string are derived from the kind. Messages and remediation are secret-free by construction.
/// </summary>
public sealed class ProvisioningException : AgentHostingException
{
    public ProvisioningException(ProvisioningErrorKind kind, string message, string remediation)
        : base(message, kind.ToExitCode(), kind.ToWireString(), remediation)
    {
        Kind = kind;
    }

    /// <summary>The typed failure category.</summary>
    public ProvisioningErrorKind Kind { get; }
}
