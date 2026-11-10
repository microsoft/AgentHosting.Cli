using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using CoreCluster = Microsoft.Agents.Hosting.Cli.Core.Environments.CoreServicesClusterCategory;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Creates the <see cref="HttpClient"/> the sync library uses for Dataverse Web API calls, attaching a
/// bearer token for the target organization on every request.
/// </summary>
public sealed class AgentHostDataverseHttpClientAccessor : IDataverseHttpClientAccessor, IDisposable
{
    /// <summary>How long one Dataverse request may take before the transport gives up.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly ISyncAuthProvider _authProvider;
    private readonly Func<HttpMessageHandler> _innerHandlerFactory;
    private readonly Lock _gate = new();
    private HttpMessageHandler? _sharedHandler;

    /// <summary>Creates an accessor that authenticates through <paramref name="authProvider"/>.</summary>
    /// <param name="authProvider">Supplies bearer tokens for the Dataverse organization.</param>
    public AgentHostDataverseHttpClientAccessor(ISyncAuthProvider authProvider) : this(authProvider, CreateRedirectRejectingHandler)
    {
    }

    internal AgentHostDataverseHttpClientAccessor(ISyncAuthProvider authProvider, Func<HttpMessageHandler> innerHandlerFactory)
    {
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _innerHandlerFactory = innerHandlerFactory ?? throw new ArgumentNullException(nameof(innerHandlerFactory));
    }

    /// <summary>Creates the transport handler used for Dataverse calls, which never follows redirects.</summary>
    /// <returns>A handler with automatic redirect following disabled.</returns>
    public static HttpMessageHandler CreateRedirectRejectingHandler() => TransportDefaults.CreateRedirectRejectingHandler();

    /// <summary>Sets the Dataverse organization subsequent clients authenticate against.</summary>
    /// <param name="dataverseOrganization">The organization url.</param>
    // Deliberately an instance member: this accessor is injected as a single unit, and callers set the
    // organization on the same object they later create clients from.
#pragma warning disable CA1822
    public void SetDataverseOrganization(Uri dataverseOrganization) =>
        DataverseOperationScope.Set(dataverseOrganization ?? throw new ArgumentNullException(nameof(dataverseOrganization)), DataverseOperationScope.TenantId, DataverseOperationScope.Context);
#pragma warning restore CA1822

    /// <summary>
    /// Creates an authenticated client for the organization the current operation targets.
    ///
    /// The timeout is stated rather than inherited, because a component-heavy push can exceed the
    /// 100 seconds <see cref="HttpClient"/> defaults to, and the resulting cancellation reads as a
    /// transport failure even though the request may have been applied.
    /// </summary>
    /// <returns>A client that attaches a bearer token to every request.</returns>
    public HttpClient CreateClient()
    {
        var target = DataverseOperationScope.Capture()
            ?? throw new InvalidOperationException("The Dataverse organization has not been set. Call SetDataverseOrganization before creating a client.");

        return CreateClient(target);
    }

    internal HttpClient CreateClient(CopilotEnvironmentContext environment) =>
        CreateClient(new DataverseOperationScope.DataverseTarget(environment.DataverseOrganization, environment.TenantId, environment.Context), sdkRequests: false);

    private HttpClient CreateClient(DataverseOperationScope.DataverseTarget target, bool sdkRequests = true)
    {
        DataverseTargeting.RequireTarget(target);
        return new(new BearerTokenHandler(_authProvider, target, target.Organization, new DataverseRetryHandler
        {
            InnerHandler = new NoReplayResponseHandler(SharedHandler(), sdkRequests),
        }), disposeHandler: true) { Timeout = RequestTimeout };
    }

    internal HttpClient CreateGatewayClient(PowerPlatformCloudMap legacyCloud, CoreCluster legacyCluster)
        => CreateGatewayClient(legacyCloud, () => legacyCluster);

    internal HttpClient CreateGatewayClient(PowerPlatformCloudMap legacyCloud, Func<CoreCluster> legacyCluster)
        => new(new DeferredGatewayHandler(this, legacyCloud, legacyCluster, DataverseOperationScope.Capture()), disposeHandler: true) { Timeout = RequestTimeout };

    private BearerTokenHandler CreateGatewayHandler(PowerPlatformCloudMap legacyCloud, Func<CoreCluster> legacyCluster,
        DataverseOperationScope.DataverseTarget? target)
    {
        if (target is not null)
            DataverseTargeting.RequireTarget(target);
        var cloud = target?.Context is { } context ? PowerPlatformCloudMap.ForTarget(context.Target) : legacyCloud;
        var cluster = target?.Context is { } captured ? PowerAppsServiceTarget.ClusterFor(captured) : legacyCluster();
        var audience = AgentHostSyncAuthProvider.IslandAudienceFor(cluster);
        return new BearerTokenHandler(_authProvider, target, audience, SharedHandler(), cloud);
    }

    private sealed class DeferredGatewayHandler(AgentHostDataverseHttpClientAccessor accessor, PowerPlatformCloudMap cloud,
        Func<CoreCluster> cluster, DataverseOperationScope.DataverseTarget? target) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var invoker = new HttpMessageInvoker(accessor.CreateGatewayHandler(cloud, cluster, target));
            return await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the transport shared by every client this accessor created.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _sharedHandler?.Dispose();
            _sharedHandler = null;
        }
    }

    /// <summary>
    /// The transport every client created here sends through.
    ///
    /// One handler is shared because the connection pool lives in the handler: a fresh one per request
    /// means a fresh TLS handshake and socket per request, which a paged read or a component delete
    /// performs many of. Clients stay disposable — <see cref="BearerTokenHandler"/> does not dispose what
    /// it does not own — so callers may keep using <c>using</c>.
    /// </summary>
    private HttpMessageHandler SharedHandler()
    {
        lock (_gate)
        {
            return _sharedHandler ??= _innerHandlerFactory();
        }
    }

    // The SDK retries every 429, including POST, and several write methods on transient errors.
    // Terminate unsafe failures inside both its accessor retry layer and its outer SDK policy.
    private sealed class NoReplayResponseHandler(HttpMessageHandler transport, bool sdkRequests) : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options
                && (int)response.StatusCode >= 400
                && (sdkRequests || response.StatusCode != System.Net.HttpStatusCode.NotFound))
            {
                try
                {
                    if (sdkRequests)
                    {
                        var failure = await DataverseWebApiClient.DescribeFailureWithCodeAsync(response, cancellationToken).ConfigureAwait(false);
                        var body = new JsonObject
                        {
                            ["error"] = new JsonObject { ["code"] = failure.Code, ["message"] = failure.Description },
                        }.ToJsonString();
                        throw new DataverseRequestException(response.StatusCode, body);
                    }
                    throw CopilotStudioException.ServiceError($"{request.Method.Method} {request.RequestUri!.PathAndQuery.TrimStart('/')}",
                        await DataverseWebApiClient.DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    response.Dispose();
                }
            }
            return response;
        }
    }

    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly ISyncAuthProvider _authProvider;
        private readonly Uri _audience;
        private readonly DataverseOperationScope.DataverseTarget? _target;
        private readonly PowerPlatformCloudMap? _gatewayTrust;

        public BearerTokenHandler(ISyncAuthProvider authProvider, DataverseOperationScope.DataverseTarget? target, Uri audience, HttpMessageHandler innerHandler, PowerPlatformCloudMap? gatewayTrust = null) : base(innerHandler)
        {
            _authProvider = authProvider;
            _audience = audience;
            _target = target;
            _gatewayTrust = gatewayTrust;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: true, Fragment.Length: > 0 })
                throw new EndpointTrustException("The Sync destination must not contain a URI fragment.");
            if (_gatewayTrust is not null)
            {
                if (request.RequestUri is null || !_gatewayTrust.IsTrustedAgentManagementGateway(request.RequestUri)
                    || request.RequestUri.Query.Length != 0)
                    throw new EndpointTrustException("The Sync gateway destination is not trusted for the captured authentication target.");
            }
            else if (request.RequestUri is null || !IsSameOrigin(request.RequestUri, _audience))
            {
                throw CopilotStudioException.UntrustedDataverseOrigin(request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "(none)", _audience.GetLeftPart(UriPartial.Authority));
            }

            using var scope = DataverseOperationScope.Push(_target);
            var token = await _authProvider.AcquireTokenAsync(_audience, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new EndpointTrustException("The Sync request returned a redirect; redirect destinations are not trusted.");
            }
            return response;
        }

        protected override void Dispose(bool disposing)
        {
        }

        /// <summary>
        /// Whether a request targets the organization the token was minted for.
        ///
        /// The token is a delegated Dataverse credential. A response can redirect the client elsewhere —
        /// an OData <c>@odata.nextLink</c> is an absolute url the service controls — so the destination is
        /// checked at the point the header is attached rather than trusted from wherever the url came.
        /// </summary>
        private static bool IsSameOrigin(Uri request, Uri audience) =>
            request.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.IsNullOrEmpty(request.UserInfo)
            && request.Host.Equals(audience.Host, StringComparison.OrdinalIgnoreCase)
            && request.Port == audience.Port;
    }
}
