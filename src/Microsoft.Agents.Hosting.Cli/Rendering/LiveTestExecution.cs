using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

internal interface IStreamingFailureResult
{
    int? ErrorCode { get; }

    bool IsCancellation => false;
}

/// <summary>Runs one live turn and owns its output from validation through final flush.</summary>
public sealed class LiveTestExecution : ILiveTestStreamingResult, IStreamingFailureResult
{
    private readonly LiveTestRequest _request;
    private readonly Func<LiveTestRequest, LiveTestDestination> _resolve;
    private readonly Func<LiveTestDestination, bool, CancellationToken, Task<string>> _acquireToken;
    private readonly Func<LiveTestDestination, string, CancellationToken, ILiveTestSession> _createSession;
    private int? _errorCode;
    private bool _isCancellation;

    int? IStreamingFailureResult.ErrorCode => _errorCode;

    bool IStreamingFailureResult.IsCancellation => _isCancellation;

    public LiveTestExecution(
        LiveTestRequest request,
        Func<LiveTestRequest, LiveTestDestination> resolve,
        Func<LiveTestDestination, bool, CancellationToken, Task<string>> acquireToken,
        Func<LiveTestDestination, string, CancellationToken, ILiveTestSession> createSession)
    {
        _request = request;
        _resolve = resolve;
        _acquireToken = acquireToken;
        _createSession = createSession;
    }

    public async Task<int> RenderAsync(TextWriter stdout, TextWriter stderr, bool color, CancellationToken cancellationToken)
    {
        var json = _request.Json ? new LiveTestJsonSink(stdout) : null;
        var human = json is null
            ? new LiveTestHumanRenderer(stdout,
                plainOutput: _request.Plain || !ReferenceEquals(stdout, Console.Out) || Console.IsOutputRedirected,
                color: color, error: stderr)
            : null;
        if (json is not null)
        {
            try
            {
                await json.StartAsync();
            }
            catch (Exception failure)
            {
                _errorCode = ErrorCodes.General.Unknown;
                stderr.WriteLine(IsOutputFailure(failure)
                    ? "The live-test result output failed."
                    : "The live test could not initialize its result.");
                return 1;
            }
        }

        string? targetSource = null;
        string? conversationId = _request.ConversationId;
        string conversationSource = conversationId is null ? "none" : "supplied";
        string delivery = "not-attempted";
        TurnError? turnError = null;
        try
        {
            Validate(_request);
            var attachments = await ReadAttachmentsAsync(_request.Files, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var target = _resolve(_request);
            targetSource = target.TargetSource;
            var token = await _acquireToken(target, _request.NonInteractive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await using var session = _createSession(target, token, cancellationToken);
            if (conversationId is null)
            {
                await foreach (var activity in session.StartAsync(cancellationToken).WithCancellation(cancellationToken))
                {
                    await WriteActivity(activity);
                    ConfirmId(session.ServiceConversationId, ref conversationId, ref conversationSource);
                    ConfirmId(ActivityId(activity), ref conversationId, ref conversationSource);
                }
                ConfirmId(session.ServiceConversationId, ref conversationId, ref conversationSource);
                if (conversationId is null)
                    throw new LiveTestFailure("protocol-error", 1,
                        "The service did not return a conversation ID.",
                        "Check that the published agent supports authenticated conversations.");
            }

            if (!_request.StartConversation)
            {
                delivery = "attempted";
                var replies = attachments.Count == 0
                    ? session.QueryAsync(_request.Query!, conversationId, cancellationToken)
                    : session.QueryAsync(_request.Query!, conversationId, attachments, cancellationToken);
                await foreach (var activity in replies.WithCancellation(cancellationToken))
                {
                    ConfirmId(session.ServiceConversationId, ref conversationId, ref conversationSource);
                    ConfirmId(ActivityId(activity), ref conversationId, ref conversationSource);
                    await WriteActivity(activity);
                }
                ConfirmId(session.ServiceConversationId, ref conversationId, ref conversationSource);
            }
        }
        catch (Exception failure)
        {
            _isCancellation = cancellationToken.IsCancellationRequested;
            _errorCode = _isCancellation ? null : failure switch
            {
                AgentHostingException typed => typed.ErrorCode,
                LiveTestFailure { ExitCode: ExitCodes.Usage } => ErrorCodes.General.Usage,
                OperationCanceledException => ErrorCodes.General.TransportTimeout,
                _ => ErrorCodes.General.Unknown,
            };
            turnError = Classify(failure, cancellationToken);
        }

        try
        {
            if (json is not null)
                await json.FinishAsync(new LiveTestResult(turnError?.Status ?? "completed", turnError?.ExitCode ?? 0,
                    targetSource, conversationId, conversationSource, delivery,
                    turnError?.Kind, turnError?.Message, turnError?.Remediation));
            else
            {
                if (conversationId is not null)
                    human!.WriteConversationId(conversationId);
                if (turnError is not null)
                    human!.WriteError($"{turnError.Message} {turnError.Remediation}");
                human!.Finish(sdkCompleted: turnError is null);
            }
        }
        catch (Exception failure)
        {
            _errorCode = ErrorCodes.General.Unknown;
            _isCancellation = false;
            stderr.WriteLine(IsOutputFailure(failure)
                ? "The live-test result output failed."
                : "The live test could not finalize its result.");
            return 1;
        }
        return turnError?.ExitCode ?? 0;

        async Task WriteActivity(JsonElement activity)
        {
            try
            {
                if (json is not null)
                    await json.WriteActivityAsync(activity);
                else
                {
                    human!.WriteActivity(activity);
                    await stdout.FlushAsync(cancellationToken);
                }
            }
            catch (Exception failure) when (IsOutputFailure(failure))
            {
                throw new LiveTestFailure("output-error", 1, "The result output failed.",
                    "Check the output destination; do not assume the remote query was cancelled.");
            }
        }
    }

    private static bool IsOutputFailure(Exception failure) =>
        failure is IOException or ObjectDisposedException or NotSupportedException;

    private static void Validate(LiveTestRequest request)
    {
        if (request.Files.Count > 5 || (request.StartConversation && request.Files.Count > 0))
            throw new LiveTestFailure("invalid-input", 2, "Attach at most five files to a query, not to --start-conversation.",
                "Send a query with up to five --file options, at most 10 MiB total.");
        if (request.StartConversation && (request.Query is not null || request.ConversationId is not null))
            throw new LiveTestFailure("invalid-input", 2, "--start-conversation cannot be combined with --query or --conversation-id.",
                "Start a new conversation without a query, or supply --query to send a message.");
        if (!request.StartConversation && string.IsNullOrWhiteSpace(request.Query))
            throw new LiveTestFailure("invalid-input", 2, "A nonblank --query is required.",
                "Supply one literal query with --query.");
        if (request.Json && request.Plain)
            throw new LiveTestFailure("invalid-input", 2, "--json and --plain cannot be combined.",
                "Select one output format.");
        foreach (var (name, value) in new (string, string?)[]
            {
                ("--project", request.Project), ("--agent-id", request.AgentId),
                ("--agent-name", request.AgentName), ("--schema-name", request.SchemaName),
                ("--deployment-id", request.DeploymentId), ("--deployment-name", request.DeploymentName),
                ("--environment-id", request.EnvironmentId), ("--tenant", request.Tenant),
                ("--conversation-id", request.ConversationId),
            })
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new LiveTestFailure("invalid-input", 2, $"A nonblank {name} is required.",
                    $"Supply a value for {name} or omit it.");
        if (request.ConversationId is { } id &&
            (id is "." or ".." || id.Any(character =>
                char.IsControl(character) || character is '/' or '\\' or '?' or '#' or '%')))
            throw new LiveTestFailure("invalid-input", 2, "The --conversation-id is not a safe identifier.",
                "Supply the conversation ID returned by the selected agent.");
    }

    private static async Task<IReadOnlyList<LiveTestAttachment>> ReadAttachmentsAsync(
        IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var attachments = new List<LiveTestAttachment>();
        long remaining = 10 * 1024 * 1024;
        try
        {
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > remaining)
                    throw new LiveTestFailure("invalid-input", 2, "Attachments exceed the 10 MiB total limit.",
                        "Select smaller files.");
                var bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                if (stream.ReadByte() != -1)
                    throw new LiveTestFailure("invalid-input", 2, "An attachment changed while being read.",
                        "Finish editing the file before sending it.");
                remaining -= bytes.Length;
                var contentType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".pdf" => "application/pdf",
                    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                    ".txt" => "text/plain",
                    ".md" => "text/markdown",
                    ".csv" => "text/csv",
                    ".json" => "application/json",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream",
                };
                attachments.Add(new(Path.GetFileName(path), contentType, bytes));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new LiveTestFailure("invalid-input", 2, "An attachment could not be read.",
                "Select readable local files, at most 10 MiB total.");
        }
        return attachments;
    }

    private static string? ActivityId(JsonElement activity)
    {
        if (!activity.TryGetProperty("conversation", out var conversation)
            || conversation.ValueKind != JsonValueKind.Object
            || !conversation.TryGetProperty("id", out var id))
            return null;
        if (id.ValueKind != JsonValueKind.String)
            throw new LiveTestFailure("protocol-error", 1, "The service returned invalid conversation metadata.",
                "Check the published agent response.");
        return id.GetString();
    }

    private static void ConfirmId(string? observed, ref string? known, ref string source)
    {
        if (string.IsNullOrWhiteSpace(observed))
            return;
        if (known is not null && !string.Equals(known, observed, StringComparison.Ordinal))
            throw new LiveTestFailure("protocol-error", 1, "The service returned conflicting conversation metadata.",
                "Check the selected destination before another query.");
        known = observed;
        source = "service";
    }

    private sealed record TurnError(string Status, int ExitCode, string Kind, string Message, string Remediation);

    private static TurnError Classify(Exception failure, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new("cancelled", 1, "cancelled", "Local testing was cancelled.",
                "Remote effects may continue if the query was already sent.");
        if (failure is LiveTestFailure typed)
            return new("failed", typed.ExitCode, typed.Kind, typed.Message, typed.Remediation);
        if (failure is LiveTestAttachmentsNotSupportedException unsupportedAttachments)
            return new("failed", 1, "unsupported-attachments", unsupportedAttachments.Message,
                "Use a session that supports attachments. Verify remote effects before retrying; a session wrapper may already have dispatched the query.");
        if (failure is LiveTestTargetResolutionException targetFailure)
            return new("failed", targetFailure.ExitCode, targetFailure.ErrorKind ?? "invalid-configuration",
                targetFailure.Message,
                targetFailure.Remediation ?? "Check the agent, deployment, schema, environment, and tenant options.");
        if (failure is ManagedAgentException)
            return new("failed", 2, "invalid-configuration", "The local agent configuration could not be read.",
                "Check --project and the managed-agent configuration.");
        if (failure is ActiveAccountMismatchException or TokenClaimException)
            return new("failed", 2, "identity-mismatch", "The authenticated identity does not match the selected destination.",
                "Select the intended account and tenant, then invoke the command again.");
        if (failure is NotSignedInException or InteractionRequiredException)
            return new("failed", 3, "authentication-required", "A selected signed-in user is required.",
                "Run `msagent auth create` for the intended account and cloud; allow consent or remove --non-interactive.");
        if (failure is CacheProtectionUnavailableException storage)
            return new("failed", 1, "credential-storage-unavailable", storage.Message,
                storage.Remediation ?? "Restore access to the platform credential storage; see error 12006.");
        if (failure is AuthenticationException)
            return new("failed", 1, "authentication-failed", "Delegated authentication failed.",
                "Check the selected account, public-client registration, and delegated consent.");
        if (failure is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            return new("failed", 1, "authorization-failed", "The service rejected delegated access.",
                "Check agent sharing and delegated Copilot Studio permissions before retrying.");
        if (failure is JsonException or FormatException or McsLiveTestProtocolException)
            return new("failed", 1, "protocol-error", "The service returned an invalid response.",
                "Check the published agent response.");
        if (failure is McsLiveTestTokenException)
            return new("failed", 1, "authentication-failed", "Delegated authentication failed.",
                "Check the selected account and delegated consent.");
        if (failure is HttpRequestException or IOException or OperationCanceledException)
            return new("failed", 1, "transport-error", "The service connection failed.",
                "Check the destination before retrying; a delivered query may have caused remote actions.");
        return new("failed", 1, "internal-error", "The live test could not complete.",
            "Check the CLI configuration and try again; verify remote effects first if the query was sent.");
    }
}
