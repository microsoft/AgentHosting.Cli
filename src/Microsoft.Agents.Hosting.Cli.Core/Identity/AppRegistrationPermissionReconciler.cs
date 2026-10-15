using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Declares the always-on bot permissions (and optional Observability/MCP discovery) on an ordinary
/// App Registration by resolving each scope/role id from the resource service principal and merging
/// them into <c>requiredResourceAccess</c>. The merge is read-modify-write and additive: existing
/// resource-access entries are preserved, so unrelated API permissions are never dropped.
/// </summary>
internal sealed class AppRegistrationPermissionReconciler
{
    private readonly IdentityGraphClient _graph;
    private readonly AppRoleAssignmentReconciler _appRoles;

    public AppRegistrationPermissionReconciler(IdentityGraphClient graph, TimeSpan retryDelay)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _appRoles = new AppRoleAssignmentReconciler(graph, retryDelay);
    }

    public async Task<AppRegistrationPermissionReconcileResult> EnsureAsync(
        AppRegistrationIdentityPlan plan,
        AppRegistrationApplication application,
        string principalId,
        CancellationToken cancellationToken)
    {
        var desired = application.RequiredResourceAccess.DeepClone().AsArray();
        var changed = false;
        var roles = new List<ResolvedAppRole>();
        foreach (var permission in plan.Permissions)
        {
            var resource = await ResolveResourceAsync(
                plan.Request.TenantId,
                permission,
                cancellationToken).ConfigureAwait(false);
            var accessEntries = new List<(string Id, string Type)>
            {
                (FindPermissionId(resource, "oauth2PermissionScopes", permission.DelegatedScope), "Scope"),
            };
            if (permission.ApplicationRole is not null)
            {
                var roleId = FindPermissionId(resource, "appRoles", permission.ApplicationRole);
                accessEntries.Add((roleId, "Role"));
                roles.Add(new ResolvedAppRole(
                    permission,
                    RequireString(resource, "id", $"{permission.Name} service principal"),
                    roleId));
            }

            changed |= MergeResourceAccess(desired, permission.ResourceAppId, accessEntries);
        }

        if (changed)
        {
            await _graph.SendAsync(
                HttpMethod.Patch,
                $"/v1.0/applications/{application.ObjectId}",
                new JsonObject { ["requiredResourceAccess"] = desired },
                [IdentityProvisioningConfig.GraphApplicationReadWriteScope],
                plan.Request.TenantId,
                cancellationToken).ConfigureAwait(false);
        }

        var roleResults = new List<AppRoleAssignmentChange>();
        foreach (var role in roles)
        {
            var assignmentChanged = await _appRoles.EnsureAsync(
                plan.Request.TenantId,
                principalId,
                role.ResourceServicePrincipalId,
                role.AppRoleId,
                [
                    IdentityProvisioningConfig.GraphAppRoleAssignmentReadWriteScope,
                    IdentityProvisioningConfig.GraphApplicationReadScope,
                ],
                cancellationToken).ConfigureAwait(false);
            roleResults.Add(new AppRoleAssignmentChange(role.Permission, assignmentChanged));
        }

        return new AppRegistrationPermissionReconcileResult(changed, roleResults);
    }

    internal sealed record ResolvedAppRole(
        BlueprintPermission Permission,
        string ResourceServicePrincipalId,
        string AppRoleId);

    internal sealed record AppRoleAssignmentChange(
        BlueprintPermission Permission,
        bool Changed);

    internal sealed record AppRegistrationPermissionReconcileResult(
        bool DeclarationsChanged,
        IReadOnlyList<AppRoleAssignmentChange> AppRoleAssignments);

    private async Task<JsonObject> ResolveResourceAsync(
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
        return values.Count == 1
            ? values[0]
            : throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                $"The {permission.Name} resource service principal is unavailable in the target tenant.",
                $"Provision or enable resource application {permission.ResourceAppId}, then retry.");
    }

    private static bool MergeResourceAccess(
        JsonArray desired,
        string resourceAppId,
        IReadOnlyList<(string Id, string Type)> accessEntries)
    {
        var resource = desired.OfType<JsonObject>().FirstOrDefault(item =>
            string.Equals(ReadString(item, "resourceAppId"), resourceAppId, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            desired.Add(new JsonObject
            {
                ["resourceAppId"] = resourceAppId,
                ["resourceAccess"] = new JsonArray(
                    accessEntries
                        .Select(entry => (JsonNode?)new JsonObject
                        {
                            ["id"] = entry.Id,
                            ["type"] = entry.Type,
                        })
                        .ToArray()),
            });
            return true;
        }

        var access = resource["resourceAccess"]?.AsArray() ?? [];
        resource["resourceAccess"] = access;
        var changed = false;
        foreach (var (id, type) in accessEntries)
        {
            var exists = access.OfType<JsonObject>().Any(item =>
                string.Equals(ReadString(item, "id"), id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ReadString(item, "type"), type, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                access.Add(new JsonObject { ["id"] = id, ["type"] = type });
                changed = true;
            }
        }

        return changed;
    }

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
