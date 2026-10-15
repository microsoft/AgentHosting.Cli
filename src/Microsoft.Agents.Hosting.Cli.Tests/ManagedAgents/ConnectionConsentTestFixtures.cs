using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body, string? ClientRequestId = null);

internal sealed class SteppedClock(TimeSpan step) : TimeProvider
{
    private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);

    public void Advance() => Interlocked.Add(ref _ticks, step.Ticks);
}

internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    private readonly Func<CapturedRequest, HttpResponseMessage>? _responder;

    public ScriptedHandler(params HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    public ScriptedHandler(Func<CapturedRequest, HttpResponseMessage> responder)
    {
        _responses = new Queue<HttpResponseMessage>();
        _responder = responder;
    }

    public List<CapturedRequest> Requests { get; } = [];

    public HttpResponseMessage Fallback { get; init; } = new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.Parameter,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.TryGetValues(ServiceCorrelation.ClientRequestIdHeader, out var clientRequestIds) ? clientRequestIds.FirstOrDefault() : null);

        Requests.Add(captured);

        if (_responder is not null)
        {
            return _responder(captured);
        }

        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(Fallback.StatusCode) { Content = new StringContent(await Fallback.Content.ReadAsStringAsync(cancellationToken)) };
    }
}

internal sealed class RecordingTokenProvider : ITokenProvider
{
    public List<string> Resources { get; } = [];

    public List<string> Scopes { get; } = [];

    public List<string?> Tenants { get; } = [];

    public DateTimeOffset ExpiresOn { get; init; } = DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>The bearer value handed back, replaced where the claims on it matter.</summary>
    public string AccessTokenReturns { get; init; } = "management-token";

    /// <summary>The tenant the library reports the token was acquired for, defaulting to the requested one.</summary>
    public string? AcquiredTenantId { get; init; }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken)
    {
        Resources.Add(resource);
        Tenants.Add(targetTenantId);

        return Task.FromResult(new TokenAcquisitionResult { AccessToken = AccessTokenReturns, TenantId = AcquiredTenantId ?? targetTenantId, ExpiresOn = ExpiresOn });
    }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken)
    {
        Scopes.AddRange(scopes);

        return Task.FromResult(new TokenAcquisitionResult { AccessToken = "apihub-token", ExpiresOn = ExpiresOn });
    }
}

internal static class ConnectionConsentCallback
{
    /// <summary>Answers a waiting loopback the way the consent page's redirect does.</summary>
    public static async Task SendAsync(Uri callbackUrl, string query)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", callbackUrl.Port);

        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {callbackUrl.AbsolutePath}?{query} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        await stream.FlushAsync();

        await DrainAsync(stream);
    }

    public static async Task DrainAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];

        while (await stream.ReadAsync(buffer) > 0)
        {
        }
    }
}

internal static class ConnectionFixtures
{
    public const string EnvironmentId = "a7975ddd-87fe-e986-9280-bfe47a2e7b10";

    /// <summary>The same environment as <see cref="EnvironmentId"/>, for the ports that take a context.</summary>
    public static CopilotEnvironmentContext Environment { get; } = new()
    {
        EnvironmentId = EnvironmentId,
        TenantId = Guid.Empty.ToString(),
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    /// <summary>The same environment homed in another tenant, for the guest and cross-tenant cases.</summary>
    public static CopilotEnvironmentContext EnvironmentIn(string tenantId) => Environment with { TenantId = tenantId };

    public const string ConsentLink = "https://usa002-004.consent.azure-apihub.net/login?data=abc";
    public const string LandingUrl = "http://127.0.0.1:51999/";

    public const string SsoConnector = """
        {"name":"shared_office365","properties":{"displayName":"Office 365 Outlook","connectionParameters":{"token":{"type":"oauthSetting","oAuthSettings":{"properties":{"IsFirstParty":"true","IsOnbehalfofLoginSupported":true}}}}}}
        """;

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string serviceRequestId)
    {
        var response = Json(status, body);

        response.Headers.TryAddWithoutValidation("x-ms-request-id", serviceRequestId);

        return response;
    }

    public static HttpResponseMessage Ok(string body) => Json(HttpStatusCode.OK, body);

    public static string Connection(bool authenticated) =>
        "{\"name\":\"conn-1\",\"properties\":{\"displayName\":\"Office 365 Outlook\""
        + (authenticated
            ? ",\"statuses\":[{\"status\":\"Connected\"}]"
            : ",\"statuses\":[{\"status\":\"Error\",\"target\":\"Token\",\"error\":{\"code\":\"Unauthenticated\"}}]")
        + "}}";

    public static string ConnectionWithStatus(string statusJson, bool withDisplayName = true) =>
        "{\"name\":\"conn-1\",\"properties\":{"
        + (withDisplayName ? "\"displayName\":\"Office 365 Outlook\"," : string.Empty)
        + "\"statuses\":[" + statusJson + "]}}";

    public static string ReadyConnection(bool withDisplayName = true) =>
        ConnectionWithStatus("{\"status\":\"Connected\"}", withDisplayName);

    public static string ConsentResponse(bool withConsentLink = true, string? consentLink = null) =>
        "{\"principalType\":\"NotSpecified\""
        + (withConsentLink ? ",\"consentLink\":\"" + (consentLink ?? ConsentLink) + "\"" : string.Empty)
        + "}";
}
