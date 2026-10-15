using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal sealed class BlueprintCredentialReconciler
{
    internal const string SecretDisplayName = "AgentHost CLI";

    private static readonly string[] TokenExchangeAudiences = ["api://AzureADTokenExchange"];

    private readonly IdentityGraphClient _graph;
    private readonly ManagedIdentityProvisioner _managedIdentities;

    public BlueprintCredentialReconciler(IIdentityRestClient rest, IdentityGraphClient graph)
    {
        ArgumentNullException.ThrowIfNull(rest);
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _managedIdentities = new ManagedIdentityProvisioner(rest);
    }

    public async Task<string> AddClientSecretAsync(
        string tenantId,
        string applicationObjectId,
        CancellationToken cancellationToken)
    {
        var response = await _graph.SendAsync(
            HttpMethod.Post,
            $"/beta/applications/{applicationObjectId}/microsoft.graph.agentIdentityBlueprint/addPassword",
            new
            {
                passwordCredential = new
                {
                    displayName = SecretDisplayName,
                    endDateTime = DateTimeOffset.UtcNow.AddYears(1),
                },
            },
            [IdentityProvisioningConfig.GraphReadWriteScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        return BlueprintIdentityJson.RequireString(
            BlueprintIdentityJson.ParseObject(response.Body),
            "secretText",
            "client secret create");
    }

    public async Task<string> EnsureFederatedCredentialAsync(
        BlueprintIdentityPlan plan,
        string applicationObjectId,
        CancellationToken cancellationToken)
    {
        var managedIdentity = await _managedIdentities
            .EnsureAsync(plan.Request.TenantId, plan.ManagedIdentity!, cancellationToken)
            .ConfigureAwait(false);
        await EnsureFederatedCredentialAsync(
            plan.Request.TenantId,
            applicationObjectId,
            managedIdentity.PrincipalId,
            plan.Request.ManagedIdentityName!,
            cancellationToken).ConfigureAwait(false);
        return managedIdentity.ClientId;
    }

    private async Task EnsureFederatedCredentialAsync(
        string tenantId,
        string applicationObjectId,
        string principalId,
        string name,
        CancellationToken cancellationToken)
    {
        var path =
            $"/beta/applications/{applicationObjectId}/microsoft.graph.agentIdentityBlueprint/" +
            $"federatedIdentityCredentials(name='{name}')";
        var issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        await _graph.SendAsync(
            HttpMethod.Patch,
            path,
            new
            {
                issuer,
                subject = principalId,
                audiences = TokenExchangeAudiences,
            },
            [IdentityProvisioningConfig.GraphReadWriteScope],
            tenantId,
            cancellationToken,
            new Dictionary<string, string> { ["Prefer"] = "create-if-missing" }).ConfigureAwait(false);
    }
}
