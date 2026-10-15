using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal sealed class BlueprintPermissionReconciler
{
    private readonly IdentityGraphClient _graph;
    private readonly AppRoleAssignmentReconciler _appRoles;

    public BlueprintPermissionReconciler(IdentityGraphClient graph, TimeSpan retryDelay)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _appRoles = new AppRoleAssignmentReconciler(graph, retryDelay);
    }

    public async Task<ResolvedBlueprintPermission> ResolveAsync(
        string tenantId,
        BlueprintPermission permission,
        CancellationToken cancellationToken)
    {
        var query = "/v1.0/servicePrincipals?$filter=" +
            Uri.EscapeDataString($"appId eq '{permission.ResourceAppId}'") +
            "&$select=id,oauth2PermissionScopes,appRoles";
        var values = await _graph.ReadCollectionAsync(
            query,
            [IdentityProvisioningConfig.GraphApplicationReadScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        if (values.Count != 1)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                $"The {permission.Name} resource service principal is unavailable in the target tenant.",
                $"Provision or enable resource application {permission.ResourceAppId}, then retry.");
        }

        var resource = values[0];
        _ = FindPermissionId(resource, "oauth2PermissionScopes", permission.DelegatedScope);
        var roleId = permission.ApplicationRole is null
            ? null
            : FindPermissionId(resource, "appRoles", permission.ApplicationRole);
        return new ResolvedBlueprintPermission(
            permission,
            RequireString(resource, "id", $"{permission.Name} service principal"),
            roleId);
    }

    public async Task<PermissionReconcileResult> EnsureAsync(
        string tenantId,
        string principalId,
        string applicationObjectId,
        ResolvedBlueprintPermission permission,
        CancellationToken cancellationToken)
    {
        var appRoleAssignmentRequiresAdministrator = false;
        if (permission.AppRoleId is not null)
        {
            try
            {
                await _appRoles.EnsureAsync(
                    tenantId,
                    principalId,
                    permission.ResourceServicePrincipalId,
                    permission.AppRoleId,
                    [IdentityProvisioningConfig.GraphReadWriteScope],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TargetRequestException error) when (error.Status == 403)
            {
                appRoleAssignmentRequiresAdministrator = true;
            }
        }

        await EnsureInheritablePermissionAsync(
            tenantId,
            applicationObjectId,
            permission.Spec.ResourceAppId,
            permission.AppRoleId is not null,
            cancellationToken).ConfigureAwait(false);
        return new PermissionReconcileResult(appRoleAssignmentRequiresAdministrator);
    }

    private async Task EnsureInheritablePermissionAsync(
        string tenantId,
        string applicationObjectId,
        string resourceAppId,
        bool allowRoles,
        CancellationToken cancellationToken)
    {
        var basePath =
            $"/beta/applications/{applicationObjectId}/microsoft.graph.agentIdentityBlueprint/inheritablePermissions";
        var permissions = await _graph.ReadCollectionAsync(
            basePath,
            [IdentityProvisioningConfig.GraphReadWriteScope],
            tenantId,
            cancellationToken).ConfigureAwait(false);
        var existing = permissions.FirstOrDefault(item =>
            string.Equals(ReadString(item, "resourceAppId"), resourceAppId, StringComparison.OrdinalIgnoreCase));
        var desired = new JsonObject
        {
            ["inheritableScopes"] = CreateAllAllowedPermission("Scopes"),
            ["inheritableRoles"] = allowRoles
                ? CreateAllAllowedPermission("Roles")
                : CreateNoRolesPermission(),
        };
        if (existing is null)
        {
            desired["resourceAppId"] = resourceAppId;
            await _graph.SendAsync(
                HttpMethod.Post,
                basePath,
                desired,
                [IdentityProvisioningConfig.GraphReadWriteScope],
                tenantId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var scopesKind = ReadString(existing["inheritableScopes"] as JsonObject, "kind");
        var rolesKind = ReadString(existing["inheritableRoles"] as JsonObject, "kind");
        var desiredRolesKind = allowRoles ? "allAllowed" : "none";
        if (!string.Equals(scopesKind, "allAllowed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rolesKind, desiredRolesKind, StringComparison.OrdinalIgnoreCase))
        {
            await _graph.SendAsync(
                HttpMethod.Patch,
                $"{basePath}/{resourceAppId}",
                desired,
                [IdentityProvisioningConfig.GraphReadWriteScope],
                tenantId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonObject CreateAllAllowedPermission(string permissionType) => new()
    {
        ["@odata.type"] = $"#microsoft.graph.allAllowed{permissionType}",
        ["kind"] = "allAllowed",
    };

    private static JsonObject CreateNoRolesPermission() => new()
    {
        ["@odata.type"] = "#microsoft.graph.noRoles",
        ["kind"] = "none",
    };

    private static string FindPermissionId(JsonObject resource, string collectionName, string value)
    {
        var collection = resource[collectionName]?.AsArray() ?? [];
        var permission = collection.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(ReadString(item, "value"), value, StringComparison.OrdinalIgnoreCase) &&
            item["isEnabled"]?.GetValue<bool>() != false);
        return permission is null
            ? throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                $"Permission \"{value}\" is not exposed by the target resource.",
                "Verify the resource application and permission catalog, then retry.")
            : RequireString(permission, "id", value);
    }
}

internal sealed record ResolvedBlueprintPermission(
    BlueprintPermission Spec,
    string ResourceServicePrincipalId,
    string? AppRoleId);

internal sealed record PermissionReconcileResult(
    bool AppRoleAssignmentRequiresAdministrator);
