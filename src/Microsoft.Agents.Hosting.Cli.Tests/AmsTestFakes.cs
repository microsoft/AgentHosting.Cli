using System.Net;
using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Packaging;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Builds package/deploy services for facade tests that must never call AMS.</summary>
internal static class AgentDeployFixtures
{
    public static AgentPackageUploadService PendingUploadService(EnvironmentSelectionService environments) => new(PendingResolver(environments), new PendingAmsAgentReadTransport(), new PendingAmsPackagePublicationTransport());

    public static AgentDeployService PendingService(EnvironmentSelectionService environments) => new(PendingResolver(environments), new PendingAmsDeploymentTransport());

    public static AmsAddressResolver PendingResolver(EnvironmentSelectionService environments) => new(AmsTargetFixtures.Targets(), environments, new PendingAmsAgentReadTransport(), new PendingAmsDeploymentTransport());
}

/// <summary>Builds an agent-create service for tests that must never call AMS.</summary>
internal static class AgentCreateFixtures
{
    public static AgentCreateService PendingService(EnvironmentSelectionService environments) =>
        new(
            AmsTargetFixtures.Targets(),
            environments,
            new PendingAmsAgentCreationTransport());
}

internal static class AmsTargetFixtures
{
    public static AmsTargets Targets(AmsEndpointProfile? endpoints = null) => new(endpoints ?? new AmsEndpointProfile(), PowerPlatformApiTargetConfig.Public);

    public static void AssertTarget(AmsServiceTarget expected, AmsServiceTarget actual)
    {
        Assert.NotNull(actual.Context);
        Assert.Equal(expected, actual with { Context = null });
    }
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

    public Task<IReadOnlyList<AmsAgentReadResult>> ListAsync(AmsServiceTarget serviceTarget, CancellationToken cancellationToken) => throw new InvalidOperationException("Agent list was not expected in this test.");
}

internal sealed class PendingAmsDeploymentTransport : IAmsDeploymentTransport
{
    public Task<AmsDeploymentResult> ReadAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deployment read was not expected in this test.");

    public Task<AmsDeploymentListResult> ListAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deployment list was not expected in this test.");

    public Task<AmsDeploymentResult> CreateAsync(AmsDeploymentCreateTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deployment creation was not expected in this test.");

    public Task<AmsDeploymentResult> UpdateAsync(AmsDeploymentUpdateTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deployment update was not expected in this test.");

    public Task<AmsDeploymentResult> DeployAsync(AmsDeploymentDeployTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deploy was not expected in this test.");

    public Task DeleteAsync(AmsDeploymentDeleteTransportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Deployment deletion was not expected in this test.");
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
            IfMatch = request.Headers.TryGetValues("If-Match", out var entityTags) ? entityTags.Single() : null,
            HeaderNames = [.. request.Headers.Select(h => h.Key)],
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
    public string? IfMatch { get; init; }
    public required IReadOnlyList<string> HeaderNames { get; init; }
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

    /// <summary>The permissions the minted token carries; defaults to the publication permission.</summary>
    public string Scopes { get; init; } = PowerPlatformApiPermissions.AppsUpdate;

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource,
        string? targetTenantId,
        CancellationToken cancellationToken)
    {
        Resources.Add(resource);
        Tenants.Add(targetTenantId);

        return Task.FromResult(new TokenAcquisitionResult
        {
            AccessToken = FakeJwt.Create(audience: resource, tenantId: targetTenantId, scopes: Scopes),
            TenantId = targetTenantId,
            ExpiresOn = DateTimeOffset.UnixEpoch.AddYears(60),
        });
    }

    public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes,
        string? targetTenantId,
        CancellationToken cancellationToken) =>
        AcquireTokenSilentAsync(string.Join(' ', scopes), targetTenantId, cancellationToken);
}

internal sealed class FakeStandardInput(string text = "", bool isRedirected = true) : IStandardInput
{
    public int Reads { get; private set; }

    public int? MaximumBytes { get; private set; }

    public bool IsRedirected => isRedirected;

    public Task<string> ReadToEndAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        Reads++;
        MaximumBytes = maximumBytes;
        return Task.FromResult(text);
    }
}

internal static class AmsCommandHost
{
    public static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, object handlers, IFeatureFlags featureFlags, params string[] args)
    {
        var services = new ServiceCollection();
        foreach (var contract in handlers.GetType().GetInterfaces())
        {
            services.AddSingleton(contract, handlers);
        }

        using var container = services.BuildServiceProvider();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(operations, new CompositeHandlerProvider(new FacadeHandlerProvider(operations), container), featureFlags);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}

internal sealed class RecordingAmsHandlers :
    ICommandHandler<AgentDeploymentAmsCreateRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>,
    ICommandHandler<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsUpdateRequest, AgentDeploymentAmsResult>,
    ICommandHandler<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>,
    ICommandHandler<AgentAmsListRequest, AgentAmsListResult>,
    ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>,
    ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>,
    ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>,
    ICommandHandler<AgentCommitListRequest, AgentCommitListResult>
{
    public static readonly AgentAmsRecord Agent = new() { AgentId = "44444444-4444-4444-4444-444444444444", DisplayName = "Contoso Agent", AgentType = "CodeAgent", State = "Active", TenantId = "11111111-1111-1111-1111-111111111111", EnvironmentId = "22222222-2222-2222-2222-222222222222", Language = "DotNet", LanguageVersion = "10" };

    public List<object> Requests { get; } = [];
    public Exception? Failure { get; init; }
    public AgentDeploymentAmsRecord Deployment { get; init; } = FakeOperations.CreateStubDeploymentRecord();
    public string? DeleteStatus { get; init; }

    public T Single<T>() => Assert.IsType<T>(Assert.Single(Requests));

    public Task<AgentDeploymentAmsResult> HandleAsync(AgentDeploymentAmsCreateRequest request, CancellationToken cancellationToken) => Answer(request, new AgentDeploymentAmsResult { Status = AgentDeploymentAmsResult.CreatedStatus, Deployment = Deployment with { State = "NotDeployed", PackageRevisionId = null, CommitId = null, PackageDigest = null, PackageSizeInBytes = null, Endpoint = null, BotService = null } });

    public Task<AgentDeploymentAmsListResult> HandleAsync(AgentDeploymentAmsListRequest request, CancellationToken cancellationToken) => Answer(request, new AgentDeploymentAmsListResult { AgentId = Deployment.AgentId, HomeEnvironmentId = Deployment.HomeEnvironmentId, Deployments = [Deployment], KeptBots = [new AgentAmsKeptBot { DeploymentId = "78787878-7878-7878-7878-787878787878", DeploymentName = "old", BotName = "ams-old" }] });

    public Task<AgentDeploymentAmsResult> HandleAsync(AgentDeploymentAmsShowRequest request, CancellationToken cancellationToken) => Answer(request, new AgentDeploymentAmsResult { Status = AgentDeploymentAmsResult.ReadStatus, Deployment = Deployment });

    public Task<AgentDeploymentAmsResult> HandleAsync(AgentDeploymentAmsUpdateRequest request, CancellationToken cancellationToken) => Answer(request, new AgentDeploymentAmsResult { Status = AgentDeploymentAmsResult.UpdatedStatus, Deployment = Deployment with { DeploymentName = request.NewName ?? Deployment.DeploymentName } });

    public Task<AgentDeploymentAmsDeleteResult> HandleAsync(AgentDeploymentAmsDeleteRequest request, CancellationToken cancellationToken) => Answer(request, new AgentDeploymentAmsDeleteResult { Status = DeleteStatus ?? AgentDeploymentAmsDeleteResult.DeletedStatus, Deployment = Deployment, WasLive = true });

    public Task<AgentAmsListResult> HandleAsync(AgentAmsListRequest request, CancellationToken cancellationToken) => Answer(request, new AgentAmsListResult { EnvironmentId = request.EnvironmentId ?? Agent.EnvironmentId, Agents = [Agent] });

    public Task<AgentAmsShowResult> HandleAsync(AgentAmsShowRequest request, CancellationToken cancellationToken) => Answer(request, new AgentAmsShowResult { Agent = Agent, DeploymentsAvailable = true, Deployments = [Deployment], KeptBots = [] });

    public Task<AgentAmsUpdateResult> HandleAsync(AgentAmsUpdateRequest request, CancellationToken cancellationToken) => Answer(request, new AgentAmsUpdateResult { Status = AgentAmsUpdateResult.UpdatedStatus, Agent = Agent with { DisplayName = request.NewName ?? Agent.DisplayName, Description = request.Description } });

    public Task<AgentAmsDeleteResult> HandleAsync(AgentAmsDeleteRequest request, CancellationToken cancellationToken) => Answer(request, new AgentAmsDeleteResult { Status = DeleteStatus ?? AgentAmsDeleteResult.DeletedStatus, AgentId = Agent.AgentId, DisplayName = Agent.DisplayName, Deployments = [new AgentAmsRemovedDeployment { DeploymentId = Deployment.DeploymentId, DeploymentName = Deployment.DeploymentName, EnvironmentId = Deployment.TargetEnvironmentId,     DeploymentType = "Dev", BotName = "ams-bot-1", BotDeleted = true }] });

    public Task<AgentCommitListResult> HandleAsync(AgentCommitListRequest request, CancellationToken cancellationToken) => Answer(request, new AgentCommitListResult { AgentId = Agent.AgentId, DeploymentsAvailable = true, Commits = [new AgentCommitRecord { CommitId = Deployment.CommitId, PackageRevisionId = Deployment.PackageRevisionId!, SizeInBytes = 1024, CreatedTime = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), Deployments = [Deployment.DeploymentName] }] });

    private Task<TResult> Answer<TResult>(object request, TResult result)
    {
        Requests.Add(request);
        return Failure is null ? Task.FromResult(result) : Task.FromException<TResult>(Failure);
    }
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
