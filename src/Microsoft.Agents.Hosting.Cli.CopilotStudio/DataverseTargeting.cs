using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// The organization and tenant the current operation is targeting.
/// </summary>
internal static class DataverseOperationScope
{
    private static readonly AsyncLocal<DataverseTarget?> Current = new();

    /// <summary>The organization the current operation targets, when one has been set.</summary>
    public static Uri? Organization => Current.Value?.Organization;

    /// <summary>The tenant the current operation targets, when one has been set.</summary>
    public static string? TenantId => Current.Value?.TenantId;

    /// <summary>Targets an organization and tenant for the remainder of the current async flow.</summary>
    /// <param name="organization">The Dataverse organization url.</param>
    /// <param name="tenantId">The tenant owning the environment, when it is known.</param>
    public static void Set(Uri organization, string? tenantId) =>
        Current.Value = new DataverseTarget(organization, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim());

    private sealed record DataverseTarget(Uri Organization, string? TenantId);
}

/// <summary>
/// Points the sync library and the raw Web API client at one environment's Dataverse organization.
/// </summary>
internal static class DataverseTargeting
{
    /// <summary>Targets the organization and tenant behind an environment.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="environment">The environment to target.</param>
    public static void Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(environment);

        Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, environment.DataverseOrganization, environment.TenantId);
    }

    /// <summary>Targets the organization and tenant behind an environment.</summary>
    /// <param name="dataverseClient">The sync library's standalone workflow client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="environment">The environment to target.</param>
    public static void Connect(IStandaloneWorkflowDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotEnvironmentContext environment)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(environment);

        Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, environment.DataverseOrganization, environment.TenantId);
    }

    /// <summary>
    /// Targets the organization and tenant a connected workspace records.
    /// </summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="workspace">The workspace whose recorded connection is being used.</param>
    public static void Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, CopilotWorkspaceInfo workspace)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);
        ArgumentNullException.ThrowIfNull(workspace);

        Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, workspace.DataverseOrganization, workspace.TenantId);
    }

    /// <summary>Targets an organization and tenant directly.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the organization.</param>
    /// <param name="dataverseOrganization">The organization url to target.</param>
    /// <param name="tenantId">The tenant owning the organization, when it is known.</param>
    public static void Connect(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor, Uri dataverseOrganization, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(dataverseClient);

        Connect(dataverseClient.SetDataverseUrl, httpClientAccessor, dataverseOrganization, tenantId);
    }

    private static void Connect(Action<string> setDataverseUrl, AgentHostDataverseHttpClientAccessor httpClientAccessor, Uri dataverseOrganization, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(httpClientAccessor);
        ArgumentNullException.ThrowIfNull(dataverseOrganization);

        DataverseOperationScope.Set(dataverseOrganization, tenantId);
        setDataverseUrl(dataverseOrganization.ToString());
        httpClientAccessor.SetDataverseOrganization(dataverseOrganization);
    }
}
