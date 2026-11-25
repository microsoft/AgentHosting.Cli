using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>A bounded local file explicitly attached to a live query.</summary>
public sealed record LiveTestAttachment(string Name, string ContentType, byte[] Content);

/// <summary>One fully resolved public-cloud MCS destination.</summary>
public sealed record LiveTestDestination(
    string EnvironmentId,
    string TenantId,
    string SchemaName,
    string TargetSource,
    Uri? Endpoint = null);

/// <summary>One identity- and destination-bound invocation of the live MCS client.</summary>
public interface ILiveTestSession : IAsyncDisposable
{
    string? ServiceConversationId { get; }
    IAsyncEnumerable<JsonElement> StartAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<JsonElement> QueryAsync(string query, string conversationId, CancellationToken cancellationToken);
    /// <summary>
    /// Execute a query with explicit attachments. Existing implementations delegate an empty
    /// attachment list to the query-only overload and must override this method to support files.
    /// </summary>
    /// <exception cref="Microsoft.Agents.Hosting.Cli.Core.Operations.LiveTestAttachmentsNotSupportedException">
    /// The default implementation received attachments and did not send the query.
    /// </exception>
    IAsyncEnumerable<JsonElement> QueryAsync(string query, string conversationId,
        IReadOnlyList<LiveTestAttachment> attachments, CancellationToken cancellationToken) =>
        attachments.Count == 0
            ? QueryAsync(query, conversationId, cancellationToken)
            : throw new LiveTestAttachmentsNotSupportedException();
}

/// <summary>The default live-test session implementation rejected attachments before sending a query.</summary>
public sealed class LiveTestAttachmentsNotSupportedException : NotSupportedException
{
    internal LiveTestAttachmentsNotSupportedException()
        : base("This live-test session does not support file attachments.")
    {
    }
}

/// <summary>Privacy-safe category and guidance for a live-test failure.</summary>
public sealed class LiveTestFailure(string kind, int exitCode, string message, string remediation) : Exception(message)
{
    public string Kind { get; } = kind;
    public int ExitCode { get; } = exitCode;
    public string Remediation { get; } = remediation;
}
