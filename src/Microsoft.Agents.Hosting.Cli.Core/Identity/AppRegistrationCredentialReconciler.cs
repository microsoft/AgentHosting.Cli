using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using System.Globalization;
using System.Text.Json.Nodes;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal enum FederatedCredentialChange
{
    None,
    Created,
    Reconciled,
    Unchanged,
}

/// <summary>Outcome of App Registration credential reconciliation.</summary>
internal sealed record AppRegistrationCredentialResult(
    string? Secret,
    bool RetainedExistingSecret,
    string? FederatedClientId,
    ProvisionedManagedIdentity? ManagedIdentity,
    FederatedCredentialChange FederatedCredentialChange = FederatedCredentialChange.None,
    DateTimeOffset? SecretExpiresOn = null);

internal sealed record ProvisionedClientSecret(string Value, DateTimeOffset ExpiresOn);

/// <summary>
/// Reconciles the credential that backs an App Registration identity over direct Graph and ARM REST.
/// ClientSecret mints a one-time password on the app; FederatedCredentials provisions a user-assigned
/// managed identity and federates it to the app; UserManagedIdentity provisions the managed identity
/// as the runtime credential and leaves the (companion) app credential-free. Uses the standard
/// <c>/v1.0/applications</c> routes rather than the Blueprint cast.
/// </summary>
internal sealed class AppRegistrationCredentialReconciler
{
    internal const string SecretDisplayName = "AgentHost CLI";
    private readonly IdentityGraphClient _graph;
    private readonly ManagedIdentityProvisioner _managedIdentities;

    public AppRegistrationCredentialReconciler(IIdentityRestClient rest, IdentityGraphClient graph)
    {
        ArgumentNullException.ThrowIfNull(rest);
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _managedIdentities = new ManagedIdentityProvisioner(rest);
    }

    public async Task<AppRegistrationCredentialResult> EnsureAsync(
        AppRegistrationIdentityPlan plan,
        AppRegistrationApplication application,
        CancellationToken cancellationToken)
    {
        switch (plan.Request.AuthType)
        {
            case IdentityCredentialType.ClientSecret:
                if (application.HasUsableClientSecret)
                {
                    return new AppRegistrationCredentialResult(null, true, null, null);
                }

                var secret = await AddClientSecretAsync(
                    plan.Request.TenantId,
                    application.ObjectId,
                    cancellationToken).ConfigureAwait(false);
                return new AppRegistrationCredentialResult(
                    secret.Value,
                    false,
                    null,
                    null,
                    SecretExpiresOn: secret.ExpiresOn);

            case IdentityCredentialType.FederatedCredentials:
                var federated = await _managedIdentities
                    .EnsureAsync(plan.Request.TenantId, plan.ManagedIdentity!, cancellationToken)
                    .ConfigureAwait(false);
                FederatedCredentialChange federationChange;
                try
                {
                    federationChange = await EnsureFederatedCredentialAsync(
                        plan.Request.TenantId,
                        application.ObjectId,
                        federated.PrincipalId,
                        plan.Request.ManagedIdentityName!,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AgentHostingException error)
                {
                    throw new AppRegistrationFederatedCredentialException(error, federated);
                }

                return new AppRegistrationCredentialResult(
                    null, false, federated.ClientId, federated, federationChange);

            case IdentityCredentialType.UserManagedIdentity:
                var managed = await _managedIdentities
                    .EnsureAsync(plan.Request.TenantId, plan.ManagedIdentity!, cancellationToken)
                    .ConfigureAwait(false);
                return new AppRegistrationCredentialResult(null, false, null, managed);

            default:
                throw new InvalidOperationException($"Unhandled auth type '{plan.Request.AuthType}'.");
        }

    }

    private async Task<ProvisionedClientSecret> AddClientSecretAsync(
        string tenantId,
        string applicationObjectId,
        CancellationToken cancellationToken)
    {
        var response = await _graph.SendAsync(
            HttpMethod.Post,
            $"/v1.0/applications/{applicationObjectId}/addPassword",
            new
            {
                passwordCredential = new
                {
                    displayName = SecretDisplayName,
                    endDateTime = DateTimeOffset.UtcNow.AddYears(1),
                },
            },
            [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        var body = ParseObject(response.Body);
        var secret = RequireString(body, "secretText", "client secret create");
        var expirationText = RequireString(body, "endDateTime", "client secret create");
        if (!DateTimeOffset.TryParse(
                expirationText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var expiresOn))
        {
            throw new TargetRequestException(
                "The client secret create response included an invalid endDateTime.",
                ExitCodes.Failure,
                TargetErrorKind.RequestFailed,
                status: 200);
        }

        return new ProvisionedClientSecret(secret, expiresOn);
    }

    private async Task<FederatedCredentialChange> EnsureFederatedCredentialAsync(
        string tenantId,
        string applicationObjectId,
        string principalId,
        string name,
        CancellationToken cancellationToken)
    {
        var upsertPath =
            $"/v1.0/applications/{applicationObjectId}/federatedIdentityCredentials(name='{name}')";
        var getPath =
            $"/v1.0/applications/{applicationObjectId}/federatedIdentityCredentials/{Uri.EscapeDataString(name)}";
        var issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        const string audience = "api://AzureADTokenExchange";
        var scopes = new[] { IdentityProvisioningConfig.GraphApplicationReadWriteScope };
        var existing = await _graph.TryGetAsync(
            getPath,
            scopes,
            tenantId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null &&
            FederatedCredentialMatches(existing.Body, issuer, principalId, audience))
        {
            return FederatedCredentialChange.Unchanged;
        }

        await _graph.SendAsync(
            HttpMethod.Patch,
            upsertPath,
            new
            {
                issuer,
                subject = principalId,
                audiences = new[] { audience },
            },
            scopes,
            tenantId,
            cancellationToken,
            new Dictionary<string, string> { ["Prefer"] = "create-if-missing" }).ConfigureAwait(false);

        return existing is null
            ? FederatedCredentialChange.Created
            : FederatedCredentialChange.Reconciled;
    }

    private static bool FederatedCredentialMatches(
        string? body,
        string issuer,
        string subject,
        string audience)
    {
        var existing = ParseObject(body);
        if (!string.Equals(ReadString(existing, "issuer"), issuer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ReadString(existing, "subject"), subject, StringComparison.Ordinal))
        {
            return false;
        }

        return existing["audiences"] is JsonArray audiences &&
            audiences.Count == 1 &&
            string.Equals(audiences[0]?.GetValue<string>(), audience, StringComparison.Ordinal);
    }
}

internal sealed class AppRegistrationFederatedCredentialException : AgentHostingException
{
    public AppRegistrationFederatedCredentialException(
        AgentHostingException cause,
        ProvisionedManagedIdentity managedIdentity)
        : base(
            cause.Message,
            cause.ExitCode,
            cause.ErrorKind,
            cause.Remediation,
            cause,
            cause.ErrorCode)
    {
        ManagedIdentity = managedIdentity ?? throw new ArgumentNullException(nameof(managedIdentity));
    }

    public ProvisionedManagedIdentity ManagedIdentity { get; }
}
