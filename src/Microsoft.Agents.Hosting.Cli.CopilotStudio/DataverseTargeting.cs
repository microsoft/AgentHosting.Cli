using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;
using System.Runtime.CompilerServices;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// The organization and tenant the current operation is targeting.
/// </summary>
internal static class DataverseOperationScope
{
    private static readonly AsyncLocal<DataverseTarget?> Current = new();

    internal sealed record DataverseTarget(Uri Organization, string? TenantId, EnvironmentResolutionContext? Context);

    internal static DataverseTarget? Capture() => Current.Value;

    internal static EnvironmentResolutionContext? Context => Current.Value?.Context;

    /// <summary>The organization the current operation targets, when one has been set.</summary>
    public static Uri? Organization => Current.Value?.Organization;

    /// <summary>The tenant the current operation targets, when one has been set.</summary>
    public static string? TenantId => Current.Value?.TenantId;

    /// <summary>Targets an organization and tenant for the remainder of the current async flow.</summary>
    /// <param name="organization">The Dataverse organization url.</param>
    /// <param name="tenantId">The tenant owning the environment, when it is known.</param>
    /// <param name="context">The captured authentication coordinates, or null for legacy callers.</param>
    public static void Set(Uri organization, string? tenantId, EnvironmentResolutionContext? context = null) =>
        Current.Value = new DataverseTarget(organization, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(), context);

    internal static IDisposable Push(DataverseTarget? target)
    {
        var previous = Current.Value;
        Current.Value = target;
        return new Restore(previous);
    }

    private sealed class Restore(DataverseTarget? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Points the sync library and the raw Web API client at one environment's Dataverse organization.
/// </summary>
internal static class DataverseTargeting
{
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> Gates = new();

    internal static async Task<IDisposable> EnterAsync(object client, CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(client, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Targets the organization and tenant behind an environment.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="environment">The environment to target.</param>
    public static IDisposable Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(environment);

        return Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, environment.DataverseOrganization, environment.TenantId, environment.Context);
    }

    /// <summary>Targets the organization and tenant behind an environment.</summary>
    /// <param name="dataverseClient">The sync library's standalone workflow client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="environment">The environment to target.</param>
    public static IDisposable Connect(IStandaloneWorkflowDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(environment);

        return Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, environment.DataverseOrganization, environment.TenantId, environment.Context);
    }

    /// <summary>
    /// Targets the organization and tenant a connected workspace records.
    /// </summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="workspace">The workspace whose recorded connection is being used.</param>
    public static IDisposable Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotWorkspaceInfo workspace)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(workspace);

        return Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, workspace.DataverseOrganization, workspace.TenantId, workspace.Context);
    }

    /// <summary>Targets an organization and tenant directly.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="dataverseOrganization">The organization url to target.</param>
    /// <param name="tenantId">The tenant owning the organization, when it is known.</param>
    public static IDisposable Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, Uri dataverseOrganization, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);

        return Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, dataverseOrganization, tenantId, null);
    }

    private static IDisposable Connect(Action<string> setDataverseUrl, AgentHostDataverseHttpClientAccessor httpClientAccessor, Uri dataverseOrganization, string? tenantId, EnvironmentResolutionContext? context)
    {
        ArgumentNullException.ThrowIfNull(httpClientAccessor);
        ArgumentNullException.ThrowIfNull(dataverseOrganization);

        var target = new DataverseOperationScope.DataverseTarget(dataverseOrganization, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(), context);
        RequireTarget(target);
        var scope = DataverseOperationScope.Push(target);
        try
        {
            setDataverseUrl(dataverseOrganization.ToString());
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    internal static void RequireTarget(DataverseOperationScope.DataverseTarget target)
    {
        if (target.Organization is { IsAbsoluteUri: true } organization
            && (organization.Query.Length != 0 || organization.Fragment.Length != 0))
            throw new Microsoft.Agents.Hosting.Cli.Core.Errors.EndpointTrustException(
                "The Dataverse organization must not contain a query or URI fragment.");
        if (target.Context is { } context)
        {
            Microsoft.Agents.Hosting.Cli.Core.Environments.PowerAppsServiceTarget.RequireContext(context);
            var cloud = Microsoft.Agents.Hosting.Cli.Core.Environments.PowerPlatformCloudMap.ForTarget(context.Target);
            if (!cloud.IsTrustedDataverseOrganization(target.Organization))
                throw new Microsoft.Agents.Hosting.Cli.Core.Errors.EndpointTrustException(
                    "The Dataverse organization is not trusted for the captured authentication target.");
        }
    }
}
