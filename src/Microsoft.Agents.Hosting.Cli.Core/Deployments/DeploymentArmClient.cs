using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Shared ARM transport for the deployment module's per-subgroup adapters. It owns the injectable
/// <see cref="HttpClient"/> and the abstract <see cref="ITokenProvider"/> seam — no MSAL/Azure/Graph
/// type — applies the bearer token to the <c>Authorization</c> header (never logged), serializes
/// camelCase request bodies, and turns non-2xx responses into typed, secret-free
/// <see cref="TargetRequestException"/>s. It also implements the shared subscription resolver used by
/// OAuth provider discovery and exposes subscription/resource-group resolution to the deployment
/// transport without duplicating listing or pagination logic.
///
/// Subclasses — <see cref="DeploymentAdapter"/> (bot lifecycle), <see cref="DeploymentOAuthAdapter"/>
/// (OAuth connections), and future subgroups such as channels — extend this class to implement only
/// their own API calls on top of the shared <see cref="RequestAsync"/> plumbing. The whole surface is
/// used only by the deployment module, so it pins its own <c>Microsoft.BotService</c> api-version via
/// <see cref="DeploymentTargetConfig"/>, independent of the <c>msagent abs</c> feature. A future AMS
/// implementation should implement the deployment or OAuth port directly rather than inherit this
/// direct-ARM transport. A fake <see cref="HttpMessageHandler"/> drives it fully offline in tests,
/// keeping the host's offline-guarantee test green.
/// </summary>
public abstract class DeploymentArmClient
{
    // Human-readable target label used only in error diagnostics (not an Azure coordinate).
    private const string ArmAudienceLabel = "ARM";

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly string _binaryName;

    /// <summary>The ARM endpoint and api-versions the subclasses build their request URLs from.</summary>
    protected DeploymentTargetConfig Config { get; }

    /// <summary>Creates the shared client.</summary>
    /// <param name="httpClient">The transport; a fake handler drives it offline in tests.</param>
    /// <param name="tokenProvider">The bearer-token seam, kept free of MSAL/Azure types.</param>
    /// <param name="config">The ARM target; defaults to Azure public cloud.</param>
    /// <param name="binaryName">The CLI name surfaced in error remediation text.</param>
    protected DeploymentArmClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        DeploymentTargetConfig? config = null,
        string binaryName = "msagent")
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        Config = config ?? DeploymentTargetConfig.AzurePublic;
        _binaryName = string.IsNullOrWhiteSpace(binaryName) ? "msagent" : binaryName;
    }

    public async Task<AzureSubscriptionInfo> ResolveSubscriptionAsync(
        string? preferredSubscriptionId,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        // Follow ARM pagination so an explicit --subscription on a later page is not reported missing and
        // auto-select does not wrongly land on the first page's sole result.
        var subscriptions = new List<AzureSubscriptionInfo>();
        var next = $"{Config.ArmEndpoint}/subscriptions?api-version={Config.SubscriptionsApiVersion}";
        while (!string.IsNullOrEmpty(next))
        {
            var body = await RequestAsync(HttpMethod.Get, next, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);
            subscriptions.AddRange(ParseSubscriptions(body));
            next = ParseNextLink(body);
        }

        if (subscriptions.Count == 0)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                "No accessible Azure subscriptions were found for the signed-in account.",
                "Sign in with `msagent auth login` to an account that can access a subscription, or pass --subscription.");
        }

        if (!string.IsNullOrWhiteSpace(preferredSubscriptionId))
        {
            var wanted = preferredSubscriptionId.Trim();
            var chosen = subscriptions.FirstOrDefault(s =>
                string.Equals(s.SubscriptionId, wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                throw new ProvisioningException(
                    ProvisioningErrorKind.MissingContext,
                    $"Subscription '{wanted}' was not found among the subscriptions the signed-in account can access.",
                    "Pass a --subscription id the signed-in account can access, or omit it to auto-select.");
            }

            return chosen;
        }

        if (subscriptions.Count > 1)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.AmbiguousContext,
                "The signed-in account can access more than one Azure subscription.",
                "Pass --subscription to choose which subscription to provision the bot into.");
        }

        return subscriptions[0];
    }

    public async Task<AzureResourceGroupInfo> ResolveResourceGroupAsync(
        string subscriptionId,
        string? preferredResourceGroup,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            throw new ArgumentException("A subscription id is required to resolve a resource group.", nameof(subscriptionId));
        }

        // Follow ARM pagination so an explicit --resource-group on a later page is not reported missing and
        // auto-select does not wrongly land on the first page's sole result.
        var resourceGroups = new List<AzureResourceGroupInfo>();
        var next = $"{Config.ArmEndpoint}/subscriptions/{Uri.EscapeDataString(subscriptionId.Trim())}/resourcegroups" +
            $"?api-version={Config.ResourceGroupsApiVersion}";
        while (!string.IsNullOrEmpty(next))
        {
            var body = await RequestAsync(HttpMethod.Get, next, body: null, targetTenantId, cancellationToken).ConfigureAwait(false);
            resourceGroups.AddRange(ParseResourceGroups(body));
            next = ParseNextLink(body);
        }

        if (resourceGroups.Count == 0)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                "No resource groups were found in the subscription for the signed-in account.",
                "Create a resource group, then pass --resource-group to `msagent deployment create`.");
        }

        if (!string.IsNullOrWhiteSpace(preferredResourceGroup))
        {
            var wanted = preferredResourceGroup.Trim();
            var chosen = resourceGroups.FirstOrDefault(g =>
                string.Equals(g.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                throw new ProvisioningException(
                    ProvisioningErrorKind.MissingContext,
                    $"Resource group '{wanted}' was not found among the resource groups the signed-in account can access in the subscription.",
                    "Pass a --resource-group the signed-in account can access, or omit it to auto-select.");
            }

            return chosen;
        }

        if (resourceGroups.Count > 1)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.AmbiguousContext,
                "The subscription contains more than one resource group.",
                "Pass --resource-group to choose which resource group to provision the bot into.");
        }

        return resourceGroups[0];
    }

    /// <summary>Issues an ARM request and returns the response body, mapping non-2xx to a typed error.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="body">The request body to serialize as camelCase JSON, or null.</param>
    /// <param name="targetTenantId">The tenant the bearer token is acquired for, or null for home.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    protected async Task<string> RequestAsync(
        HttpMethod method,
        string url,
        object? body,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(method, url, body, targetTenantId, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw TargetRequestException.MapTargetResponseError(
                (int)response.StatusCode,
                content,
                method.Method,
                url,
                ArmAudienceLabel,
                _binaryName);
        }

        return content;
    }

    /// <summary>Reads a string property from a JSON object, or null when absent or not a string.</summary>
    /// <param name="element">The JSON element to read from.</param>
    /// <param name="propertyName">The property name to read.</param>
    protected static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>Reads a scalar property as text, or null when absent or not a scalar.</summary>
    protected static string? GetScalar(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Parses a response body and requires a JSON object root.</summary>
    protected static JsonElement ParseObject(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw InvalidResponse("The response is not a valid JSON object");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw InvalidResponse("The response is not a valid JSON object");
        }
    }

    protected static JsonElement.ArrayEnumerator ParseArray(string? body)
    {
        var root = ParseObject(body);
        return GetArray(root, "value", isOptional: false)!.Value.EnumerateArray();
    }

    /// <summary>Gets an object member, optionally accepting an absent or null value.</summary>
    protected static JsonElement? GetObject(JsonElement root, string member, bool isOptional)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                $"The response item must be a JSON object, but was {root.ValueKind}");
        }

        if (!root.TryGetProperty(member, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            if (isOptional)
            {
                return null;
            }

            throw InvalidResponse($"'{member}' must be a JSON object");
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            var expectedShape = isOptional ? "a JSON object or null" : "a JSON object";
            throw InvalidResponse($"'{member}' must be {expectedShape}");
        }

        return value;
    }

    /// <summary>Gets an array member, optionally accepting an absent or null value.</summary>
    protected static JsonElement? GetArray(JsonElement root, string member, bool isOptional)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse(
                $"The response item must be a JSON object, but was {root.ValueKind}");
        }

        if (!root.TryGetProperty(member, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            if (isOptional)
            {
                return null;
            }

            throw InvalidResponse($"'{member}' must be a JSON array");
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            var expectedShape = isOptional ? "a JSON array or null" : "a JSON array";
            throw InvalidResponse($"'{member}' must be {expectedShape}");
        }

        return value;
    }

    /// <summary>Reads the ARM <c>nextLink</c> pagination cursor from a list response, or null.</summary>
    /// <param name="body">The response body.</param>
    protected static string? ParseNextLink(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return GetString(document.RootElement, "nextLink");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static ProvisioningException InvalidResponse(string detail) =>
        new(
            ProvisioningErrorKind.InvalidResponse,
            $"The Azure Bot Service response is invalid: {detail}.",
            "Retry; if it persists, verify the Microsoft.BotService api-version and report the issue.");

    private static List<AzureSubscriptionInfo> ParseSubscriptions(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<AzureSubscriptionInfo>();
            foreach (var element in value.EnumerateArray())
            {
                var subscriptionId = GetString(element, "subscriptionId");
                var tenantId = GetString(element, "tenantId");
                if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(tenantId))
                {
                    continue;
                }

                // Skip disabled/warned subscriptions so auto-selection only lands on a usable one.
                var state = GetString(element, "state");
                if (!string.IsNullOrEmpty(state) && !string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new AzureSubscriptionInfo
                {
                    SubscriptionId = subscriptionId,
                    TenantId = tenantId,
                    DisplayName = GetString(element, "displayName"),
                });
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<AzureResourceGroupInfo> ParseResourceGroups(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<AzureResourceGroupInfo>();
            foreach (var element in value.EnumerateArray())
            {
                var name = GetString(element, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                results.Add(new AzureResourceGroupInfo
                {
                    Name = name,
                    Location = GetString(element, "location"),
                });
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        object? body,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        var requestUri = ValidateArmRequestUri(url);
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var token = await _tokenProvider
            .AcquireTokenSilentAsync(Config.ArmResource, targetTenantId, cancellationToken)
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

    private Uri ValidateArmRequestUri(string url)
    {
        if (!Uri.TryCreate(Config.ArmEndpoint, UriKind.Absolute, out var armEndpoint) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var requestUri) ||
            !string.Equals(armEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            requestUri.UserInfo.Length != 0 ||
            Uri.Compare(
                armEndpoint,
                requestUri,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new ProvisioningException(
                ProvisioningErrorKind.InvalidResponse,
                "The Azure Resource Manager request target is invalid.",
                "Verify the deployment metadata and ARM endpoint configuration, then retry.");
        }

        return requestUri;
    }
}
