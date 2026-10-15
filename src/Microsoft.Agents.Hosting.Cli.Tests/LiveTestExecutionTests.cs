using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestExecutionTests
{
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
        public int StartCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public string? Query { get; private set; }
        public string? ConversationUsed { get; private set; }
        public bool FailQuery { get; init; }
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
}
