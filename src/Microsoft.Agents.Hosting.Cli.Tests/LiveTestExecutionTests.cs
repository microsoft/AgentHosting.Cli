using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestExecutionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task QueryReadsExplicitFilesAndPreservesTheirNamesAndBytes(int fileCount)
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var files = Enumerable.Range(1, fileCount)
                .Select(index => Path.Combine(directory.FullName, $"study-{index}.xlsx")).ToArray();
            foreach (var file in files)
                await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var session = new FakeSession();
            var execution = new LiveTestExecution(
                new LiveTestRequest { Query = "compare", Files = files, ConversationId = "existing", Json = true },
                _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
                (_, _, _) => Task.FromResult("token"), (_, _, _) => session);
            Assert.Equal(0, await execution.RenderAsync(new StringWriter(), new StringWriter(), false, CancellationToken.None));
            Assert.Equal(fileCount, session.Attachments.Count);
            for (var index = 0; index < files.Length; index++)
            {
                var attachment = session.Attachments[index];
                Assert.Equal(Path.GetFileName(files[index]), attachment.Name);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", attachment.ContentType);
                Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Content);
            }
            Assert.Equal(1, session.QueryCalls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LegacySessionReportsUnsupportedAttachmentsConservatively(
        bool suppliedConversation, bool jsonOutput)
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var file = Path.Combine(directory.FullName, "study.txt");
            await File.WriteAllTextAsync(file, "content");
            var session = new LegacySession();
            var execution = new LiveTestExecution(
                new LiveTestRequest
                {
                    Query = "compare", Files = [file],
                    ConversationId = suppliedConversation ? "existing" : null, Json = jsonOutput
                },
                _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
                (_, _, _) => Task.FromResult("token"), (_, _, _) => session);
            using var output = new StringWriter();
            using var error = new StringWriter();

            Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));

            if (jsonOutput)
            {
                using var result = JsonDocument.Parse(output.ToString());
                Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
                Assert.Equal("unsupported-attachments", result.RootElement.GetProperty("errorKind").GetString());
                Assert.Equal("This live-test session does not support file attachments.",
                    result.RootElement.GetProperty("errorMessage").GetString());
                Assert.Equal("attempted", result.RootElement.GetProperty("queryDelivery").GetString());
                Assert.Equal(!suppliedConversation, result.RootElement.GetProperty("partial").GetBoolean());
                Assert.Equal(suppliedConversation ? "existing" : "created",
                    result.RootElement.GetProperty("conversationId").GetString());
                Assert.Empty(error.ToString());
            }
            else
            {
                Assert.Contains("This live-test session does not support file attachments.", error.ToString(), StringComparison.Ordinal);
                Assert.Contains("Verify remote effects before retrying;", error.ToString(), StringComparison.Ordinal);
            }
            Assert.Equal(suppliedConversation ? 0 : 1, session.StartCalls);
            Assert.Equal(0, session.QueryCalls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task OtherAttachmentFailuresRemainAttempted()
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var file = Path.Combine(directory.FullName, "study.txt");
            await File.WriteAllTextAsync(file, "content");
            var session = new FakeSession { FailAttachmentSynchronously = true };
            var execution = new LiveTestExecution(
                new LiveTestRequest { Query = "compare", Files = [file], ConversationId = "existing", Json = true },
                _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
                (_, _, _) => Task.FromResult("token"), (_, _, _) => session);
            using var output = new StringWriter();

            Assert.Equal(1, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));

            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal("internal-error", result.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal("attempted", result.RootElement.GetProperty("queryDelivery").GetString());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task AttachmentFailurePropagatedByWrapperRemainsAttempted()
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var file = Path.Combine(directory.FullName, "study.txt");
            await File.WriteAllTextAsync(file, "content");
            var session = new ForwardingSession();
            var execution = new LiveTestExecution(
                new LiveTestRequest { Query = "compare", Files = [file], ConversationId = "existing", Json = true },
                _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
                (_, _, _) => Task.FromResult("token"), (_, _, _) => session);
            using var output = new StringWriter();

            Assert.Equal(1, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));

            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal("unsupported-attachments", result.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal("attempted", result.RootElement.GetProperty("queryDelivery").GetString());
            Assert.False(result.RootElement.GetProperty("partial").GetBoolean());
            Assert.Equal(1, session.DispatchCalls);
            Assert.Equal(0, session.LegacySession.QueryCalls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(1, true)]
    public async Task InvalidFileCombinationsFailBeforeResolutionOrAuthentication(int fileCount, bool startConversation)
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var files = Enumerable.Range(1, fileCount)
                .Select(index => Path.Combine(directory.FullName, $"study-{index}.txt")).ToArray();
            foreach (var file in files)
                await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var execution = new LiveTestExecution(
                new LiveTestRequest
                {
                    Query = startConversation ? null : "compare",
                    StartConversation = startConversation,
                    Files = files,
                    Json = true
                },
                _ => throw new InvalidOperationException("Resolution must not run."),
                (_, _, _) => throw new InvalidOperationException("Authentication must not run."),
                (_, _, _) => throw new InvalidOperationException("Session must not start."));
            using var output = new StringWriter();
            using var error = new StringWriter();

            Assert.Equal(2, await execution.RenderAsync(output, error, false, CancellationToken.None));

            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal("invalid-input", result.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal("Attach at most five files to a query, not to --start-conversation.",
                result.RootElement.GetProperty("errorMessage").GetString());
            Assert.Equal("not-attempted", result.RootElement.GetProperty("queryDelivery").GetString());
            Assert.Equal(0, result.RootElement.GetProperty("activityCount").GetInt32());
            Assert.Empty(error.ToString());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableAndOversizedFilesFailBeforeAuthentication(bool oversized)
    {
        var directory = Directory.CreateTempSubdirectory("msagent-file-test-");
        try
        {
            var file = Path.Combine(directory.FullName, "study.pdf");
            if (oversized)
            {
                using var stream = File.Create(file);
                stream.SetLength(10 * 1024 * 1024 + 1);
            }
            var execution = new LiveTestExecution(
                new LiveTestRequest { Query = "compare", Files = [file], Json = true },
                _ => throw new InvalidOperationException("Resolution must not run."),
                (_, _, _) => throw new InvalidOperationException("Auth must not run."),
                (_, _, _) => throw new InvalidOperationException("Session must not start."));
            using var output = new StringWriter();
            Assert.Equal(2, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));
            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal("invalid-input", result.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal("not-attempted", result.RootElement.GetProperty("queryDelivery").GetString());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetResolutionFailurePreservesSafeGuidanceWithoutStartingSdk(bool jsonOutput)
    {
        const string message = "The selected managed agent has no configured default deployment.";
        const string remediation = "Supply --deployment-id or --deployment-name; a singleton non-default deployment is not used.";
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello", Json = jsonOutput },
            _ => throw LiveTestTargetResolutionException.InvalidConfiguration(message, remediation),
            (_, _, _) => throw new InvalidOperationException("Authentication must not run."),
            (_, _, _) => throw new InvalidOperationException("SDK session must not be created."));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, await execution.RenderAsync(output, error, false, CancellationToken.None));

        if (jsonOutput)
        {
            using var json = JsonDocument.Parse(output.ToString());
            var result = json.RootElement;
            Assert.Equal("invalid-configuration", result.GetProperty("errorKind").GetString());
            Assert.Equal(message, result.GetProperty("errorMessage").GetString());
            Assert.Equal(remediation, result.GetProperty("remediation").GetString());
            Assert.Equal("not-attempted", result.GetProperty("queryDelivery").GetString());
            Assert.Equal(0, result.GetProperty("activityCount").GetInt32());
            Assert.Empty(error.ToString());
        }
        else
        {
            Assert.Equal($"CLI: {message} {remediation}{Environment.NewLine}", error.ToString());
            Assert.Empty(output.ToString());
        }
    }

    [Fact]
    public async Task CredentialStorageFailureIsNotReportedAsSignIn()
    {
        const string message = "Owned protected storage is unavailable (macOS: native-status--25293).";
        const string remediation = "Check native storage availability.";
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello", Json = true },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "configured"),
            (_, _, _) => throw new CacheProtectionUnavailableException(message, remediation),
            (_, _, _) => throw new InvalidOperationException("SDK session must not be created."));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));

        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement;
        Assert.Equal("credential-storage-unavailable", result.GetProperty("errorKind").GetString());
        Assert.Equal(message, result.GetProperty("errorMessage").GetString());
        Assert.Equal(remediation, result.GetProperty("remediation").GetString());
        Assert.Equal("not-attempted", result.GetProperty("queryDelivery").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanFailureReportsOnlySpecificErrorBeforeOrAfterSdkStarts(bool sdkStarted)
    {
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello", ConversationId = "supplied" },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "configured"),
            (_, _, _) => sdkStarted
                ? Task.FromResult("token")
                : throw new IOException("do not leak the authentication response"),
            (_, _, _) => new FakeSession { FailQuery = true });
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));
        Assert.Equal(
            "CLI: The service connection failed. Check the destination before retrying; a delivered query may have caused remote actions."
            + Environment.NewLine,
            error.ToString());
        var conversationLine = $"CLI: Conversation ID: supplied{Environment.NewLine}";
        Assert.Equal(
            sdkStarted ? $"Agent: answer{Environment.NewLine}{conversationLine}" : conversationLine,
            output.ToString());
    }

    [Fact]
    public async Task UnclassifiedFailureReportsOneSafeFallback()
    {
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello" },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
            (_, _, _) => Task.FromResult("token"),
            (_, _, _) => throw new InvalidOperationException("do not leak the exception detail"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));

        Assert.Empty(output.ToString());
        Assert.Equal(
            "CLI: The live test could not complete. Check the CLI configuration and try again; verify remote effects first if the query was sent."
            + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public async Task NewConversationStreamsInitializationThenOneQuery()
    {
        var session = new FakeSession();
        var request = new LiveTestRequest { Query = "hello", Json = true };
        var execution = new LiveTestExecution(request,
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "configured"),
            (_, _, _) => Task.FromResult("token"),
            (_, _, _) => session);
        using var output = new StringWriter();
        var exit = await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(1, session.StartCalls);
        Assert.Equal(1, session.QueryCalls);
        Assert.Equal("hello", session.Query);
        Assert.Equal("confirmed", session.ConversationUsed);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("activityCount").GetInt32());
        Assert.Equal("init", root.GetProperty("activities")[0].GetProperty("text").GetString());
        Assert.Equal("answer", root.GetProperty("activities")[1].GetProperty("text").GetString());
        Assert.Equal("service", root.GetProperty("conversationIdSource").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task StartOnlyStreamsOpeningActivitiesAndReturnsConversationWithoutSendingAQuery()
    {
        var session = new FakeSession();
        var execution = new LiveTestExecution(
            new LiveTestRequest { StartConversation = true, Json = true },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
            (_, _, _) => Task.FromResult("token"),
            (_, _, _) => session);
        using var output = new StringWriter();

        Assert.Equal(0, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));

        Assert.Equal(1, session.StartCalls);
        Assert.Equal(0, session.QueryCalls);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement;
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("confirmed", result.GetProperty("conversationId").GetString());
        Assert.Equal("service", result.GetProperty("conversationIdSource").GetString());
        Assert.Equal("not-attempted", result.GetProperty("queryDelivery").GetString());
        Assert.Equal("init", result.GetProperty("activities")[0].GetProperty("text").GetString());
        Assert.Equal(1, result.GetProperty("activityCount").GetInt32());
    }

    [Theory]
    [InlineData("hello", null)]
    [InlineData("", null)]
    [InlineData(null, "existing")]
    public async Task StartOnlyRejectsQueryAndConversationIdBeforeResolvingTargetsOrAuthenticating(
        string? query, string? conversationId)
    {
        var execution = new LiveTestExecution(
            new LiveTestRequest { StartConversation = true, Query = query, ConversationId = conversationId, Json = true },
            _ => throw new InvalidOperationException("Target resolution must not run."),
            (_, _, _) => throw new InvalidOperationException("Authentication must not run."),
            (_, _, _) => throw new InvalidOperationException("Session must not be created."));
        using var output = new StringWriter();

        Assert.Equal(2, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-input", json.RootElement.GetProperty("errorKind").GetString());
        Assert.Equal("not-attempted", json.RootElement.GetProperty("queryDelivery").GetString());
    }

    [Fact]
    public async Task ContinuationFailureRetainsEarlierActivityAndNeverStartsReplacement()
    {
        var session = new FakeSession { FailQuery = true };
        var request = new LiveTestRequest { Query = "hello", Json = true, ConversationId = "supplied" };
        var execution = new LiveTestExecution(request,
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
            (_, _, _) => Task.FromResult("token"),
            (_, _, _) => session);
        using var output = new StringWriter();
        var exit = await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.Equal(0, session.StartCalls);
        Assert.Equal(1, session.QueryCalls);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("supplied", root.GetProperty("conversationIdSource").GetString());
        Assert.Equal("supplied", root.GetProperty("conversationId").GetString());
        Assert.Equal("attempted", root.GetProperty("queryDelivery").GetString());
        Assert.True(root.GetProperty("partial").GetBoolean());
        Assert.Equal(1, root.GetProperty("activityCount").GetInt32());
    }

    [Theory]
    [InlineData(null, false, "invalid-input")]
    [InlineData(" ", false, "invalid-input")]
    [InlineData("hello", true, "invalid-input")]
    public async Task InvalidInputNeverAcquiresCredentialsOrSends(
        string? query, bool plain, string expectedKind)
    {
        var authCalls = 0;
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = query, Json = true, Plain = plain },
            _ => throw new Exception("target resolution must not run"),
            (_, _, _) => { authCalls++; return Task.FromResult("token"); },
            (_, _, _) => throw new Exception("delivery must not run"));
        using var output = new StringWriter();
        Assert.Equal(2, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedKind, json.RootElement.GetProperty("errorKind").GetString());
        Assert.Equal("not-attempted", json.RootElement.GetProperty("queryDelivery").GetString());
        Assert.Equal(0, authCalls);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("conversation?token=value")]
    [InlineData("conversation%2Fother")]
    public async Task UnsafeConversationIdIsRejectedBeforeAuthOrDelivery(string conversationId)
    {
        var authCalls = 0;
        var sessionCalls = 0;
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello", Json = true, ConversationId = conversationId },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
            (_, _, _) => { authCalls++; return Task.FromResult("token"); },
            (_, _, _) => { sessionCalls++; return new FakeSession(); });
        using var output = new StringWriter();

        Assert.Equal(2, await execution.RenderAsync(output, new StringWriter(), false, CancellationToken.None));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("invalid-input", json.RootElement.GetProperty("errorKind").GetString());
        Assert.Equal("not-attempted", json.RootElement.GetProperty("queryDelivery").GetString());
        Assert.Equal(0, authCalls);
        Assert.Equal(0, sessionCalls);
    }

    [Fact]
    public async Task CallerCancellationBeforeAuthenticationDoesNotSendOrInventSuccess()
    {
        var sessionCalls = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var execution = new LiveTestExecution(
            new LiveTestRequest { Query = "hello", Json = true },
            _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
            (_, _, _) => Task.FromResult("token"),
            (_, _, _) => { sessionCalls++; return new FakeSession(); });
        using var output = new StringWriter();
        Assert.Equal(1, await execution.RenderAsync(output, new StringWriter(), false, cancellation.Token));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("cancelled", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("completionBasis").ValueKind);
        Assert.Equal("not-attempted", json.RootElement.GetProperty("queryDelivery").GetString());
        Assert.Equal(0, sessionCalls);
    }

    [Fact]
    public async Task OutputFailureOnOpeningIsOwnedByStreamingResult()
    {
        var execution = NewExecution();
        using var output = new FailingWriter(failOnWrite: 1);
        using var error = new StringWriter();

        Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));
        Assert.Contains("output", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("success", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputFailureOnFinalFlushIsReportedWithoutAnotherEnvelope()
    {
        var execution = NewExecution();
        using var output = new FailingWriter(failOnFlush: 2);
        using var error = new StringWriter();

        Assert.Equal(1, await execution.RenderAsync(output, error, false, CancellationToken.None));
        Assert.Contains("output", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, output.ToString().Split("\"schemaVersion\"").Length - 1);
    }

    private static LiveTestExecution NewExecution() => new(
        new LiveTestRequest { Query = "hello", Json = true, ConversationId = "supplied" },
        _ => new LiveTestDestination("synthetic", "tenant", "schema", "explicit"),
        (_, _, _) => Task.FromResult("token"),
        (_, _, _) => new FakeSession());

    private sealed class FailingWriter(int? failOnWrite = null, int? failOnFlush = null) : StringWriter
    {
        private int _writes;
        private int _flushes;

        public override Task WriteAsync(string? value)
        {
            if (++_writes == failOnWrite)
                throw new IOException("synthetic output failure");
            return base.WriteAsync(value);
        }

        public override Task FlushAsync()
        {
            if (++_flushes == failOnFlush)
                throw new IOException("synthetic output failure");
            return base.FlushAsync();
        }
    }

    private sealed class FakeSession : ILiveTestSession
    {
        public IReadOnlyList<LiveTestAttachment> Attachments { get; private set; } = [];
        public IAsyncEnumerable<JsonElement> QueryAsync(string query, string conversationId,
            IReadOnlyList<LiveTestAttachment> attachments, CancellationToken cancellationToken)
        {
            if (FailAttachmentSynchronously)
                throw new NotSupportedException("Do not infer delivery from an adapter failure.");
            Attachments = attachments;
            return QueryAsync(query, conversationId, cancellationToken);
        }
        public int StartCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public string? Query { get; private set; }
        public string? ConversationUsed { get; private set; }
        public bool FailQuery { get; init; }
        public bool FailAttachmentSynchronously { get; init; }
        public string? ServiceConversationId => StartCalls > 0 ? "confirmed" : null;

        public async IAsyncEnumerable<JsonElement> StartAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StartCalls++;
            yield return JsonDocument.Parse("""{"type":"message","text":"init"}""").RootElement.Clone();
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<JsonElement> QueryAsync(
            string query, string conversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            QueryCalls++;
            Query = query;
            ConversationUsed = conversationId;
            yield return JsonDocument.Parse("""{"type":"message","text":"answer"}""").RootElement.Clone();
            await Task.CompletedTask;
            if (FailQuery) throw new IOException("do not leak the service response");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacySession : ILiveTestSession
    {
        public int StartCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public string? ServiceConversationId => StartCalls > 0 ? "created" : null;

        public async IAsyncEnumerable<JsonElement> StartAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StartCalls++;
            yield return JsonSerializer.SerializeToElement(new { type = "message", text = "opening" });
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<JsonElement> QueryAsync(
            string query, string conversationId, CancellationToken cancellationToken)
        {
            QueryCalls++;
            throw new InvalidOperationException("An attachment must not be dropped.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ForwardingSession : ILiveTestSession
    {
        public LegacySession LegacySession { get; } = new();
        public int DispatchCalls { get; private set; }
        public string? ServiceConversationId => LegacySession.ServiceConversationId;

        public IAsyncEnumerable<JsonElement> StartAsync(CancellationToken cancellationToken) =>
            LegacySession.StartAsync(cancellationToken);

        public IAsyncEnumerable<JsonElement> QueryAsync(
            string query, string conversationId, CancellationToken cancellationToken) =>
            LegacySession.QueryAsync(query, conversationId, cancellationToken);

        public IAsyncEnumerable<JsonElement> QueryAsync(
            string query, string conversationId, IReadOnlyList<LiveTestAttachment> attachments,
            CancellationToken cancellationToken)
        {
            DispatchCalls++;
            return ((ILiveTestSession)LegacySession).QueryAsync(query, conversationId, attachments, cancellationToken);
        }

        public ValueTask DisposeAsync() => LegacySession.DisposeAsync();
    }
}
