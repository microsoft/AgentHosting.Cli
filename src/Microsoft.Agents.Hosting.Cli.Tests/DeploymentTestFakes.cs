using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class RecordingBotNameProbe
{
    public List<string> Calls { get; } = [];
    public List<string> CheckedNames { get; } = [];
    public bool IsAvailable { get; set; } = true;
    public string? UnavailableMessage { get; set; }

    public Task<BotNameAvailability> CheckBotNameAvailabilityAsync(
        string name,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        Calls.Add("arm.checkName");
        CheckedNames.Add(name);
        return Task.FromResult(new BotNameAvailability
        {
            IsValid = IsAvailable,
            Message = IsAvailable ? string.Empty : UnavailableMessage,
        });
    }
}

internal sealed class FakeSubscriptionResolver
{
    public AzureSubscriptionInfo Result { get; set; } =
        new() { SubscriptionId = "sub-1", TenantId = "tenant-1" };

    public ProvisioningException? Throw { get; set; }
    public bool IsCalled { get; private set; }
    public string? RequestedPreferred { get; private set; }
    public string? RequestedTenant { get; private set; }

    public Task<AzureSubscriptionInfo> ResolveSubscriptionAsync(
        string? preferredSubscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        IsCalled = true;
        RequestedPreferred = preferredSubscriptionId;
        RequestedTenant = targetTenantId;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(Result);
    }
}

internal sealed class FakeResourceGroupResolver
{
    public AzureResourceGroupInfo Result { get; set; } = new() { Name = "rg-resolved", Location = "global" };
    public ProvisioningException? Throw { get; set; }
    public bool IsCalled { get; private set; }
    public string? RequestedSubscription { get; private set; }
    public string? RequestedPreferred { get; private set; }
    public string? RequestedTenant { get; private set; }

    public Task<AzureResourceGroupInfo> ResolveResourceGroupAsync(
        string subscriptionId,
        string? preferredResourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        IsCalled = true;
        RequestedSubscription = subscriptionId;
        RequestedPreferred = preferredResourceGroup;
        RequestedTenant = targetTenantId;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(Result);
    }
}

internal sealed class CapturingBotProvisioningPort
{
    public DeploymentBotResource? ExistingBot { get; set; }
    public Exception? GetBotThrows { get; set; }
    public Exception? UpdateBotThrows { get; set; }
    public List<DeploymentCreateRequest> CreateRequests { get; } = [];
    public List<DeploymentUpdateRequest> UpdateRequests { get; } = [];
    public List<string> Deleted { get; } = [];
    public string? LastBotResourceId { get; private set; }
    public string? LastTenantId { get; private set; }
    public System.Text.Json.JsonElement? RawArmResponse { get; set; }

    public Task<DeploymentBotResource> GetBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken)
    {
        LastBotResourceId = botResourceId;
        LastTenantId = targetTenantId;
        if (GetBotThrows is not null)
        {
            throw GetBotThrows;
        }

        if (ExistingBot is null)
        {
            throw new TargetRequestException(
                "ARM request failed (GET …) with HTTP 404. Error code: ResourceNotFound.",
                ExitCodes.Usage,
                TargetErrorKind.RequestFailed,
                404,
                "Resource not found.");
        }

        return Task.FromResult(ExistingBot);
    }

    public Task<DeploymentBotResource> CreateBotAsync(DeploymentCreateRequest request, CancellationToken cancellationToken)
    {
        CreateRequests.Add(request);
        return Task.FromResult(FromCreate(request));
    }

    public Task<DeploymentBotResource> UpdateBotAsync(DeploymentUpdateRequest request, CancellationToken cancellationToken)
    {
        if (UpdateBotThrows is not null)
        {
            throw UpdateBotThrows;
        }

        UpdateRequests.Add(request);
        return Task.FromResult(new DeploymentBotResource
        {
            ResourceId = BuildBotResourceId(request.SubscriptionId ?? "sub-1", request.ResourceGroup ?? "rg-1", request.DeploymentName ?? "dev"),
            Name = request.DeploymentName ?? "dev",
            EntraId = "app-1",
            EntraIdType = "AppReg",
            Endpoint = request.Endpoint,
            Sku = request.Sku ?? "F0",
            Location = "global",
            TenantId = request.TenantId ?? "tenant-1",
            DisplayName = request.DisplayName ?? request.DeploymentName ?? "dev",
            Description = request.Description,
            RawArmResponse = RawArmResponse,
        });
    }

    public Task<bool> DeleteBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken)
    {
        Deleted.Add(botResourceId);
        return Task.FromResult(true);
    }

    private DeploymentBotResource FromCreate(DeploymentCreateRequest request) => new()
    {
        ResourceId = BuildBotResourceId(request.SubscriptionId ?? "sub-1", request.ResourceGroup ?? "rg-1", request.DeploymentName),
        Name = request.DeploymentName ?? "dev",
        EntraId = request.EntraId ?? "app-1",
        EntraIdType = request.EntraIdType ?? "AppReg",
        Endpoint = request.Endpoint,
        Sku = request.Sku ?? "F0",
        Location = request.Location ?? "global",
        TenantId = request.TenantId ?? "tenant-1",
        DisplayName = request.DisplayName ?? request.DeploymentName ?? "dev",
        Description = request.Description,
        RawArmResponse = RawArmResponse,
    };

    private static string BuildBotResourceId(string subscriptionId, string resourceGroup, string name) =>
        $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.BotService/botServices/{name}";
}

internal sealed class CompositeDeploymentTransport : IDeploymentTransport
{
    private readonly CapturingBotProvisioningPort _botProvisioning;
    private readonly RecordingBotNameProbe _botNameAvailability;
    private readonly FakeSubscriptionResolver _subscriptionResolver;
    private readonly FakeResourceGroupResolver _resourceGroupResolver;

    public CompositeDeploymentTransport(
        CapturingBotProvisioningPort? botProvisioning = null,
        RecordingBotNameProbe? botNameAvailability = null,
        FakeSubscriptionResolver? subscriptionResolver = null,
        FakeResourceGroupResolver? resourceGroupResolver = null)
    {
        _botProvisioning = botProvisioning ?? new CapturingBotProvisioningPort();
        _botNameAvailability = botNameAvailability ?? new RecordingBotNameProbe();
        _subscriptionResolver = subscriptionResolver ?? new FakeSubscriptionResolver();
        _resourceGroupResolver = resourceGroupResolver ?? new FakeResourceGroupResolver();
    }

    public Task<AzureSubscriptionInfo> ResolveSubscriptionAsync(
        string? preferredSubscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        _subscriptionResolver.ResolveSubscriptionAsync(
            preferredSubscriptionId,
            targetTenantId,
            cancellationToken);

    public Task<AzureResourceGroupInfo> ResolveResourceGroupAsync(
        string subscriptionId,
        string? preferredResourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        _resourceGroupResolver.ResolveResourceGroupAsync(
            subscriptionId,
            preferredResourceGroup,
            targetTenantId,
            cancellationToken);

    public Task<BotNameAvailability> CheckBotNameAvailabilityAsync(
        string name,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        _botNameAvailability.CheckBotNameAvailabilityAsync(name, targetTenantId, cancellationToken);

    public Task<DeploymentBotResource> GetBotAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        _botProvisioning.GetBotAsync(botResourceId, targetTenantId, cancellationToken);

    public Task<DeploymentBotResource> CreateBotAsync(
        DeploymentCreateRequest request,
        CancellationToken cancellationToken) =>
        _botProvisioning.CreateBotAsync(request, cancellationToken);

    public Task<DeploymentBotResource> UpdateBotAsync(
        DeploymentUpdateRequest request,
        CancellationToken cancellationToken) =>
        _botProvisioning.UpdateBotAsync(request, cancellationToken);

    public Task<bool> DeleteBotAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        _botProvisioning.DeleteBotAsync(botResourceId, targetTenantId, cancellationToken);
}

internal static class DeploymentServiceTestFactory
{
    public static DeploymentService Build(IDeploymentStore store, CapturingBotProvisioningPort botProvisioning) =>
        new(
            store,
            new CompositeDeploymentTransport(botProvisioning));
}

internal sealed class InMemoryDeploymentRecordStore : IDeploymentStore
{
    public InMemoryDeploymentRecordStore(params CodeAgentDeployment[] seed) => Records = [.. seed];

    public List<CodeAgentDeployment> Records { get; }

    public string? LastProject { get; private set; }

    public IReadOnlyList<CodeAgentDeployment> List(string? project)
    {
        LastProject = project;
        return [.. Records];
    }

    public void Upsert(string? project, CodeAgentDeployment deployment)
    {
        LastProject = project;
        Records.RemoveAll(record => string.Equals(record.DeploymentId, deployment.DeploymentId, StringComparison.OrdinalIgnoreCase));

        if (deployment.IsDefault)
        {
            Records.Replace(record => record.AgentId == deployment.AgentId && record.IsDefault, record => record with { IsDefault = false });
        }

        Records.Add(deployment);
    }

    public void Remove(string? project, string deploymentId)
    {
        LastProject = project;
        Records.RemoveAll(record => string.Equals(record.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class DeploymentRecordListExtensions
{
    public static void Replace(
        this List<CodeAgentDeployment> records,
        Func<CodeAgentDeployment, bool> match,
        Func<CodeAgentDeployment, CodeAgentDeployment> replacement)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (match(records[index]))
            {
                records[index] = replacement(records[index]);
            }
        }
    }
}
