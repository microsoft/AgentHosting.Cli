using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>A user-assigned managed identity resolved through Azure Resource Manager.</summary>
/// <param name="ResourceId">The fully qualified ARM resource id of the identity.</param>
/// <param name="ClientId">The application (client) id of the identity.</param>
/// <param name="PrincipalId">The service principal (object) id of the identity.</param>
/// <param name="Created">True when the identity was newly created (GET returned 404 and a PUT followed);
/// false when an existing identity was reused. Drives created-vs-reused apply wording.</param>
internal sealed record ProvisionedManagedIdentity(string ResourceId, string ClientId, string PrincipalId, bool Created);

/// <summary>
/// Creates or reconciles a user-assigned managed identity over Azure Resource Manager REST. Shared by
/// the Blueprint and App Registration credential flows so both use the same idempotent GET-then-PUT
/// convergence and property extraction.
/// </summary>
internal sealed class ManagedIdentityProvisioner
{
    private readonly IIdentityRestClient _rest;

    public ManagedIdentityProvisioner(IIdentityRestClient rest)
    {
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
    }

    public async Task<ProvisionedManagedIdentity> EnsureAsync(
        string tenantId,
        ManagedIdentityPlan identity,
        CancellationToken cancellationToken)
    {
        var path =
            $"/subscriptions/{identity.SubscriptionId}/resourceGroups/{identity.ResourceGroup}" +
            $"/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{identity.Name}";
        var get = await _rest.SendArmAsync(
            HttpMethod.Get,
            path,
            null,
            tenantId,
            cancellationToken).ConfigureAwait(false);
        var created = get.StatusCode == 404;
        var response = created
            ? await _rest.SendArmAsync(
                HttpMethod.Put,
                path,
                new { location = identity.Location },
                tenantId,
                cancellationToken).ConfigureAwait(false)
            : get;
        EnsureSuccess(response, created ? HttpMethod.Put : HttpMethod.Get, path);
        var body = BlueprintIdentityJson.ParseObject(response.Body);
        var properties = body["properties"] as JsonObject
            ?? throw new TargetRequestException(
                "The managed identity response did not include properties.",
                ExitCodes.Failure,
                TargetErrorKind.RequestFailed,
                response.StatusCode);
        return new ProvisionedManagedIdentity(
            BlueprintIdentityJson.RequireString(body, "id", "managed identity"),
            BlueprintIdentityJson.RequireString(properties, "clientId", "managed identity"),
            BlueprintIdentityJson.RequireString(properties, "principalId", "managed identity"),
            created);
    }

    private static void EnsureSuccess(IdentityRestResponse response, HttpMethod method, string path)
    {
        if (!response.IsSuccess)
        {
            throw TargetRequestException.MapTargetResponseError(
                response.StatusCode,
                response.Body,
                method.Method,
                path,
                "ARM",
                "msagent");
        }
    }
}
