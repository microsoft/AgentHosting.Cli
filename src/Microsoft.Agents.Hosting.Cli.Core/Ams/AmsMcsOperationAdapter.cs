using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The HTTP transport for MCS agent reads, registers, deletes, deployments and MCS actions on the Agent Management Service, validating delegated-token claims before attaching authorization.</summary>
public sealed class AmsMcsOperationAdapter : IAmsMcsAgentReadTransport, IAmsMcsRegistrationTransport, IAmsMcsOperationTransport, IAmsMcsAgentDeleteTransport, IAmsMcsDeploymentTransport
{
    /// <summary>The header that names the MCS agent type of the record a shared deployment route changes, which selects the route's MCS path.</summary>
    public const string AgentTypeHeaderName = "x-ms-agent-type";

    /// <summary>The longest one request may take, which outlasts the service's synchronous wait for an operation.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private const string JsonMediaType = "application/json";

    private const string PreferHeaderName = "Prefer";

    private const string RespondAsyncPreference = "respond-async";

    private const string IfMatchHeaderName = "If-Match";

    private readonly AmsDelegatedRequestSender _sender;

    /// <summary>Creates the adapter over a client that does not follow redirects.</summary>
    /// <param name="httpClient">The client requests are sent with.</param>
    /// <param name="tokens">Acquires delegated tokens.</param>
    /// <param name="claimsValidator">Validates token claims before a token leaves the process.</param>
    public AmsMcsOperationAdapter(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _sender = new AmsDelegatedRequestSender(httpClient, tokens, claimsValidator ?? new PpapiTokenClaimsValidator());
    }

    /// <inheritdoc />
    public async Task<AmsMcsAgentRecord> GetAgentAsync(AmsServiceTarget target, string agentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var response = await SendAsync(new AmsMcsCall(HttpMethod.Get, AmsRoutes.Agent(target, agentId), target, PowerPlatformApiPermissions.AppsRead, "Agent read"), cancellationToken).ConfigureAwait(false);
        return Read(response, root => AmsMcsWire.ReadAgent(root, response.ETag));
    }

    /// <inheritdoc />
    public async Task<AmsMcsAgentPage> ListAgentsAsync(AmsServiceTarget target, int top, string? agentType, string? repositoryId, string? skipToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Get, AmsRoutes.McsAgentRecords(target, top, agentType, repositoryId, skipToken), target, PowerPlatformApiPermissions.AppsRead, "Agent list"), cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadAgentPage);
    }

    /// <inheritdoc />
    public async Task<AmsMcsRepository> ReadRepositoryAsync(AmsServiceTarget target, string repositoryId, string? agentFolder, bool includeDeployments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Get, AmsRoutes.McsRepository(target, repositoryId, agentFolder, includeDeployments), target, PowerPlatformApiPermissions.AppsRead, "Repository read"), cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadRepository);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AmsMcsHolder>> FindHoldersAsync(AmsServiceTarget target, string? botId, string? workflowId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            return Read(await SendAsync(new AmsMcsCall(HttpMethod.Get, AmsRoutes.McsHolders(target, botId, workflowId), target, PowerPlatformApiPermissions.AppsRead, "Holders read"), cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadHolders);
        }
        catch (TargetRequestException exception) when (exception.Status == 404)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<AmsMcsAgentRecord> RegisterAsync(AmsMcsRegistration request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new RegisterWireRequest(request.AgentId, request.DisplayName, request.Description, request.AgentType, ManagedAgentOrigins.AgentHostCli, new RegisterMcsWire(request.SchemaName, request.AuthoringShape), new RegisterSourceControlWire(request.RepositoryId is null ? "New" : "Existing", request.RepositoryId, request.Branch, request.AgentFolder), [.. request.Deployments.Select(deployment => new RegisterDeploymentWire(deployment.DeploymentId, deployment.DeploymentType, deployment.DeploymentName, deployment.EnvironmentId, deployment.BotId, deployment.WorkflowId))]);
        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Post, AmsRoutes.McsRegister(request.Target), request.Target, PowerPlatformApiPermissions.AppsUpdate, "Agent register") { Body = body, OnBehalfOf = true, RecoversLostAnswer = true }, cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadAgent);
    }

    /// <inheritdoc />
    public async Task<AmsMcsOperationAccepted> DeployAsync(AmsMcsDeployTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReadStarted(await SendAsync(new AmsMcsCall(HttpMethod.Post, AmsRoutes.McsDeploy(request.Target, request.AgentId, request.DeploymentId), request.Target, PowerPlatformApiPermissions.AppsUpdate, "MCS deploy") { Body = new DeployWireRequest(request.CommitSha, request.Publish, request.Overwrite), IdempotencyKey = request.IdempotencyKey, OnBehalfOf = true, RespondAsync = request.RespondAsync, StartsOperation = true, RecoversLostAnswer = true }, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<AmsMcsOperationAccepted> CaptureAsync(AmsMcsCaptureTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReadStarted(await SendAsync(new AmsMcsCall(HttpMethod.Post, AmsRoutes.McsCapture(request.Target, request.AgentId, request.DeploymentId), request.Target, PowerPlatformApiPermissions.AppsUpdate, "MCS capture") { Body = new CaptureWireRequest(request.Message, request.Resolve), IdempotencyKey = request.IdempotencyKey, OnBehalfOf = true, RespondAsync = request.RespondAsync, StartsOperation = true, RecoversLostAnswer = true }, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<AmsMcsOperationRecord> GetOperationAsync(AmsServiceTarget target, string agentId, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Get, AmsRoutes.McsOperation(target, agentId, operationId), target, PowerPlatformApiPermissions.AppsRead, "MCS operation read"), cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadOperation);
    }

    /// <inheritdoc />
    public async Task<AmsMcsDeploymentRecord> RecordDeployAsync(AmsMcsDeployRecordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Post, AmsRoutes.McsDeployRecords(request.Target, request.AgentId, request.DeploymentId), request.Target, PowerPlatformApiPermissions.AppsUpdate, "Deploy record") { Body = new DeployRecordWireRequest(request.BotId, request.WorkflowId, request.CommitSha, request.Exact, request.Published, request.DeployedTime.UtcDateTime), OnBehalfOf = true }, cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadDeployment);
    }

    /// <inheritdoc />
    public async Task DeleteAgentAsync(AmsServiceTarget target, string agentId, string eTag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        await SendAsync(new AmsMcsCall(HttpMethod.Delete, AmsRoutes.McsAgent(target, agentId), target, PowerPlatformApiPermissions.AppsUpdate, "Agent delete") { IfMatch = eTag }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AmsMcsDeploymentRecord> CreateMcsDeploymentAsync(AmsMcsDeploymentCreateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Post, AmsRoutes.Deployments(request.Target, request.AgentId), request.Target, PowerPlatformApiPermissions.AppsUpdate, "Deployment create") { Body = new DeploymentCreateWireRequest(request.DeploymentId, request.AgentType, request.DeploymentType, request.DeploymentName, request.TargetEnvironmentId) }, cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadDeployment);
    }

    /// <inheritdoc />
    public async Task<AmsMcsDeploymentRecord> RenameMcsDeploymentAsync(AmsMcsDeploymentRenameTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Read(await SendAsync(new AmsMcsCall(HttpMethod.Patch, AmsRoutes.Deployment(request.Target, request.AgentId, request.DeploymentId), request.Target, PowerPlatformApiPermissions.AppsUpdate, "Deployment rename") { Body = new DeploymentRenameWireRequest(request.DeploymentName), AgentType = request.AgentType }, cancellationToken).ConfigureAwait(false), AmsMcsWire.ReadDeployment);
    }

    /// <inheritdoc />
    public async Task DeleteDeploymentAsync(AmsServiceTarget target, string agentId, string deploymentId, string agentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        await SendAsync(new AmsMcsCall(HttpMethod.Delete, AmsRoutes.Deployment(target, agentId, deploymentId), target, PowerPlatformApiPermissions.AppsUpdate, "Deployment delete") { AgentType = agentType }, cancellationToken).ConfigureAwait(false);
    }

    internal static HttpRequestMessage PreferResponse(HttpRequestMessage request, bool respondAsync)
    {
        request.Headers.TryAddWhenPresent(PreferHeaderName, respondAsync ? RespondAsyncPreference : null);
        return request;
    }

    private static T Read<T>(AmsMcsResponse response, Func<JsonElement, T> read)
    {
        try
        {
            return AmsMcsWire.Parse(response.Body, read);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw AgentDeployException.InvalidResponse(response.Phase, response.Correlation.ClientRequestId, response.Correlation.ServiceRequestId);
        }
    }

    private static AmsMcsOperationAccepted ReadStarted(AmsMcsResponse response) => Read<AmsMcsOperationAccepted>(response, response.Status == HttpStatusCode.OK ? AmsMcsWire.ReadEndedOperation : AmsMcsWire.ReadOperationAccepted);

    private Task<AmsMcsResponse> SendAsync(AmsMcsCall call, CancellationToken cancellationToken) =>
        AmsRequestAttempt.SendAsync(call.RecoversLostAnswer, attempt => SendAttemptAsync(call, attempt, cancellationToken));

    private async Task<AmsMcsResponse> SendAttemptAsync(AmsMcsCall call, AmsRequestAttempt attempt, CancellationToken cancellationToken)
    {
        var mutation = call.Method != HttpMethod.Get;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await _sender.SendAsync(call.Target, call.Permission, call.OnBehalfOf, () => Request(call, attempt), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var correlation = new ServiceCorrelation(attempt.ReportedClientRequestId, ServiceCorrelation.ReadServiceRequestId(response));
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                throw AgentDeployException.RedirectRejected(call.Phase, (int)response.StatusCode, call.Target.Endpoint.Host);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new AmsMcsResponse(body, correlation, call.Phase, response.StatusCode, response.Headers.ETag?.ToString())
                : throw attempt.Refused(response.StatusCode, AmsMcsOperationErrorMap.Map((int)response.StatusCode, body, call.Method.Method, call.Uri, correlation, RetryAfter(response.Headers.RetryAfter)));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw attempt.AnswerLost(AgentDeployException.TimedOut(call.Phase, attempt.ReportedClientRequestId, mutation, exception));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw attempt.AnswerLost(AgentDeployException.TransportFailed(call.Phase, attempt.ReportedClientRequestId, mutation, exception));
        }
        catch (InvalidOperationException exception)
        {
            throw AgentDeployException.TransportFailed(call.Phase, attempt.ReportedClientRequestId, mutation, exception);
        }
    }

    private static HttpRequestMessage Request(AmsMcsCall call, AmsRequestAttempt attempt)
    {
        var request = new HttpRequestMessage(call.Method, call.Uri) { Content = call.Body is null ? null : JsonContent.Create(call.Body, call.Body.GetType(), options: AmsMcsWire.RequestJson) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
        request.Headers.TryAddWithoutValidation(ServiceCorrelation.ClientRequestIdHeader, attempt.ClientRequestId);
        request.Headers.TryAddWhenPresent(AmsAgentCreationAdapter.IdempotencyKeyHeaderName, call.IdempotencyKey);
        request.Headers.TryAddWhenPresent(AgentTypeHeaderName, call.AgentType);
        request.Headers.TryAddWhenPresent(IfMatchHeaderName, call.IfMatch);
        return PreferResponse(request, call.StartsOperation && attempt.PrefersRespondAsync(call.RespondAsync));
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header) =>
        header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

    private sealed record AmsMcsCall(HttpMethod Method, Uri Uri, AmsServiceTarget Target, string Permission, string Phase)
    {
        public object? Body { get; init; }

        public string? IdempotencyKey { get; init; }

        public bool OnBehalfOf { get; init; }

        public string? AgentType { get; init; }

        public string? IfMatch { get; init; }

        public bool RespondAsync { get; init; }

        public bool StartsOperation { get; init; }

        public bool RecoversLostAnswer { get; init; }
    }

    private sealed record AmsMcsResponse(string Body, ServiceCorrelation Correlation, string Phase, HttpStatusCode Status, string? ETag);

    private sealed record DeployWireRequest([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitSha, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Publish, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Overwrite);

    private sealed record CaptureWireRequest([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Resolve);

    private sealed record DeploymentCreateWireRequest(string DeploymentId, string AgentType, string DeploymentType, string DeploymentName, string EnvironmentId);

    private sealed record DeploymentRenameWireRequest(string DeploymentName);

    private sealed record RegisterWireRequest(string AgentId, string DisplayName, string? Description, string AgentType, string Origin, RegisterMcsWire Mcs, RegisterSourceControlWire SourceControl, IReadOnlyList<RegisterDeploymentWire> Deployments);

    private sealed record RegisterMcsWire(string? SchemaName, string? AuthoringShape);

    private sealed record RegisterSourceControlWire(string Repository, string? RepositoryId, string Branch, string AgentFolder);

    private sealed record RegisterDeploymentWire(string DeploymentId, string DeploymentType, string DeploymentName, string EnvironmentId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkflowId);

    private sealed record DeployRecordWireRequest([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BotId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkflowId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitSha, bool Exact, bool Published, DateTime DeployedTime);
}
