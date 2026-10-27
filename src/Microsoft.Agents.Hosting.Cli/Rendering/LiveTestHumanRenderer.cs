using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>
/// Renders live-test activities for a human without interpreting returned content as commands.
/// </summary>
public sealed class LiveTestHumanRenderer
{
    private const string CompletedStatus = "SDK response completed; remote action success is not verified";
    private const string MissingFinalWarning = "No final message was supplied for a streamed response";

    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _plainOutput;
    private readonly bool _color;
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private string? _activeTransientText;
    private bool _finished;

    /// <summary>Creates a renderer that writes human output to <paramref name="output"/>.</summary>
    /// <param name="output">The human-output sink.</param>
    /// <param name="plainOutput">
    /// When true, transient stream updates are omitted and output is never repainted.
    /// </param>
    /// <param name="color">Whether CLI-authored labels may use ANSI color.</param>
    /// <param name="error">
    /// Optional human-error sink. When omitted, <paramref name="output"/> is used.
    /// </param>
    public LiveTestHumanRenderer(
        TextWriter output,
        bool plainOutput = false,
        bool color = false,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _error = error ?? output;
        _plainOutput = plainOutput;
        _color = color;
    }

    internal int RetainedStreamTextLength =>
        _streams.Values.Sum(static stream => stream.Text.Length);

    internal int RetainedStreamStateCount => _streams.Count;

    /// <summary>Writes one raw activity without changing or retaining its JSON value.</summary>
    public void WriteActivity(JsonElement activity)
    {
        ThrowIfFinished();

        if (activity.ValueKind != JsonValueKind.Object)
        {
            WriteActivitySummary(activity);
            return;
        }

        if (HasUnknownDelta(activity))
        {
            WriteActivitySummary(activity);
            return;
        }

        if (TryGetStreamInfo(activity, out var streamInfo))
        {
            RenderStreamActivity(activity, streamInfo);
            return;
        }

        var rendered = false;
        if (TryGetString(activity, "text", out var text) && !string.IsNullOrEmpty(text))
        {
            WriteAgentLine(text);
            rendered = true;
        }

        rendered |= RenderCitations(activity);
        rendered |= RenderAttachments(activity);

        if (!rendered && (HasProperty(activity, "channelData") || HasProperty(activity, "delta")))
        {
            WriteActivitySummary(activity);
            return;
        }

        if (!rendered &&
            (!TryGetString(activity, "type", out var type) ||
             !string.Equals(type, "typing", StringComparison.OrdinalIgnoreCase)))
        {
            WriteActivitySummary(activity);
        }
    }

    /// <summary>Writes a CLI-authored status line to the human-output sink.</summary>
    public void WriteStatus(string message)
    {
        ThrowIfFinished();
        WriteCliLine(message, _output);
    }

    /// <summary>Writes a CLI-authored error line to the configured error sink.</summary>
    public void WriteError(string message)
    {
        ThrowIfFinished();
        WriteCliLine(message, _error);
    }

    /// <summary>Writes a known conversation identifier as requested human output.</summary>
    public void WriteConversationId(string conversationId)
    {
        ThrowIfFinished();
        WriteCliLine($"Conversation ID: {conversationId}", _output);
    }

    /// <summary>Finishes with the normal SDK-stream completion status.</summary>
    public void Finish() => Finish(sdkCompleted: true);

    /// <summary>
    /// Finishes the human rendering path. A false value reports that the SDK stream did not complete
    /// normally; it does not claim anything about remote work. Preflight failures omit SDK status.
    /// </summary>
    public void Finish(bool sdkCompleted, bool sdkStarted = true)
    {
        if (_finished)
        {
            return;
        }

        ClearTransient();

        foreach (var stream in _streams.Values)
        {
            if (!stream.Finalized)
            {
                WriteCliLine(MissingFinalWarning, _output);
            }
        }

        if (sdkStarted)
        {
            WriteCliLine(
                sdkCompleted ? CompletedStatus : "SDK response did not complete",
                _output);
        }
        _output.Flush();
        _finished = true;
    }

    private void RenderStreamActivity(JsonElement activity, StreamInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.StreamId) ||
            (info.StreamType is not "informative" and not "streaming" and not "final"))
        {
            WriteActivitySummary(activity);
            return;
        }

        if (!_streams.TryGetValue(info.StreamId, out var state))
        {
            state = new StreamState();
            _streams.Add(info.StreamId, state);
        }

        if (info.StreamType == "final")
        {
            if (state.Finalized || IsStale(state, info))
            {
                return;
            }

            state.Finalized = true;
            state.HasSequence = info.HasSequence;
            state.Sequence = info.Sequence;
            ClearTransient();

            var rendered = false;
            if (TryGetString(activity, "text", out var finalText) && !string.IsNullOrEmpty(finalText))
            {
                WriteAgentLine(finalText);
                rendered = true;
            }

            rendered |= RenderCitations(activity);
            rendered |= RenderAttachments(activity);
            if (!rendered)
            {
                WriteActivitySummary(activity);
            }

            state.Text = string.Empty;
            return;
        }

        if (state.Finalized || IsStale(state, info))
        {
            return;
        }

        state.HasSequence = info.HasSequence;
        state.Sequence = info.Sequence;
        state.Text = TryGetString(activity, "text", out var text) ? text : string.Empty;

        if (!_plainOutput && !string.IsNullOrEmpty(state.Text))
        {
            WriteTransient(state.Text);
        }
    }

    private static bool IsStale(StreamState state, StreamInfo info) =>
        state.HasSequence && info.HasSequence && info.Sequence <= state.Sequence;

    private static bool HasUnknownDelta(JsonElement activity)
    {
        if (HasProperty(activity, "delta"))
        {
            return true;
        }

        return TryGetProperty(activity, "channelData", out var channelData) &&
            channelData.ValueKind == JsonValueKind.Object &&
            HasProperty(channelData, "delta");
    }

    private bool RenderCitations(JsonElement activity)
    {
        var rendered = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (TryGetProperty(activity, "entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object ||
                    !TryGetString(entity, "type", out var type) ||
                    !string.Equals(type, "citation", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rendered |= RenderCitation(entity, seen);
            }
        }

        if (TryGetProperty(activity, "channelData", out var channelData) &&
            channelData.ValueKind == JsonValueKind.Object &&
            TryGetProperty(channelData, "citations", out var citations) &&
            citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in citations.EnumerateArray())
            {
                if (citation.ValueKind == JsonValueKind.Object)
                {
                    rendered |= RenderCitation(citation, seen);
                }
            }
        }

        return rendered;
    }

    private bool RenderCitation(JsonElement citation, HashSet<string> seen)
    {
        if (TryGetProperty(citation, "citation", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            citation = nested;
        }

        var url = FirstString(citation, "url", "targetUrl", "sourceUrl", "link");
        var title = FirstString(citation, "title", "label", "name", "sourceTitle");
        var number = FirstNumber(citation, "citationIndex", "position", "index", "number");
        var key = $"{number}|{title}|{url}";
        if (!seen.Add(key))
        {
            return false;
        }

        if (url is null)
        {
            WriteAgentLine($"Citation {(number is null ? string.Empty : $"[{number}] ")}{title ?? "untitled"} (target URL unavailable)");
        }
        else if (title is null)
        {
            WriteAgentLine($"Citation {(number is null ? string.Empty : $"[{number}] ")}{url}");
        }
        else
        {
            WriteAgentLine($"{(number is null ? "Citation" : $"[{number}]")} {title} — {url}");
        }

        return true;
    }

    private bool RenderAttachments(JsonElement activity)
    {
        if (!TryGetProperty(activity, "attachments", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var rendered = false;
        foreach (var attachment in attachments.EnumerateArray())
        {
            if (attachment.ValueKind != JsonValueKind.Object)
            {
                WriteAgentLine("Attachment: unsupported noninteractive value; see JSON for full data");
                rendered = true;
                continue;
            }

            var contentType = FirstString(attachment, "contentType", "mediaType") ?? "unknown";
            var name = FirstString(attachment, "name", "filename");
            var label = CardLabel(contentType);
            if (label is null)
            {
                WriteCliLine(
                    $"Unsupported attachment/card type '{contentType}'; noninteractive fallback; see JSON for full data",
                    _output);
            }
            else
            {
                WriteAgentLine($"Card ({label}) [noninteractive fallback]");
            }

            if (name is not null)
            {
                WriteAgentLine($"Attachment: {name} ({contentType})");
            }

            if (TryGetProperty(attachment, "content", out var content))
            {
                RenderCardContent(content);
            }

            rendered = true;
        }

        return rendered;
    }

    private void RenderCardContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            WriteAgentLine($"Text: {content.GetString()}");
            return;
        }

        if (content.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in content.EnumerateObject())
        {
            if (property.NameEquals("title") ||
                property.NameEquals("subtitle") ||
                property.NameEquals("text"))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    WriteAgentLine($"{property.Name}: {property.Value.GetString()}");
                }
            }
            else if (property.NameEquals("body") && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var bodyItem in property.Value.EnumerateArray())
                {
                    if (bodyItem.ValueKind == JsonValueKind.Object)
                    {
                        var bodyText = FirstString(bodyItem, "text", "title", "label");
                        if (bodyText is not null)
                        {
                            WriteAgentLine($"Text: {bodyText}");
                        }
                    }
                }
            }
        }

        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in EnumerateUrls(content))
        {
            if (urls.Add(url))
            {
                WriteAgentLine($"Link target: {url}");
            }
        }
    }

    private static IEnumerable<string> EnumerateUrls(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var actionType = FirstString(element, "type");
            foreach (var property in element.EnumerateObject())
            {
                if ((property.NameEquals("url") ||
                     property.NameEquals("targetUrl") ||
                     property.NameEquals("sourceUrl")) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return value;
                    }
                }

                if (property.NameEquals("value") &&
                    string.Equals(actionType, "openUrl", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return value;
                    }
                }

                foreach (var nested in EnumerateUrls(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateUrls(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private void WriteTransient(string text)
    {
        ClearTransient();

        var safeText = Sanitize(text).Replace('\n', ' ');
        _output.Write("Streaming: ");
        _output.Write(safeText);
        _activeTransientText = safeText;
    }

    private void ClearTransient()
    {
        if (_plainOutput || _activeTransientText is null)
        {
            _activeTransientText = null;
            return;
        }

        _output.Write('\r');
        _output.Write(new string(' ', Math.Max("Streaming: ".Length + _activeTransientText.Length, 1)));
        _output.Write('\r');
        _activeTransientText = null;
    }

    private void WriteAgentLine(string text)
    {
        ClearTransient();
        WriteLabelLine("Agent:", text, _output, "36");
    }

    private void WriteActivitySummary(JsonElement activity)
    {
        ClearTransient();
        var type = FirstString(activity, "type");
        var name = FirstString(activity, "name");
        var label = string.IsNullOrWhiteSpace(type) ? "unknown" : type;
        if (!string.IsNullOrWhiteSpace(name))
        {
            label += $" ({name})";
        }

        WriteCliLine($"Activity received: {label}", _output);
    }

    private void WriteCliLine(string text, TextWriter writer)
    {
        writer.Write(Colorize("CLI:", "36"));
        writer.Write(' ');
        writer.WriteLine(Sanitize(text));
    }

    private void WriteLabelLine(string label, string text, TextWriter writer, string colorCode)
    {
        writer.Write(Colorize(label, colorCode));
        writer.Write(' ');
        writer.WriteLine(Sanitize(text));
    }

    private string Colorize(string label, string colorCode) =>
        _color ? $"\u001b[{colorCode}m{label}\u001b[0m" : label;

    private static string Sanitize(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return string.Join(
            '\n',
            value.Split('\n').Select(TerminalText.Sanitize));
    }

    private static string? CardLabel(string contentType)
    {
        if (contentType.Contains("adaptive", StringComparison.OrdinalIgnoreCase))
        {
            return "adaptive";
        }

        if (contentType.Contains("hero", StringComparison.OrdinalIgnoreCase))
        {
            return "hero";
        }

        if (contentType.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return "thumbnail";
        }

        return null;
    }

    private static bool TryGetStreamInfo(JsonElement activity, out StreamInfo info)
    {
        if (TryGetProperty(activity, "entities", out var entities) &&
            entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object ||
                    !TryGetString(entity, "type", out var type) ||
                    !string.Equals(type, "streaminfo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var streamId = FirstString(entity, "streamId", "streamID");
                var streamType = FirstString(entity, "streamType", "streamtype")?.ToLowerInvariant();
                var hasSequence = TryGetSequence(entity, out var sequence);
                info = new StreamInfo(streamId, streamType, hasSequence, sequence);
                return true;
            }
        }

        info = default;
        return false;
    }

    private static bool TryGetSequence(JsonElement element, out long sequence)
    {
        if (TryGetProperty(element, "streamSequence", out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out sequence))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence))
            {
                return true;
            }
        }

        sequence = 0;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetString(element, name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool HasProperty(JsonElement element, string name) =>
        TryGetProperty(element, name, out _);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The live-test human renderer has already finished.");
        }
    }

    private readonly record struct StreamInfo(
        string? StreamId,
        string? StreamType,
        bool HasSequence,
        long Sequence);

    private sealed class StreamState
    {
        public bool Finalized { get; set; }
        public bool HasSequence { get; set; }
        public long Sequence { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
