using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using System.Net.Http.Headers;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

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
        DataverseOperationScope.Set(dataverseOrganization ?? throw new ArgumentNullException(nameof(dataverseOrganization)), DataverseOperationScope.TenantId);
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
        var organization = DataverseOperationScope.Organization
            ?? throw new InvalidOperationException("The Dataverse organization has not been set. Call SetDataverseOrganization before creating a client.");

        return new HttpClient(new BearerTokenHandler(_authProvider, organization, new DataverseRetryHandler { InnerHandler = SharedHandler() }), disposeHandler: true) { Timeout = RequestTimeout };
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

    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly ISyncAuthProvider _authProvider;
        private readonly Uri _audience;

        public BearerTokenHandler(ISyncAuthProvider authProvider, Uri audience, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _authProvider = authProvider;
            _audience = audience;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null || !IsSameOrigin(request.RequestUri, _audience))
            {
                throw CopilotStudioException.UntrustedDataverseOrigin(request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "(none)", _audience.GetLeftPart(UriPartial.Authority));
            }

            var token = await _authProvider.AcquireTokenAsync(_audience, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
