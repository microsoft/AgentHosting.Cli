using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
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
    public static readonly TimeSpan RequestTimeout = OriginGuardedDataverseHttpClientAccessor.RequestTimeout;

    private readonly OriginGuardedDataverseHttpClientAccessor _accessor;

    /// <summary>Creates an accessor that authenticates through <paramref name="authProvider"/>.</summary>
    /// <param name="authProvider">Supplies bearer tokens for the Dataverse organization.</param>
    public AgentHostDataverseHttpClientAccessor(ISyncAuthProvider authProvider) : this(authProvider, CreateRedirectRejectingHandler)
    {
    }

    internal AgentHostDataverseHttpClientAccessor(ISyncAuthProvider authProvider, Func<HttpMessageHandler> innerHandlerFactory)
    {
        _accessor = new OriginGuardedDataverseHttpClientAccessor(authProvider ?? throw new ArgumentNullException(nameof(authProvider)), () => DataverseOperationScope.Organization ?? throw new InvalidOperationException("The Dataverse organization has not been set. Call SetDataverseOrganization before creating a client."), innerHandlerFactory ?? throw new ArgumentNullException(nameof(innerHandlerFactory)), Refusal);
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
    public HttpClient CreateClient() => _accessor.CreateClient();

    /// <summary>Disposes the transport shared by every client this accessor created.</summary>
    public void Dispose() => _accessor.Dispose();

    internal static CopilotStudioException Refusal(Uri? requestUri, Uri organization) => CopilotStudioException.UntrustedDataverseOrigin(requestUri?.GetLeftPart(UriPartial.Authority) ?? "(none)", organization.GetLeftPart(UriPartial.Authority));
}
