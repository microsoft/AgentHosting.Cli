using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Packaging;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Builds package/deploy services for facade tests that must never call AMS.</summary>
internal static class AgentDeployFixtures
{
    public static AgentPackageUploadService PendingUploadService(EnvironmentSelectionService environments) =>
        new(
            environments,
            new PendingAmsAgentReadTransport(),
            new PendingAmsPackagePublicationTransport());

    public static AgentDeployService PendingService(EnvironmentSelectionService environments) =>
        new(
            PowerPlatformApiTargetConfig.Public,
            environments,
            new PendingAmsAgentReadTransport(),
            new PendingAmsDeploymentTransport());
}

/// <summary>Builds an agent-create service for tests that must never call AMS.</summary>
internal static class AgentCreateFixtures
{
    public static AgentCreateService PendingService(EnvironmentSelectionService environments) =>
        new(
            new AgentCreationTarget(),
            environments,
            new PendingAmsAgentCreationTransport());
}

/// <summary>A create transport whose invocation is always a test failure.</summary>
internal sealed class PendingAmsAgentCreationTransport : IAmsAgentCreationTransport
{
    public Task<AmsAgentCreationResult> CreateAsync(
        AmsAgentCreationTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent creation was not expected in this test.");
}

/// <summary>
/// The transport a test wires in when the scenario must never publish. Any call is a test failure
/// rather than a silent no-op, so "no bytes were sent" is proven instead of assumed.
/// </summary>
internal sealed class PendingAmsPackagePublicationTransport : IAmsPackagePublicationTransport
{
    public Task<AmsPackagePublicationResult> PublishAsync(
        AmsPackagePublicationTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent package publication was not expected in this test.");
}

internal sealed class PendingAmsAgentReadTransport : IAmsAgentReadTransport
{
    public Task<AmsAgentReadResult> GetAsync(
        AmsAgentReadTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agent read was not expected in this test.");
}

internal sealed class PendingAmsDeploymentTransport : IAmsDeploymentTransport
{
    public Task<AmsDeploymentCurrentResult> GetAsync(
        AmsDeploymentReadTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment read was not expected in this test.");

    public Task<AmsDeploymentCreateResult> CreateAsync(
        AmsDeploymentCreateTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment creation was not expected in this test.");

    public Task<AmsDeploymentCreateResult> UpdateAsync(
        AmsDeploymentUpdateTransportRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deployment update was not expected in this test.");
}

/// <summary>
/// Captures the exact wire request the adapter builds — method, url, headers, media type, declared
/// length, and body bytes — and answers with a supplied response.
///
/// <para>
/// The authorization header is recorded as a redaction marker plus a count, never as a value: a test
/// that stored the token would be the first place the token leaked. The count is what makes the
/// redirect assertion meaningful — the property is that the credential is attached exactly once, to
/// one host.
/// </para>
/// </summary>
internal sealed class CapturingPackageHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
    : HttpMessageHandler
{
    public List<CapturedPackageRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new CapturedPackageRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            HasAuthorization = request.Headers.Authorization is not null,
            ClientRequestId = request.Headers.TryGetValues("x-ms-client-request-id", out var clientRequestIds)
                ? clientRequestIds.Single()
                : null,
            IdempotencyKey = request.Headers.TryGetValues(
                AmsAgentCreationAdapter.IdempotencyKeyHeaderName,
                out var idempotencyKeys)
                ? idempotencyKeys.Single()
                : null,
            HeaderNames = [.. request.Headers.Select(h => h.Key)],
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            ContentType = request.Content?.Headers.ContentType?.MediaType,
            ContentLength = request.Content?.Headers.ContentLength,
            Body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken),
        });

        return response(request);
    }
}

/// <summary>An HTTP handler whose asynchronous result is supplied by the timeout/cancellation test.</summary>
internal sealed class AsyncPackageHandler(
    Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        response(cancellationToken);
}

internal sealed class ThrowingReadContent(Exception exception) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.FromException(exception);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed record CapturedPackageRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public bool HasAuthorization { get; init; }
    public string? ClientRequestId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required IReadOnlyList<string> HeaderNames { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string? IfMatch => Headers.GetValueOrDefault("If-Match");
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    public required byte[] Body { get; init; }
}

/// <summary>
/// A silent token provider that mints an unsigned JWT carrying exactly the claims the CLI checks. It
/// contacts nothing and opens nothing, so every publication test is offline by construction. The
/// token is built by the shared <see cref="FakeJwt"/> fixture builder — a fixture, not a credential.
/// </summary>
internal sealed class FakePpapiTokenProvider : ITokenProvider
{
    public List<string> Resources { get; } = [];
    public List<string?> Tenants { get; } = [];
    public List<string> RenewedResources { get; } = [];

    /// <summary>The permissions the minted token carries; defaults to the publication permission.</summary>
    public string Scopes { get; init; } = PowerPlatformApiPermissions.AppsUpdate;

    public DateTimeOffset ExpiresOn { get; set; } = DateTimeOffset.UnixEpoch.AddYears(60);

    public DateTimeOffset RenewedExpiresOn { get; init; } = DateTimeOffset.UnixEpoch.AddYears(60);

    public Exception? AcquisitionFailure { get; init; }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        Resources.Add(resource);
        Tenants.Add(targetTenantId);

        return AcquisitionFailure is null ? Task.FromResult(Token(resource, targetTenantId)) : Task.FromException<TokenAcquisitionResult>(AcquisitionFailure);
    }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        AcquireTokenSilentAsync(string.Join(' ', scopes), targetTenantId, cancellationToken);

    public Task<TokenAcquisitionResult> RenewTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken)
    {
        RenewedResources.Add(resource);
        ExpiresOn = RenewedExpiresOn;

        return Task.FromResult(Token(resource, targetTenantId));
    }

    private TokenAcquisitionResult Token(string resource, string? targetTenantId) => new() { AccessToken = FakeJwt.Create(audience: resource, tenantId: targetTenantId, scopes: Scopes), TenantId = targetTenantId, ExpiresOn = ExpiresOn };
}

/// <summary>
/// An in-memory transport that records the digest of the bytes it was actually handed. Reading the
/// stream is the point: it proves the service side saw the same bytes the caller hashed, rather than
/// trusting a value the caller passed alongside them.
/// </summary>
internal sealed class RecordingAmsTransport : IAmsPackagePublicationTransport
{
    private readonly Func<AmsPackagePublicationTransportRequest, string, AmsPackagePublicationResult> _result;

    public RecordingAmsTransport(
        Func<AmsPackagePublicationTransportRequest, string, AmsPackagePublicationResult> result) =>
        _result = result;

    public AmsPackagePublicationTransportRequest? LastRequest { get; private set; }
    public string? SentDigest { get; private set; }
    public int Calls { get; private set; }

    public async Task<AmsPackagePublicationResult> PublishAsync(
        AmsPackagePublicationTransportRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;

        using var copy = new MemoryStream();
        await request.PackageStream.CopyToAsync(copy, cancellationToken);
        SentDigest = Convert.ToHexString(SHA256.HashData(copy.ToArray())).ToLowerInvariant();

        return _result(request, SentDigest);
    }
}

/// <summary>An in-memory agent-create transport that records the exact service request.</summary>
internal sealed class RecordingAmsAgentCreationTransport : IAmsAgentCreationTransport
{
    private readonly Func<
        AmsAgentCreationTransportRequest,
        CancellationToken,
        Task<AmsAgentCreationResult>> _result;

    public RecordingAmsAgentCreationTransport(
        Func<AmsAgentCreationTransportRequest, AmsAgentCreationResult> result)
        : this((request, _) => Task.FromResult(result(request)))
    {
    }

    public RecordingAmsAgentCreationTransport(
        Func<AmsAgentCreationTransportRequest, CancellationToken, Task<AmsAgentCreationResult>> result)
    {
        _result = result;
    }

    public AmsAgentCreationTransportRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public Task<AmsAgentCreationResult> CreateAsync(
        AmsAgentCreationTransportRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return _result(request, cancellationToken);
    }
}

/// <summary>Builds a JSON HTTP response for the capturing handler.</summary>
internal static class FakeHttpResponse
{
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        response.Headers.Location = new Uri(location);
        return response;
    }
}

internal static class LostAnswer
{
    private static readonly HashSet<string> PerAttemptHeaders = new(["x-ms-client-request-id", "Prefer", "Authorization"], StringComparer.OrdinalIgnoreCase);

    public static string[] Triggers { get; } = ["timeout", "transport", "io", "gateway-timeout"];

    public static HttpResponseMessage Of(string trigger) => trigger switch
    {
        "timeout" => throw new TaskCanceledException("lost-answer-detail"),
        "transport" => throw new HttpRequestException("lost-answer-detail"),
        "io" => throw new IOException("lost-answer-detail"),
        _ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent(string.Empty) },
    };

    public static Func<HttpRequestMessage, HttpResponseMessage> First(string? trigger, Func<HttpResponseMessage> answer)
    {
        var attempts = 0;
        return _ => trigger is not null && attempts++ == 0 ? Of(trigger) : answer();
    }

    public static string ReportedIds(IReadOnlyList<CapturedPackageRequest> requests) => $"clientRequestId={requests[0].ClientRequestId}, replayClientRequestId={requests[1].ClientRequestId}";

    public static void AssertReplayedOnce(IReadOnlyList<CapturedPackageRequest> requests, string? firstPreference = null)
    {
        Assert.Equal(2, requests.Count);
        var (lost, replay) = (requests[0], requests[1]);
        Assert.Equal((lost.Method, lost.Uri), (replay.Method, replay.Uri));
        Assert.Equal(lost.Body, replay.Body);
        Assert.Equal(Kept(lost), Kept(replay));
        Assert.Equal((firstPreference, "respond-async"), (lost.Headers.GetValueOrDefault("Prefer"), replay.Headers.GetValueOrDefault("Prefer")));
        Assert.NotNull(lost.IdempotencyKey);
        Assert.True(replay.HasAuthorization);
        Assert.True(Guid.TryParse(replay.ClientRequestId, out _));
        Assert.NotEqual(lost.ClientRequestId, replay.ClientRequestId);
    }

    private static IEnumerable<KeyValuePair<string, string>> Kept(CapturedPackageRequest request) => request.Headers.Where(header => !PerAttemptHeaders.Contains(header.Key)).OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase);
}

internal static class MultipartBody
{
    public static IReadOnlyList<(IReadOnlyDictionary<string, string> Headers, byte[] Content)> Parts(byte[] body)
    {
        var text = $"\r\n{Encoding.Latin1.GetString(body)}";
        var segments = text.Split(text[..text.IndexOf("\r\n", 2, StringComparison.Ordinal)]);
        Assert.Equal(("", "--\r\n"), (segments[0], segments[^1]));
        return [.. segments[1..^1].Select(Part)];
    }

    private static (IReadOnlyDictionary<string, string> Headers, byte[] Content) Part(string segment)
    {
        var headersEnd = segment.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (segment[2..headersEnd].Split("\r\n").Select(line => line.Split(": ", 2)).ToDictionary(header => header[0], header => header[1], StringComparer.OrdinalIgnoreCase), Encoding.Latin1.GetBytes(segment[(headersEnd + 4)..]));
    }
}
