using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// The direct-ARM Azure Bot Service adapter — the real, network-backed implementation of both the
/// read-only <see cref="IAbsStateReader"/> and the single mutating <see cref="IAbsMutationPort"/>. A
/// faithful C# port of the shipped TypeScript <c>AbsProvisioningAdapter</c> +
/// <c>createAdapterAzureStateReader</c> + <c>createAdapterArmMutationPort</c>, scoped to the ARM
/// surface the C# zero-Graph path uses. No Graph calls are issued.
///
/// It targets the real <c>Microsoft.BotService</c> REST contract via direct <c>GET</c>/<c>PUT</c>
/// (api-version <c>2022-09-15</c>) — it never shells out to <c>az bot</c>, so there is no runtime
/// Azure-CLI dependency and no process is spawned. All audience/endpoint/version strings live in
/// <see cref="AbsTargetConfig"/>; the bearer token is applied to the <c>Authorization</c> header and
/// is never logged. Non-2xx responses become typed, secret-free
/// <see cref="TargetRequestException"/>s (existence/access probes branch on status without throwing).
///
/// It lives in the core assembly (not the auth adapter) because it needs no MSAL/Azure SDK type — it
/// speaks raw HTTP over an injectable <see cref="HttpClient"/> and consumes the abstract
/// <see cref="ITokenProvider"/> seam — so the host's offline guarantee (which forbids MSAL/Azure/Graph
/// referenced assemblies) is preserved, and a fake <see cref="HttpMessageHandler"/> drives it fully
/// offline in tests.
/// </summary>
public sealed class ArmAbsProvisioningAdapter : IAbsStateReader, IAbsMutationPort
{
    // Generic-ARM api-versions for provider/subscription/resource-group probes. These endpoints are
    // not part of the Microsoft.BotService contract (which owns Config.ArmApiVersion), so their
    // long-stable versions are pinned here.
    private const string ArmProvidersApiVersion = "2021-04-01";
    private const string ArmSubscriptionsApiVersion = "2020-01-01";
    private const string ArmResourceGroupsApiVersion = "2021-04-01";
    private const string ArmAudienceLabel = "ARM";

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly AbsTargetConfig _config;
    private readonly string _binaryName;

    public ArmAbsProvisioningAdapter(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        AbsTargetConfig? config = null,
        string binaryName = "msagent")
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _config = config ?? AbsTargetConfig.AzurePublic;
        _binaryName = string.IsNullOrWhiteSpace(binaryName) ? "msagent" : binaryName;
    }

    /// <inheritdoc />
    public async Task<AbsProviderRegistration> GetProviderRegistrationAsync(
        string subscriptionId,
        string providerNamespace,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{_config.ArmEndpoint}/subscriptions/{subscriptionId}/providers/{providerNamespace}" +
            $"?api-version={ArmProvidersApiVersion}";
        var probe = await ProbeAsync(HttpMethod.Get, url, targetTenantId, cancellationToken).ConfigureAwait(false);

        var state = "Unknown";
        if (probe.Ok)
        {
            var raw = ReadStringProperty(probe.Body, "registrationState");
            state = raw switch
            {
                "Registered" => "Registered",
                "NotRegistered" or "Unregistered" => "NotRegistered",
                "Registering" => "Registering",
                _ => "Unknown",
            };
        }

        return new AbsProviderRegistration
        {
            ProviderNamespace = providerNamespace,
            RegistrationState = state,
        };
    }

    /// <inheritdoc />
    public async Task<AbsScopeAccess> GetSubscriptionAccessAsync(
        string subscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url = $"{_config.ArmEndpoint}/subscriptions/{subscriptionId}?api-version={ArmSubscriptionsApiVersion}";
        return ToScopeAccess(await ProbeAsync(HttpMethod.Get, url, targetTenantId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<AbsScopeAccess> GetResourceGroupAccessAsync(
        string subscriptionId,
        string resourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{_config.ArmEndpoint}/subscriptions/{subscriptionId}/resourcegroups/{resourceGroup}" +
            $"?api-version={ArmResourceGroupsApiVersion}";
        return ToScopeAccess(await ProbeAsync(HttpMethod.Get, url, targetTenantId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<AbsBotSnapshot?> GetBotAsync(
        string botResourceId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var url = $"{_config.ArmEndpoint}{botResourceId}?api-version={_config.ArmApiVersion}";
        try
        {
            var body = await RequestAsync(HttpMethod.Get, url, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(body, fallbackAppId: null);
        }
        catch (TargetRequestException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // No existing bot ⇒ the planner should plan a create.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AbsBotSnapshot> UpsertBotAsync(AbsBotUpsert request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url =
            $"{_config.ArmEndpoint}/subscriptions/{request.SubscriptionId}/resourceGroups/{request.ResourceGroup}" +
            $"/providers/Microsoft.BotService/botServices/{request.Name}?api-version={_config.ArmApiVersion}";

        var payload = new
        {
            location = request.Location ?? AbsDesiredStateBuilder.DefaultLocation,
            sku = new { name = request.Sku ?? "F0" },
            kind = "azurebot",
            properties = new
            {
                displayName = request.DisplayName ?? request.Name,
                endpoint = request.Endpoint,
                msaAppId = request.MsaAppId,
                msaAppType = "SingleTenant",
                msaAppTenantId = request.TenantId,
                schemaTransformationVersion = "1.3",
            },
        };

        var body = await RequestAsync(HttpMethod.Put, url, payload, request.TenantId, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(body, fallbackAppId: request.MsaAppId)
            ?? new AbsBotSnapshot { BotResourceId = string.Empty, AppId = request.MsaAppId };
    }

    /// <summary>Maps a raw ARM scope probe to an <see cref="AbsScopeAccess"/>.</summary>
    private static AbsScopeAccess ToScopeAccess((int Status, bool Ok, string? Body) probe)
    {
        if (probe.Ok)
        {
            return new AbsScopeAccess { Exists = true, HasReadAccess = true };
        }

        return probe.Status switch
        {
            (int)HttpStatusCode.Forbidden => new AbsScopeAccess { Exists = true, HasReadAccess = false },
            (int)HttpStatusCode.NotFound => new AbsScopeAccess { Exists = false, HasReadAccess = false },
            _ => new AbsScopeAccess { Exists = true, HasReadAccess = false },
        };
    }

    /// <summary>Projects a raw ARM <c>botServices</c> body onto the read-seam snapshot.</summary>
    private static AbsBotSnapshot? ToSnapshot(string? body, string? fallbackAppId)
    {
        if (string.IsNullOrEmpty(body))
        {
            return fallbackAppId is null
                ? null
                : new AbsBotSnapshot { BotResourceId = string.Empty, AppId = fallbackAppId };
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var properties = root.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object
                    ? props
                    : default;

            return new AbsBotSnapshot
            {
                BotResourceId = GetString(root, "id") ?? string.Empty,
                AppId = GetString(properties, "msaAppId") ?? fallbackAppId,
                MsaAppType = GetString(properties, "msaAppType"),
                MessagingEndpoint = GetString(properties, "endpoint"),
                Sku = root.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object
                    ? GetString(sku, "name")
                    : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStringProperty(string? body, string propertyName)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? GetString(document.RootElement, propertyName)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Read-only sibling of <see cref="RequestAsync"/> that returns the status without throwing on a
    /// non-2xx response, so existence/access probes can branch on 403/404. Never sends a body and never
    /// logs the bearer token.
    /// </summary>
    private async Task<(int Status, bool Ok, string? Body)> ProbeAsync(
        HttpMethod method,
        string url,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(method, url, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, response.IsSuccessStatusCode, string.IsNullOrEmpty(text) ? null : text);
    }

    /// <summary>
    /// Acquires an ARM token via the token seam, applies it as a bearer token, sends the request, and
    /// throws a typed <see cref="TargetRequestException"/> on any non-2xx response. An empty token
    /// omits the <c>Authorization</c> header (the HTTP-forward contract). The bearer token is never
    /// logged.
    /// </summary>
    private async Task<string?> RequestAsync(
        HttpMethod method,
        string url,
        object? body,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(method, url, body, targetTenantId, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw TargetRequestException.MapTargetResponseError(
                (int)response.StatusCode,
                text,
                method.Method,
                url,
                ArmAudienceLabel,
                _binaryName);
        }

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        object? body,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var token = await _tokenProvider
            .AcquireTokenSilentAsync(_config.ArmResource, targetTenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, CamelCase);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }
}
