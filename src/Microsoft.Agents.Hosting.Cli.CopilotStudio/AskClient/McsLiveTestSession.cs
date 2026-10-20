using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

/// <summary>One explicitly selected public-cloud Copilot Studio destination.</summary>
public sealed record McsLiveTestTarget(
    string EnvironmentId,
    string TenantId,
    string SchemaName,
    LiveTestAgentType AgentType = LiveTestAgentType.Standard,
    Uri? Endpoint = null);

/// <summary>A single, non-retrying Copilot Studio conversation invocation.</summary>
public sealed class McsLiveTestSession : IDisposable, IAsyncDisposable, ILiveTestSession
{
    private const string CopilotStudioInvokeScope = "https://api.powerplatform.com/CopilotStudio.Copilots.Invoke";

    private readonly McsLiveTestTarget _target;
    private readonly ITokenProvider _tokenProvider;
    private readonly CancellationToken _cancellationToken;
    private readonly HttpClient _httpClient;
    private readonly CopilotClient _copilotClient;
    private bool _started;
    private bool _queryStarted;
    private bool _disposed;
    private string? _conversationId;
    private string? _serviceConversationId;
    private McsConversationIdSource _conversationIdSource;
    private Exception? _responseError;

    /// <summary>Creates a session for a public-cloud environment using a delegated bearer token.</summary>
    public McsLiveTestSession(
        string environmentId,
        string tenantId,
        string schemaName,
        string delegatedToken,
        HttpMessageHandler? transport = null)
        : this(new McsLiveTestTarget(environmentId, tenantId, schemaName), delegatedToken, transport)
    {
    }

    /// <summary>Creates a session using the supplied delegated bearer token.</summary>
    public McsLiveTestSession(McsLiveTestTarget target, string delegatedToken, HttpMessageHandler? transport = null)
        : this(target, new FixedTokenProvider(delegatedToken), transport)
    {
    }

    /// <summary>Creates a session using the CLI token provider for the resolved tenant.</summary>
    public McsLiveTestSession(
        McsLiveTestTarget target,
        ITokenProvider tokenProvider,
        HttpMessageHandler? transport = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        var endpoint = target.Endpoint ?? McsLiveTargetValidation.GetEndpoint(target);
        target = target with { Endpoint = endpoint };
        _target = target;
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _cancellationToken = cancellationToken;
        McsLiveActivityCodec.Initialize();

        var inner = transport ?? CreateTransport();
        _httpClient = new HttpClient(
            new McsResponseBridgeHandler(inner, target, SetServiceConversationId, error => _responseError = error),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _copilotClient = new CopilotClient(
            new ConnectionSettings
            {
                EnvironmentId = target.EnvironmentId,
                SchemaName = target.SchemaName,
                DirectConnectUrl = target.AgentType == LiveTestAgentType.GitHubCopilot ? endpoint.ToString() : null,
                UseExperimentalEndpoint = false,
                EnableDiagnostics = false,
            },
            new SingleHttpClientFactory(_httpClient),
            GetTokenAsync,
            NullLogger<CopilotClient>.Instance,
            "copilotstudio");
    }

    /// <summary>The service-confirmed or caller-supplied conversation ID, when known.</summary>
    public string? ConversationId => _conversationId;

    /// <summary>The conversation ID confirmed by the service, excluding caller-supplied IDs.</summary>
    public string? ServiceConversationId => _serviceConversationId;

    /// <summary>How <see cref="ConversationId" /> became known.</summary>
    public McsConversationIdSource ConversationIdSource => _conversationIdSource;

    /// <summary>The dedicated transport timeout, exposed for characterization tests.</summary>
    public TimeSpan HttpClientTimeout => _httpClient.Timeout;

    /// <summary>Starts a new conversation and yields each raw activity in service order.</summary>
    public async IAsyncEnumerable<JsonElement> StartAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_started)
        {
            throw new InvalidOperationException("This live-test session has already started a conversation.");
        }

        _started = true;
        _responseError = null;
        await foreach (var activity in ConvertActivitiesAsync(
            _copilotClient.StartConversationAsync(true, cancellationToken),
            cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return activity;
        }

        ThrowResponseError();
        if (_conversationId is null)
        {
            throw new McsLiveTestProtocolException("The service did not return a conversation ID.");
        }
    }

    /// <summary>
    /// Continues a supplied or service-confirmed conversation without starting a replacement.
    /// The query is sent exactly once and is not retried after an uncertain response.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> QueryAsync(
        string query,
        string? conversationId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(query);
        if (_queryStarted)
        {
            throw new InvalidOperationException("This live-test session has already sent its query.");
        }

        var id = conversationId ?? _conversationId;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("A conversation ID is required before querying.");
        }

        McsLiveTargetValidation.ValidateConversationId(id);
        if (_conversationId is not null &&
            !string.Equals(_conversationId, id, StringComparison.Ordinal))
        {
            throw new McsLiveTestProtocolException("The conversation ID does not match this session.");
        }

        if (conversationId is not null)
        {
            _conversationId = conversationId;
            _conversationIdSource = McsConversationIdSource.Supplied;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _responseError = null;
        _queryStarted = true;
        await foreach (var activity in ConvertActivitiesAsync(
            _copilotClient.AskQuestionAsync(query, id, cancellationToken),
            cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return activity;
        }

        ThrowResponseError();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> GetTokenAsync(string resource)
    {
        if (!McsLiveTargetValidation.IsExpectedRequestResource(resource, _target))
        {
            throw new McsLiveTestTokenException("The transport requested an unapproved Copilot Studio resource.");
        }

        var result = await _tokenProvider
            .AcquireTokenSilentAsync([CopilotStudioInvokeScope], _target.TenantId, _cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            throw new McsLiveTestTokenException("The delegated token provider returned an empty token.");
        }

        if (!Guid.TryParse(result.TenantId, out var tokenTenantId) ||
            !Guid.TryParse(_target.TenantId, out var targetTenantId) ||
            tokenTenantId != targetTenantId)
        {
            throw new McsLiveTestTokenException("The delegated token does not match the selected tenant.");
        }

        return result.AccessToken;
    }

    private void ThrowResponseError()
    {
        if (_responseError is { } error)
        {
            _responseError = null;
            throw error;
        }
    }

    private sealed class FixedTokenProvider : ITokenProvider
    {
        private readonly string _delegatedToken;

        public FixedTokenProvider(string delegatedToken)
        {
            if (string.IsNullOrWhiteSpace(delegatedToken))
            {
                throw new ArgumentException("The delegated token is empty.", nameof(delegatedToken));
            }

            _delegatedToken = delegatedToken;
        }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateResult(targetTenantId));

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateResult(targetTenantId));

        private TokenAcquisitionResult CreateResult(string? targetTenantId) =>
            new()
            {
                AccessToken = _delegatedToken,
                TenantId = targetTenantId,
                ExpiresOn = DateTimeOffset.MaxValue,
            };
    }

    private async IAsyncEnumerable<JsonElement> ConvertActivitiesAsync(
        IAsyncEnumerable<IActivity> activities,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = activities
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false)
            .GetAsyncEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new McsLiveTestProtocolException("The service returned an invalid response.");
            }

            if (!moved)
            {
                yield break;
            }

            var activity = enumerator.Current;
            if (!string.IsNullOrWhiteSpace(activity.Conversation?.Id))
            {
                SetServiceConversationId(activity.Conversation.Id);
            }

            yield return McsLiveActivityCodec.ToJsonElement(activity);
        }
    }

    private void SetServiceConversationId(string conversationId)
    {
        McsLiveTargetValidation.ValidateConversationId(conversationId);
        if (_conversationId is not null &&
            !string.Equals(_conversationId, conversationId, StringComparison.Ordinal))
        {
            throw new McsLiveTestProtocolException("The service returned conflicting conversation IDs.");
        }

        _conversationId = conversationId;
        _serviceConversationId = conversationId;
        _conversationIdSource = McsConversationIdSource.Service;
    }

    internal static HttpClientHandler CreateTransport()
    {
        var handler = TransportDefaults.CreateRedirectRejectingHandler();
        handler.UseCookies = false;
        return handler;
    }

    private static void ValidateTarget(McsLiveTestTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        McsLiveTargetValidation.ValidateEnvironmentId(target.EnvironmentId);
        McsLiveTargetValidation.ValidateTenantId(target.TenantId);
        McsLiveTargetValidation.ValidatePathSegment(target.SchemaName, nameof(target.SchemaName));
        if (!Enum.IsDefined(target.AgentType) ||
            target.Endpoint is not null && target.Endpoint != McsLiveTargetValidation.GetEndpoint(target))
        {
            throw new ArgumentException("The selected runtime endpoint is invalid.", nameof(target));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(McsLiveTestSession));
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

/// <summary>Provenance of a live-test conversation ID.</summary>
public enum McsConversationIdSource
{
    /// <summary>No conversation ID is known.</summary>
    None,
    /// <summary>The conversation ID was supplied by the caller.</summary>
    Supplied,
    /// <summary>The conversation ID was returned by the service.</summary>
    Service,
}

/// <summary>A safe transport failure that intentionally excludes response content.</summary>
public sealed class McsLiveTestTransportException : HttpRequestException
{
    /// <summary>Creates a safe transport exception without response content.</summary>
    public McsLiveTestTransportException(HttpStatusCode? statusCode, string message, Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the unsuccessful status code, when one was returned.</summary>
    public new HttpStatusCode? StatusCode { get; }
}

/// <summary>A protocol failure raised by the response bridge or session guards.</summary>
public sealed class McsLiveTestProtocolException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>A token or authority validation failure.</summary>
public sealed class McsLiveTestTokenException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
