using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>Terminal facts known when a live-test output stream is closed.</summary>
public sealed record LiveTestResult(
    string Status,
    int ExitCode,
    string? TargetSource,
    string? ConversationId,
    string ConversationIdSource,
    string QueryDelivery,
    string? ErrorKind = null,
    string? ErrorMessage = null,
    string? Remediation = null);

/// <summary>Owns the single streaming JSON document for one live test.</summary>
public sealed class LiveTestJsonSink
{
    private readonly TextWriter _output;
    private bool _started;
    private bool _finished;
    private int _activityCount;

    public LiveTestJsonSink(TextWriter output) => _output = output ?? throw new ArgumentNullException(nameof(output));

    public async Task StartAsync()
    {
        if (_started)
        {
            throw new InvalidOperationException("The live-test result has already started.");
        }

        _started = true;
        await _output.WriteAsync("{\"schemaVersion\":\"1\",\"activities\":[");
    }

    public async Task WriteActivityAsync(JsonElement activity)
    {
        if (!_started || _finished)
        {
            throw new InvalidOperationException("The live-test activity stream is not open.");
        }

        if (activity.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An activity must be a JSON object.");
        }

        await _output.WriteAsync(_activityCount == 0 ? "" : ",");
        await _output.WriteAsync(activity.GetRawText());
        await _output.FlushAsync();
        _activityCount++;
    }

    public async Task FinishAsync(LiveTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_started || _finished)
        {
            throw new InvalidOperationException("The live-test result cannot be closed twice.");
        }

        if (result.Status is not ("completed" or "failed" or "cancelled")
            || (result.Status == "completed") != (result.ExitCode == 0)
            || (result.Status == "completed" && result.ErrorKind is not null)
            || (result.Status != "completed" && (result.ErrorKind is null || result.ErrorMessage is null || result.Remediation is null)))
        {
            throw new ArgumentException("The live-test terminal outcome is inconsistent.", nameof(result));
        }

        _finished = true;
        await _output.WriteAsync("]");
        await FieldAsync("conversationId", result.ConversationId);
        await FieldAsync("conversationIdSource", result.ConversationIdSource);
        await FieldAsync("targetSource", result.TargetSource);
        await FieldAsync("completionBasis", result.Status == "completed" ? "sdk-stream" : null);
        await FieldAsync("status", result.Status);
        await _output.WriteAsync($",\"success\":{(result.Status == "completed" ? "true" : "false")},\"exitCode\":{result.ExitCode}");
        await FieldAsync("queryDelivery", result.QueryDelivery);
        await _output.WriteAsync($",\"partial\":{(result.Status != "completed" && _activityCount > 0 ? "true" : "false")},\"activityCount\":{_activityCount}");
        if (result.Status != "completed")
        {
            await FieldAsync("errorKind", result.ErrorKind);
            await FieldAsync("errorMessage", result.ErrorMessage);
            await FieldAsync("remediation", result.Remediation);
        }

        await _output.WriteAsync("}");
        await _output.FlushAsync();
    }

    private Task FieldAsync(string name, string? value) =>
        _output.WriteAsync($",\"{name}\":{JsonSerializer.Serialize(value)}");
}
