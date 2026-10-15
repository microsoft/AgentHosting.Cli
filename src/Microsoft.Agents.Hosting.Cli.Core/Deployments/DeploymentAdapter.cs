using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Direct-ARM implementation of <see cref="IDeploymentTransport"/>. It owns the deployment module's
/// external bot lifecycle, name-availability, subscription, and resource-group operations while
/// <see cref="DeploymentService"/> remains independent of the current provider.
/// </summary>
public sealed class DeploymentAdapter : DeploymentArmClient, IDeploymentTransport
{
    /// <summary>Creates the bot-lifecycle adapter.</summary>
    /// <param name="httpClient">The transport; a fake handler drives it offline in tests.</param>
    /// <param name="tokenProvider">The bearer-token seam, kept free of MSAL/Azure types.</param>
    /// <param name="config">The ARM target; defaults to Azure public cloud.</param>
    /// <param name="binaryName">The CLI name surfaced in error remediation text.</param>
    public DeploymentAdapter(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        DeploymentTargetConfig? config = null,
        string binaryName = "msagent")
        : base(httpClient, tokenProvider, config, binaryName)
    {
    }

    public async Task<DeploymentBotResource> GetBotAsync(
        string botResourceId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var url = BotUrl(botResourceId);
        var body = await RequestAsync(HttpMethod.Get, url, body: null, tenantId, cancellationToken).ConfigureAwait(false);

        return BotResponseMapper(body);
    }

    public async Task<DeploymentBotResource> CreateBotAsync(
        DeploymentCreateRequest request,
        CancellationToken cancellationToken)
    {
        var url = BotUrl(
            request.SubscriptionId!,
            request.ResourceGroup!,
            request.DeploymentName!);

        var properties = new Dictionary<string, object?>
        {
            ["msaAppId"] = request.EntraId,
            ["msaAppType"] = "SingleTenant",
            ["msaAppTenantId"] = request.TenantId,
            ["displayName"] = request.DisplayName ?? request.DeploymentName,
            ["description"] = request.Description,
            ["endpoint"] = request.Endpoint,
            ["schemaTransformationVersion"] = "1.3",
            // Non-secret defaults so the direct-ARM PUT is
            // byte-conformant with the Azure CLI's Microsoft.BotService create.
            ["isCmekEnabled"] = false,
            ["publicNetworkAccess"] = "Enabled",
            ["isStreamingSupported"] = false,
        };

        var payload = new Dictionary<string, object?>
        {
            ["location"] = request.Location ?? AbsDesiredStateBuilder.DefaultLocation,
            ["sku"] = new { name = (request.Sku ?? "F0").ToUpperInvariant() },
            ["kind"] = "azurebot",
            ["properties"] = properties,
        };

        var body = await RequestAsync(HttpMethod.Put, url, payload, request.TenantId, cancellationToken).ConfigureAwait(false);

        return BotResponseMapper(body);
    }

    public async Task<DeploymentBotResource> UpdateBotAsync(
        DeploymentUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var url = BotUrl(
            request.SubscriptionId!,
            request.ResourceGroup!,
            request.DeploymentName!);

        var properties = new Dictionary<string, object?> { };
        var payload = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Sku))
        {
            payload["sku"] = new { name = request.Sku.ToUpperInvariant() };
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            properties["displayName"] = request.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            properties["description"] = request.Description;
        }

        if (!string.IsNullOrWhiteSpace(request.Endpoint))
        {
            properties["endpoint"] = request.Endpoint;
        }

        if (properties.Count > 0)
        {
            payload["properties"] = properties;
        }

        var body = await RequestAsync(HttpMethod.Patch, url, payload, request.TenantId, cancellationToken).ConfigureAwait(false);

        return BotResponseMapper(body);
    }

    public async Task<bool> DeleteBotAsync(string botResourceId, string? targetTenantId, CancellationToken cancellationToken)
    {
        var url = BotUrl(botResourceId);
        try
        {
            await RequestAsync(HttpMethod.Delete, url, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private string BotUrl(string botResourceId)
        => $"{Config.ArmEndpoint}{BotServiceResourceId.ToRequestPath(botResourceId)}" +
           $"?api-version={Config.BotServiceApiVersion}";

    private string BotUrl(string subscriptionId, string resourceGroup, string botName) =>
        $"{Config.ArmEndpoint}{BotServiceResourceId.BuildRequestPath(subscriptionId, resourceGroup, botName)}" +
        $"?api-version={Config.BotServiceApiVersion}";

    public async Task<BotNameAvailability> CheckBotNameAvailabilityAsync(
        string name,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{Config.ArmEndpoint}/providers/Microsoft.BotService/checkNameAvailability" +
            $"?api-version={Config.BotServiceApiVersion}";
        var body = await RequestAsync(
            HttpMethod.Post,
            url,
            new { name, type = "azurebot" },
            targetTenantId,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(body))
        {
            return new BotNameAvailability { IsValid = false };
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("valid", out var validElement) &&
                validElement.ValueKind == JsonValueKind.True;

            return new BotNameAvailability
            {
                IsValid = valid,
                Message = GetString(root, "message"),
            };
        }
        catch (JsonException)
        {
            return new BotNameAvailability { IsValid = false };
        }
    }

    private static DeploymentBotResource BotResponseMapper(string body)
    {
        var root = ParseObject(body);
        var properties = GetObject(root, "properties", isOptional: false)!.Value;
        var sku = GetObject(root, "sku", isOptional: true);
        _ = GetObject(root, "tags", isOptional: true);
        var msaAppType = GetString(properties, "msaAppType")!;

        return new DeploymentBotResource
        {
            ResourceId = GetString(root, "id")!,
            Location = GetString(root, "location")!,
            Name = GetString(root, "name")!,
            EntraId = GetString(properties, "msaAppId")!,
            EntraIdType = msaAppType == "SingleTenant" ? "AppReg" : msaAppType,
            DisplayName = GetString(properties, "displayName")!,
            Description = GetString(properties, "description"),
            Endpoint = GetString(properties, "endpoint"),
            TenantId = GetString(properties, "msaAppTenantId") ?? GetString(properties, "tenantId")!,
            Sku = sku is { } value ? GetString(value, "name")! : string.Empty,
            RawArmResponse = BotServiceResponseRedactor.RedactBot(root),
        };
    }
}
