using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Reads the selected Agent from AMS-DP and creates one PackageRevision-selected Deployment through
/// AMS-CMP. Each call validates delegated-token claims before attaching authorization.
/// </summary>
public sealed class AmsDeploymentAdapter : IAmsAgentReadTransport, IAmsDeploymentTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokens;
    private readonly PpapiTokenClaimsValidator _claimsValidator;

    public AmsDeploymentAdapter(
        HttpClient httpClient,
        ITokenProvider tokens,
        PpapiTokenClaimsValidator? claimsValidator = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _claimsValidator = claimsValidator ?? new PpapiTokenClaimsValidator();
    }

    public static HttpClientHandler CreateRedirectRejectingHandler() => new()
    {
        AllowAutoRedirect = false,
        SslProtocols = SslProtocols.Tls13,
    };

    public async Task<AmsAgentReadResult> GetAsync(
        AmsAgentReadTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = BuildAgentUri(request);
        var response = await SendAsync(
            HttpMethod.Get,
            uri,
            request.Endpoint,
            request.Audience,
            request.TenantId,
            [PowerPlatformApiPermissions.AppsRead],
            "Agent read",
            content: null,
            outcomeMayBeIndeterminate: false,
            isDeploymentUpdate: false,
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            return new AmsAgentReadResult
            {
                AgentId = GetRequiredString(root, "id"),
                TenantId = GetRequiredString(root, "tenantId"),
                EnvironmentId = GetRequiredString(root, "environmentId"),
                RepositoryId = GetRequiredString(root, "repositoryId"),
                RepositoryBindingStatus = GetEnum(
                    root,
                    "repositoryBindingStatus",
                    ("NotSpecified", 0),
                    ("Provisioning", 1),
                    ("Bound", 2),
                    ("Failed", 3)),
                AgentType = GetEnum(root, "agentType", ("CodeAgent", 0)),
                Language = GetEnum(
                    root,
                    "language",
                    ("NotSpecified", 0),
                    ("DotNet", 1),
                    ("Python", 2),
                    ("Node", 3)),
                LanguageVersion = GetRequiredString(root, "languageVersion"),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw AgentDeployException.ServiceRejected(
                "The Agent read returned a success status without a valid bound Agent response.",
                ExitCodes.Failure,
                "invalid-ams-response",
                "Do not upload a package. Confirm that the target implements the v1 Agent contract.",
                ex);
        }
    }

    public async Task<AmsDeploymentCurrentResult> GetAsync(
        AmsDeploymentReadTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await SendAsync(
            HttpMethod.Get,
            BuildExistingDeploymentUri(request),
            request.Endpoint,
            request.Audience,
            request.TenantId,
            [PowerPlatformApiPermissions.AppsUpdate, PowerPlatformApiPermissions.AppsRead],
            "Deployment read before update",
            content: null,
            outcomeMayBeIndeterminate: false,
            isDeploymentUpdate: true,
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var packageRevisionId = GetRequiredString(root, "packageRevisionId");
            if (!Guid.TryParse(packageRevisionId, out var revision) || revision == Guid.Empty)
            {
                throw new FormatException("packageRevisionId is invalid.");
            }

            return new AmsDeploymentCurrentResult
            {
                ClientRequestId = response.ClientRequestId,
                ServiceRequestId = response.ServiceRequestId,
                DeploymentId = GetRequiredString(root, "id"),
                TenantId = GetRequiredString(root, "tenantId"),
                HomeEnvironmentId = GetRequiredString(root, "homeEnvironmentId"),
                AgentId = GetRequiredString(root, "agentId"),
                PackageRevisionId = packageRevisionId,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw AgentDeployException.DeploymentUpdateInvalidResponse(
                mutationAttempted: false,
                clientRequestId: response.ClientRequestId,
                serviceRequestId: response.ServiceRequestId,
                innerException: ex);
        }
    }

    public async Task<AmsDeploymentCreateResult> CreateAsync(
        AmsDeploymentCreateTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = BuildDeploymentUri(request);
        using var content = JsonContent.Create(
            new DeploymentCreateWireRequest
            {
                AgentType = 0,
                PackageRevisionId = request.PackageRevisionId,
                DeploymentType = MapDeploymentType(request.DeploymentType),
                DeploymentName = request.DeploymentName,
                EnvironmentId = request.TargetEnvironmentId,
                Audience = request.EntraId,
                EntraAppIdType = MapEntraIdType(request.EntraIdType),
                Language = MapLanguage(request.Language),
                LanguageVersion = request.LanguageVersion,
                Endpoint = null,
                IsQuarantined = false,
            },
            options: RequestJson);
        var response = await SendAsync(
            HttpMethod.Post,
            uri,
            request.Endpoint,
            request.Audience,
            request.TenantId,
            [PowerPlatformApiPermissions.AppsUpdate],
            "Deployment create",
            content,
            outcomeMayBeIndeterminate: true,
            isDeploymentUpdate: false,
            cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseDeployment(response);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw AgentDeployException.ServiceRejected(
                AgentDeployException.AddCorrelation(
                    "The Deployment create returned a success status without a valid persisted Deployment.",
                    response.ClientRequestId,
                    response.ServiceRequestId),
                ExitCodes.Failure,
                "invalid-ams-response",
                "Do not treat the Deployment as confirmed. Inspect the service correlation before any retry.",
                ex);
        }
    }

    public async Task<AmsDeploymentCreateResult> UpdateAsync(
        AmsDeploymentUpdateTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var content = JsonContent.Create(
            new DeploymentUpdateWireRequest
            {
                PackageRevisionId = request.PackageRevisionId,
                ExpectedPackageRevisionId = request.ExpectedPackageRevisionId,
            },
            options: RequestJson);
        var response = await SendAsync(
            HttpMethod.Patch,
            BuildExistingDeploymentUri(request),
            request.Endpoint,
            request.Audience,
            request.TenantId,
            [PowerPlatformApiPermissions.AppsUpdate],
            "Deployment update",
            content,
            outcomeMayBeIndeterminate: true,
            isDeploymentUpdate: true,
            cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseDeployment(response);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw AgentDeployException.DeploymentUpdateInvalidResponse(
                mutationAttempted: true,
                clientRequestId: response.ClientRequestId,
                serviceRequestId: response.ServiceRequestId,
                innerException: ex);
        }
    }

    private async Task<AmsResponse> SendAsync(
        HttpMethod method,
        Uri uri,
        Uri configuredEndpoint,
        string audience,
        string tenantId,
        IReadOnlyList<string> requiredPermissions,
        string phase,
        HttpContent? content,
        bool outcomeMayBeIndeterminate,
        bool isDeploymentUpdate,
        CancellationToken cancellationToken)
    {
        var clientRequestId = Guid.NewGuid().ToString("D");
        var token = await _tokens
            .AcquireTokenSilentAsync(audience, tenantId, cancellationToken)
            .ConfigureAwait(false);
        var claims = _claimsValidator.Validate(
            token.AccessToken,
            audience,
            tenantId,
            requiredPermissions[0]);
        foreach (var requiredPermission in requiredPermissions.Skip(1))
        {
            _claimsValidator.Validate(claims, audience, tenantId, requiredPermission);
        }

        using var message = new HttpRequestMessage(method, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("x-ms-client-request-id", clientRequestId);
        message.Content = content;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw isDeploymentUpdate
                ? AgentDeployException.DeploymentUpdateTimedOut(
                    phase,
                    clientRequestId,
                    outcomeMayBeIndeterminate,
                    ex)
                : AgentDeployException.TimedOut(
                    phase,
                    clientRequestId,
                    outcomeMayBeIndeterminate,
                    ex);
        }
        catch (HttpRequestException ex)
        {
            throw isDeploymentUpdate
                ? AgentDeployException.DeploymentUpdateTransportFailed(
                    phase,
                    clientRequestId,
                    outcomeMayBeIndeterminate,
                    ex)
                : AgentDeployException.TransportFailed(
                    phase,
                    clientRequestId,
                    outcomeMayBeIndeterminate,
                    ex);
        }

        using (response)
        {
            var serviceRequestId = ReadServiceRequestId(response);
            if (IsRedirect(response.StatusCode))
            {
                throw isDeploymentUpdate
                    ? AgentDeployException.DeploymentUpdateRedirectRejected(
                        phase,
                        (int)response.StatusCode,
                        configuredEndpoint.Host,
                        outcomeMayBeIndeterminate,
                        clientRequestId,
                        serviceRequestId)
                    : AgentDeployException.RedirectRejected(
                        phase,
                        (int)response.StatusCode,
                        configuredEndpoint.Host);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw isDeploymentUpdate
                    ? AgentDeployException.DeploymentUpdateTimedOut(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex,
                        serviceRequestId)
                    : AgentDeployException.TimedOut(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex);
            }
            catch (InvalidOperationException ex)
            {
                throw isDeploymentUpdate
                    ? AgentDeployException.DeploymentUpdateTransportFailed(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex,
                        serviceRequestId)
                    : AgentDeployException.TransportFailed(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex);
            }
            catch (HttpRequestException ex)
            {
                throw isDeploymentUpdate
                    ? AgentDeployException.DeploymentUpdateTransportFailed(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex,
                        serviceRequestId)
                    : AgentDeployException.TransportFailed(
                        $"{phase} response read",
                        clientRequestId,
                        outcomeMayBeIndeterminate,
                        ex);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw MapFailure(
                    (int)response.StatusCode,
                    responseBody,
                    method,
                    uri,
                    phase,
                    isDeploymentUpdate,
                    outcomeMayBeIndeterminate,
                    clientRequestId,
                    serviceRequestId);
            }

            return new AmsResponse(responseBody, clientRequestId, serviceRequestId);
        }
    }

    private static Uri BuildAgentUri(AmsAgentReadTransportRequest request)
    {
        var relativePath =
            $"agenthosting/tenants/{Uri.EscapeDataString(request.TenantId)}/" +
            $"environments/{Uri.EscapeDataString(request.EnvironmentId)}/" +
            $"agents/{Uri.EscapeDataString(request.AgentId)}?api-version=1";
        return new Uri(EnsureTrailingSlash(request.Endpoint), relativePath);
    }

    private static Uri BuildDeploymentUri(AmsDeploymentCreateTransportRequest request)
    {
        var relativePath =
            $"agenthosting/environments/{Uri.EscapeDataString(request.HomeEnvironmentId)}/" +
            $"agents/{Uri.EscapeDataString(request.AgentId)}/deployments?api-version=1";
        return new Uri(EnsureTrailingSlash(request.Endpoint), relativePath);
    }

    private static Uri BuildExistingDeploymentUri(AmsDeploymentReadTransportRequest request)
    {
        var relativePath =
            $"agenthosting/environments/{Uri.EscapeDataString(request.HomeEnvironmentId)}/" +
            $"agents/{Uri.EscapeDataString(request.AgentId)}/deployments/" +
            $"{Uri.EscapeDataString(request.DeploymentId)}?api-version=1";
        return new Uri(EnsureTrailingSlash(request.Endpoint), relativePath);
    }

    private static AgentDeployException MapFailure(
        int status,
        string body,
        HttpMethod method,
        Uri uri,
        string phase,
        bool isDeploymentUpdate,
        bool mutationAttempted,
        string clientRequestId,
        string? serviceRequestId)
    {
        var (code, _) = TargetRequestException.ParseTargetError(body);
        if (isDeploymentUpdate &&
            status == (int)HttpStatusCode.Conflict &&
            method == HttpMethod.Patch &&
            string.Equals(code, "AgentConcurrencyConflict", StringComparison.OrdinalIgnoreCase))
        {
            return AgentDeployException.ServiceRejected(
                AgentDeployException.AddCorrelation(
                    "The deployment changed while this update was in progress.",
                    clientRequestId,
                    serviceRequestId),
                ExitCodes.Failure,
                "agent-deployment-concurrency-conflict",
                "Run `msagent deployment update` again if you still want to apply this package to the current deployment.");
        }

        if (isDeploymentUpdate &&
            string.Equals(code, "AgentPackageRevisionNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return AgentDeployException.ServiceRejected(
                AgentDeployException.AddCorrelation(
                    "The requested package revision was not found for this Agent.",
                    clientRequestId,
                    serviceRequestId),
                ExitCodes.Usage,
                "package-revision-not-found",
                "Use the package revision returned by `msagent agent package upload` for this same Agent.");
        }

        var mapped = TargetRequestException.MapTargetResponseError(
            status,
            body,
            method.Method,
            uri.ToString(),
            $"Agent Hosting {phase}",
            "msagent");
        var outcome = isDeploymentUpdate
            ? mutationAttempted
                ? " The deployment outcome may be indeterminate."
                : " No update request was sent."
            : string.Empty;
        return AgentDeployException.ServiceRejected(
            AgentDeployException.AddCorrelation(
                mapped.Message + outcome,
                clientRequestId,
                serviceRequestId),
            mapped.ExitCode,
            mapped.ErrorKind ?? "request-failed",
            mapped.Remediation ?? "Inspect the service response using the correlation identifier.");
    }

    private static AmsDeploymentCreateResult ParseDeployment(AmsResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        return new AmsDeploymentCreateResult
        {
            ClientRequestId = response.ClientRequestId,
            ServiceRequestId = response.ServiceRequestId,
            DeploymentId = GetRequiredString(root, "id"),
            AgentId = GetRequiredString(root, "agentId"),
            TenantId = GetRequiredString(root, "tenantId"),
            HomeEnvironmentId = GetRequiredString(root, "homeEnvironmentId"),
            TargetEnvironmentId = GetRequiredString(root, "environmentId"),
            PackageRevisionId = GetRequiredString(root, "packageRevisionId"),
            PackageSha256 = GetRequiredString(root, "packageSha256").ToLowerInvariant(),
            PackageSizeInBytes = GetRequiredInt64(root, "packageSizeInBytes"),
            AgentType = GetEnum(root, "agentType", ("CodeAgent", 0)),
            DeploymentName = GetRequiredString(root, "deploymentName"),
            DeploymentType = GetEnum(
                root,
                "deploymentType",
                ("Dev", 0),
                ("Test", 1),
                ("Prod", 2)),
            EntraId = GetRequiredString(root, "entraAppId"),
            EntraIdType = GetEnum(
                root,
                "entraAppIdType",
                ("AppReg", 0),
                ("Blueprint", 1),
                ("Default", 2)),
            Language = GetEnum(
                root,
                "language",
                ("NotSpecified", 0),
                ("DotNet", 1),
                ("Python", 2),
                ("Node", 3)),
            LanguageVersion = GetRequiredString(root, "languageVersion"),
            RuntimeEndpoint = GetRequiredString(root, "endpoint"),
            IsCustomEndpoint = GetRequiredBoolean(root, "isCustomEndpoint"),
            IsQuarantined = GetRequiredBoolean(root, "isQuarantined"),
        };
    }

    private static int MapDeploymentType(string value) => value switch
    {
        "Dev" => 0,
        "Test" => 1,
        "Prod" => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static int MapEntraIdType(string value) => value switch
    {
        "AppReg" => 0,
        "Blueprint" => 1,
        "Default" => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string MapLanguage(string value) => value switch
    {
        "DotNet" or "Python" or "Node" => value,
        _ => throw AgentDeployException.AgentNotCompatible(
            $"The selected Agent language '{value}' is not supported by the concrete deploy contract."),
    };

    private static string GetRequiredString(JsonElement root, string name)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new FormatException($"{name} is missing.");
        }

        return value.GetString()!;
    }

    private static long GetRequiredInt64(JsonElement root, string name)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new FormatException($"{name} is missing.");
        }

        return result;
    }

    private static bool GetRequiredBoolean(JsonElement root, string name)
    {
        var value = GetRequiredProperty(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{name} is invalid."),
        };
    }

    private static string GetEnum(
        JsonElement root,
        string name,
        params (string Name, int Value)[] values)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            var named = values.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, text, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(named.Name))
            {
                return named.Name;
            }
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            var numbered = values.FirstOrDefault(candidate => candidate.Value == numeric);
            if (!string.IsNullOrWhiteSpace(numbered.Name))
            {
                return numbered.Name;
            }
        }

        throw new FormatException($"{name} is invalid.");
    }

    private static JsonElement GetRequiredProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new FormatException($"{name} is missing.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string? ReadServiceRequestId(HttpResponseMessage response) =>
        ServiceCorrelation.ReadServiceRequestId(response);

    private static Uri EnsureTrailingSlash(Uri endpoint) =>
        endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

    private sealed record AmsResponse(string Body, string ClientRequestId, string? ServiceRequestId);

    private sealed record DeploymentCreateWireRequest
    {
        public required int AgentType { get; init; }
        public required string PackageRevisionId { get; init; }
        public required int DeploymentType { get; init; }
        public required string DeploymentName { get; init; }
        public required string EnvironmentId { get; init; }
        public required string Audience { get; init; }
        public required int EntraAppIdType { get; init; }
        public required string Language { get; init; }
        public required string LanguageVersion { get; init; }
        public string? Endpoint { get; init; }
        public bool IsQuarantined { get; init; }
    }

    private sealed record DeploymentUpdateWireRequest
    {
        public required string PackageRevisionId { get; init; }
        public required string ExpectedPackageRevisionId { get; init; }
    }
}
