using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>The deployment ring whose fixed PPAPI coordinates the host selects.</summary>
public enum PowerPlatformApiRing
{
    /// <summary>FirstRelease and production PPAPI.</summary>
    Production,

    /// <summary>The non-production PreProd PPAPI ring.</summary>
    Preprod,

    /// <summary>The non-production Test PPAPI ring.</summary>
    Test,
}

/// <summary>
/// Power Platform API coordinates for one cloud: the global endpoint the environment inventory is read
/// from, the token audience, and the inventory api-version. Keeping them in one record means the
/// adapter never repeats an endpoint or version string and a sovereign target only swaps this value.
///
/// The api-version is centralized here so every environment request uses the same explicit contract
/// and another cloud can replace the complete target configuration as one value.
/// </summary>
public sealed record PowerPlatformApiTargetConfig
{
    /// <summary>The identity cloud used to acquire tokens for this target.</summary>
    public required CloudInstance Cloud { get; init; }

    /// <summary>The fixed PPAPI deployment ring.</summary>
    public required PowerPlatformApiRing Ring { get; init; }

    /// <summary>The Power Platform API global endpoint (no trailing slash).</summary>
    public required Uri GlobalEndpoint { get; init; }

    /// <summary>The Power Platform API token audience.</summary>
    public required string Audience { get; init; }

    /// <summary>The environment-management api-version.</summary>
    public required string EnvironmentsApiVersion { get; init; }

    /// <summary>Public-cloud defaults.</summary>
    public static PowerPlatformApiTargetConfig Public { get; } = new()
    {
        Cloud = CloudInstance.Public,
        Ring = PowerPlatformApiRing.Production,
        GlobalEndpoint = new Uri("https://api.powerplatform.com"),
        Audience = "https://api.powerplatform.com/",
        EnvironmentsApiVersion = "2022-03-01-preview",
    };

    /// <summary>PreProd defaults.</summary>
    public static PowerPlatformApiTargetConfig Preprod { get; } = new()
    {
        Cloud = CloudInstance.Public,
        Ring = PowerPlatformApiRing.Preprod,
        GlobalEndpoint = new Uri("https://api.preprod.powerplatform.com"),
        Audience = "https://api.preprod.powerplatform.com",
        EnvironmentsApiVersion = "2022-03-01-preview",
    };

    /// <summary>Test defaults.</summary>
    public static PowerPlatformApiTargetConfig Test { get; } = new()
    {
        Cloud = CloudInstance.Public,
        Ring = PowerPlatformApiRing.Test,
        GlobalEndpoint = new Uri("https://api.test.powerplatform.com"),
        Audience = "https://api.test.powerplatform.com",
        EnvironmentsApiVersion = "2022-03-01-preview",
    };
}

/// <summary>
/// The live <see cref="IEnvironmentClient"/>: reads the environment inventory from the Power Platform
/// API over raw HTTP.
///
/// It lives in the core assembly for the same reason <c>ArmAbsProvisioningAdapter</c> does — it needs
/// no MSAL or Azure SDK type, only an injected <see cref="HttpClient"/> and the abstract
/// <see cref="ITokenProvider"/> seam — so the host's offline guarantee holds and a fake
/// <see cref="HttpMessageHandler"/> drives it entirely offline in tests.
///
/// Two properties matter here beyond "it makes a GET":
/// <list type="bullet">
///   <item>the token is acquired <em>silently</em>, so listing environments can never open identity
///   UI, which is what makes <c>--non-interactive</c> safe on this path;</item>
///   <item>the global endpoint and every endpoint the response advertises are checked against the
///   <see cref="PowerPlatformCloudMap"/>, so an inventory response cannot nominate a host the CLI
///   would then send a bearer token to.</item>
/// </list>
/// </summary>
public sealed class PowerPlatformApiEnvironmentClient : IEnvironmentClient
{
    private const string AudienceLabel = "Power Platform API";

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokenProvider;
    private readonly PowerPlatformCloudMap _cloudMap;
    private readonly PowerPlatformApiTargetConfig _config;
    private readonly PpapiScopeBuilder _scopeBuilder;
    private readonly PpapiTokenClaimsValidator _claimsValidator;
    private readonly string _binaryName;

    public PowerPlatformApiEnvironmentClient(
        HttpClient httpClient,
        ITokenProvider tokenProvider,
        PowerPlatformApiTargetConfig? config = null,
        string binaryName = "msagent",
        PpapiTokenClaimsValidator? claimsValidator = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _config = config ?? PowerPlatformApiTargetConfig.Public;
        _cloudMap = PowerPlatformCloudMap.ForTarget(_config);
        _scopeBuilder = new PpapiScopeBuilder();
        _claimsValidator = claimsValidator ?? new PpapiTokenClaimsValidator();
        _binaryName = string.IsNullOrWhiteSpace(binaryName) ? "msagent" : binaryName;
    }

    /// <summary>
    /// The exact scope the client requests for the inventory call, exposed so scope construction is
    /// assertable without issuing a request.
    /// </summary>
    public string InventoryScope => _scopeBuilder.DefaultScope(_config.Audience);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var globalEndpoint = _cloudMap.RequireTrusted(_config.GlobalEndpoint, "Power Platform API global");

        var url =
            $"{globalEndpoint.ToString().TrimEnd('/')}/powerapps/environments" +
            $"?api-version={_config.EnvironmentsApiVersion}";

        // Silent-only: this path must never open identity UI, which is what lets `msagent env list` run
        // under --non-interactive.
        var token = await _tokenProvider
            .AcquireTokenSilentAsync(_config.Audience, targetTenantId: null, cancellationToken)
            .ConfigureAwait(false);

        // Fail fast on a token the service would reject anyway. There is no tenant to bind to here:
        // the inventory is what discovers tenants, so this call runs before any tenant is known.
        var claims = _claimsValidator.ValidateWithoutTenantBinding(
            token.AccessToken,
            _config.Audience,
            PowerPlatformApiPermissions.EnvironmentsRead);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The shared mapper keeps the bearer token and the response body out of the message.
            throw TargetRequestException.MapTargetResponseError(
                (int)response.StatusCode,
                body,
                HttpMethod.Get.Method,
                url,
                AudienceLabel,
                _binaryName);
        }

        return Parse(body, claims.TenantId);
    }

    private List<PowerPlatformEnvironment> Parse(string body, string? tokenTenantId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // The parser message quotes the offending bytes and their position, so it is response-body
            // content and does not belong in operator-facing output.
            throw new TargetRequestException(
                $"The {AudienceLabel} environment inventory response was not valid JSON.",
                ExitCodes.Failure,
                TargetErrorKind.RequestFailed,
                status: 200);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("value", out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                throw new TargetRequestException(
                    $"The {AudienceLabel} environment inventory response did not contain an environment array.",
                    ExitCodes.Failure,
                    TargetErrorKind.RequestFailed,
                    status: 200);
            }

            var environments = new List<PowerPlatformEnvironment>();
            foreach (var element in values.EnumerateArray())
            {
                var environment = ToEnvironment(element, tokenTenantId);
                if (environment is not null)
                {
                    environments.Add(environment);
                }
            }

            return environments;
        }
    }

    private PowerPlatformEnvironment? ToEnvironment(JsonElement element, string? tokenTenantId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var environmentId = ReadString(element, "name") ?? ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            return null;
        }

        if (!ServiceIdentifier.IsWellFormedResource(environmentId))
        {
            throw new EndpointTrustException(
                "The environment inventory contained an environment identifier that is not a bounded identifier.",
                remediation: "Re-run `msagent env list` to refresh the environment inventory; if the identifier is still malformed the inventory response cannot be used.");
        }

        var properties = element.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object
                ? propertiesElement
                : element;

        var displayName = ReadString(properties, "displayName") ?? environmentId;

        var tenantId = ReadString(properties, "tenantId") ?? tokenTenantId ?? string.Empty;

        if (tenantId.Length > 0 && !ServiceIdentifier.IsGuid(tenantId))
        {
            throw new EndpointTrustException(
                $"Environment '{environmentId}' advertised a tenant identifier that is not a GUID.",
                remediation: "Re-run `msagent env list` to refresh the environment inventory.");
        }

        var rawEndpoint = ReadString(properties, "apiEndpoint")
            ?? ReadString(properties, "environmentApiEndpoint");

        // An entry that does not advertise its own endpoint is addressed through the global endpoint;
        // either way the endpoint is trusted only after the cloud map accepts it. The rejected value is
        // never echoed: it is unbounded, server-controlled text that can carry query material.
        var endpoint = string.IsNullOrWhiteSpace(rawEndpoint)
            ? _config.GlobalEndpoint
            : Uri.TryCreate(rawEndpoint.Trim(), UriKind.Absolute, out var parsed)
                ? parsed
                : throw new EndpointTrustException(
                    $"Environment '{environmentId}' advertised an endpoint that is not an absolute uri.",
                    remediation: "Re-run `msagent env list` to refresh the environment inventory; if the endpoint is still malformed the inventory response cannot be used.");

        return new PowerPlatformEnvironment
        {
            EnvironmentId = environmentId,
            DisplayName = displayName,
            TenantId = tenantId,
            ApiEndpoint = _cloudMap.RequireTrusted(endpoint, $"environment '{environmentId}'"),
            ApiAudience = _config.Audience,
            InstanceUrl = ReadLinkedInstanceUrl(properties)
                ?? ReadString(properties, "url")
                ?? ReadString(properties, "instanceUrl"),
            AgentManagementUrl = ReadRuntimeEndpoint(properties, "microsoft.PowerVirtualAgents"),
        };
    }

    /// <summary>
    /// Reads one of the service endpoints the environment advertises. 
    /// </summary>
    private static string? ReadRuntimeEndpoint(JsonElement properties, string serviceName) =>
        properties.TryGetProperty("runtimeEndpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Object
            ? ReadString(endpoints, serviceName)
            : null;

    /// <summary>
    /// Reads the linked Dataverse organization url the environment advertises. Absent for environments
    /// with no linked Dataverse instance, which is not an error.
    /// </summary>
    private static string? ReadLinkedInstanceUrl(JsonElement properties) =>
        properties.TryGetProperty("linkedEnvironmentMetadata", out var linked) &&
        linked.ValueKind == JsonValueKind.Object
            ? ReadString(linked, "instanceUrl")
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
