using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestOutputTests
{
    [Fact]
    public async Task ActivitiesFlushBeforeTheTurnEndsAndFailureRetainsPartialData()
    {
        using var output = new FlushTrackingWriter();
        var sink = new LiveTestJsonSink(output);
        await sink.StartAsync();
        using var activity = JsonDocument.Parse("""{"type":"message","text":"hello","extension":{"value":9223372036854775808}}""");
        await sink.WriteActivityAsync(activity.RootElement);

        Assert.Equal(1, output.Flushes);
        Assert.Contains("9223372036854775808", output.ToString());

        await sink.FinishAsync(new LiveTestResult(
            Status: "failed", ExitCode: 1, TargetSource: "explicit",
            ConversationId: "known", ConversationIdSource: "supplied",
            QueryDelivery: "attempted", ErrorKind: "transport-error",
            ErrorMessage: "The service connection failed.",
            Remediation: "Check the destination before retrying."));

        using var result = JsonDocument.Parse(output.ToString());
        var root = result.RootElement;
        Assert.Equal("1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("hello", root.GetProperty("activities")[0].GetProperty("text").GetString());
        Assert.Equal(1, root.GetProperty("activityCount").GetInt32());
        Assert.True(root.GetProperty("partial").GetBoolean());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("completionBasis").ValueKind);
        Assert.Equal("known", root.GetProperty("conversationId").GetString());
        Assert.Equal("supplied", root.GetProperty("conversationIdSource").GetString());
    }

    [Fact]
    public async Task EmptyNormalEndHasSdkCompletionBasisAndNoFailureFields()
    {
        using var output = new StringWriter();
        var sink = new LiveTestJsonSink(output);
        await sink.StartAsync();
        await sink.FinishAsync(new LiveTestResult(
            Status: "completed", ExitCode: 0, TargetSource: "configured",
            ConversationId: null, ConversationIdSource: "none",
            QueryDelivery: "attempted"));
        using var result = JsonDocument.Parse(output.ToString());
        var root = result.RootElement;
        Assert.Equal(0, root.GetProperty("activities").GetArrayLength());
        Assert.Equal(0, root.GetProperty("activityCount").GetInt32());
        Assert.False(root.GetProperty("partial").GetBoolean());
        Assert.Equal("sdk-stream", root.GetProperty("completionBasis").GetString());
        Assert.False(root.TryGetProperty("errorKind", out _));
        Assert.True(root.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task LargeDeepActivitiesAreNotTruncatedByTheOutputSink()
    {
        var content = new string('x', 2 * 1024 * 1024);
        var nested = new string('[', 128) + "7" + new string(']', 128);
        using var activity = JsonDocument.Parse(
            $$"""{"type":"message","text":"{{content}}","extension":{{nested}}}""",
            new JsonDocumentOptions { MaxDepth = 256 });
        using var output = new StringWriter();
        var sink = new LiveTestJsonSink(output);
        await sink.StartAsync();
        await sink.WriteActivityAsync(activity.RootElement);
        await sink.FinishAsync(new LiveTestResult("completed", 0, "explicit", null, "none", "attempted"));
        using var result = JsonDocument.Parse(output.ToString(), new JsonDocumentOptions { MaxDepth = 256 });
        var emitted = result.RootElement.GetProperty("activities")[0];
        Assert.Equal(content.Length, emitted.GetProperty("text").GetString()!.Length);
        var value = emitted.GetProperty("extension");
        for (var index = 0; index < 128; index++) value = value[0];
        Assert.Equal(7, value.GetInt32());
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        public int Flushes { get; private set; }
        public override Task FlushAsync()
        {
            Flushes++;
            return base.FlushAsync();
        }
    }
}
