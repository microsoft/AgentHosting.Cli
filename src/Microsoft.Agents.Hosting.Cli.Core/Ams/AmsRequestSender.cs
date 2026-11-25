using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>One authorized call to an Agent Management Service route.</summary>
internal sealed record AmsCall
{
    public required HttpMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public required AmsServiceTarget ServiceTarget { get; init; }

    /// <summary>The delegated permissions the token must carry before the call is sent.</summary>
    public required IReadOnlyList<string> RequiredPermissions { get; init; }

    /// <summary>The name of the call in error messages, such as "Deployment read".</summary>
    public required string Phase { get; init; }
    public HttpContent? Content { get; init; }

    /// <summary>The entity tag the call is conditional on, sent unchanged in the If-Match header.</summary>
    public string? IfMatch { get; init; }
    public bool OutcomeMayBeIndeterminate { get; init; }

    /// <summary>Explains a failure status and AMS error code better than the generic mapping, or returns null to keep the generic mapping.</summary>
    public Func<int, string?, string?, (string Message, int ExitCode, string ErrorKind, string Remediation)?>? Refusal { get; init; }
}

/// <summary>A success answer of the Agent Management Service with the correlation ids of its call.</summary>
internal sealed record AmsResponse(HttpStatusCode StatusCode, string Body, string ClientRequestId, string? ServiceRequestId);

/// <summary>Sends calls to Agent Management Service routes: validates the delegated token, refuses redirects, bounds each call in time and maps failures to typed errors.</summary>
internal sealed partial class AmsRequestSender
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokens;
    private readonly PpapiTokenClaimsValidator _claimsValidator;
    private readonly IProfileResourceTokenProvider? _profileTokens;

    /// <summary>Creates a sender over a client whose handler must not follow redirects.</summary>
    internal AmsRequestSender(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator, IProfileResourceTokenProvider? profileTokens)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _claimsValidator = claimsValidator ?? new PpapiTokenClaimsValidator();
        _profileTokens = profileTokens;
    }

    /// <summary>Sends one call and returns its success answer, or throws the mapped failure without echoing service-controlled text.</summary>
    internal async Task<AmsResponse> SendAsync(AmsCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var clientRequestId = Guid.NewGuid().ToString("D");
        var serviceTarget = call.ServiceTarget;
        var token = await ResourceTokenContext.AcquireAsync(_tokens, _profileTokens, _claimsValidator, serviceTarget.Context, serviceTarget.Audience, serviceTarget.TenantId, call.RequiredPermissions, cancellationToken).ConfigureAwait(false);

        using var message = new HttpRequestMessage(call.Method, call.Uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("x-ms-client-request-id", clientRequestId);
        if (call.IfMatch is { } entityTag)
        {
            message.Headers.TryAddWithoutValidation("If-Match", entityTag);
        }

        message.Content = call.Content;

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
            throw AgentDeployException.TimedOut(call.Phase, clientRequestId, call.OutcomeMayBeIndeterminate, ex);
        }
        catch (HttpRequestException ex)
        {
            throw AgentDeployException.TransportFailed(call.Phase, clientRequestId, call.OutcomeMayBeIndeterminate, ex);
        }

        using (response)
        {
            var serviceRequestId = ServiceCorrelation.ReadServiceRequestId(response);
            if (IsRedirect(response.StatusCode))
            {
                throw AgentDeployException.RedirectRejected(
                    call.Phase,
                    (int)response.StatusCode,
                    serviceTarget.Endpoint.Host);
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
                throw AgentDeployException.TimedOut($"{call.Phase} response read", clientRequestId, call.OutcomeMayBeIndeterminate, ex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                throw AgentDeployException.TransportFailed($"{call.Phase} response read", clientRequestId, call.OutcomeMayBeIndeterminate, ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw MapFailure(call, (int)response.StatusCode, responseBody, clientRequestId, serviceRequestId);
            }

            return new AmsResponse(response.StatusCode, responseBody, clientRequestId, serviceRequestId);
        }
    }

    private static AgentDeployException MapFailure(AmsCall call, int status, string body, string clientRequestId, string? serviceRequestId)
    {
        var (code, serviceMessage) = TargetRequestException.ParseTargetError(body);
        if (status == (int)HttpStatusCode.Conflict && string.Equals(code, "DeploymentDeleting", StringComparison.OrdinalIgnoreCase))
        {
            return AgentDeployException.DeploymentDeleting(FindDeploymentId(serviceMessage), clientRequestId, serviceRequestId);
        }

        if (call.Refusal?.Invoke(status, code, serviceMessage) is { } refusal)
        {
            return AgentDeployException.ServiceRejected(
                AgentDeployException.AddCorrelation(refusal.Message, clientRequestId, serviceRequestId),
                refusal.ExitCode,
                refusal.ErrorKind,
                refusal.Remediation);
        }

        var mapped = TargetRequestException.MapTargetResponseError(
            status,
            body,
            call.Method.Method,
            call.Uri.ToString(),
            $"Agent Hosting {call.Phase}",
            "msagent");
        return AgentDeployException.ServiceRejected(
            AgentDeployException.AddCorrelation(
                mapped.Message,
                clientRequestId,
                serviceRequestId),
            mapped.ExitCode,
            mapped.ErrorKind ?? "request-failed",
            mapped.Remediation ?? "Inspect the service response using the correlation identifier.");
    }

    private static string? FindDeploymentId(string? serviceMessage) => serviceMessage is not null && DeploymentIdPattern().Match(serviceMessage) is { Success: true } match ? match.Value : null;

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeploymentIdPattern();

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
