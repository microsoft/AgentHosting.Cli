using Microsoft.Agents.Hosting.Cli.Core.Errors;
using static Microsoft.Agents.Hosting.Cli.Core.Identity.BlueprintIdentityJson;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>Idempotently assigns an application role to a client service principal.</summary>
internal sealed class AppRoleAssignmentReconciler
{
    private const int MaximumPropagationAttempts = 5;
    private readonly IdentityGraphClient _graph;
    private readonly TimeSpan _retryDelay;

    public AppRoleAssignmentReconciler(IdentityGraphClient graph, TimeSpan retryDelay)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _retryDelay = retryDelay;
    }

    public async Task<bool> EnsureAsync(
        string tenantId,
        string principalId,
        string resourceServicePrincipalId,
        string appRoleId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        var path = $"/v1.0/servicePrincipals/{principalId}/appRoleAssignments";
        for (var attempt = 1; attempt <= MaximumPropagationAttempts; attempt++)
        {
            try
            {
                var assignments = await _graph.ReadCollectionAsync(
                    path,
                    scopes,
                    tenantId,
                    cancellationToken).ConfigureAwait(false);
                var exists = assignments.Any(item =>
                    string.Equals(
                        ReadString(item, "resourceId"),
                        resourceServicePrincipalId,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        ReadString(item, "appRoleId"),
                        appRoleId,
                        StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    return false;
                }

                await _graph.SendAsync(
                    HttpMethod.Post,
                    path,
                    new
                    {
                        principalId,
                        resourceId = resourceServicePrincipalId,
                        appRoleId,
                    },
                    scopes,
                    tenantId,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TargetRequestException error)
                when (error.Status == 404 && attempt < MaximumPropagationAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Application-role assignment retry loop completed unexpectedly.");
    }
}
