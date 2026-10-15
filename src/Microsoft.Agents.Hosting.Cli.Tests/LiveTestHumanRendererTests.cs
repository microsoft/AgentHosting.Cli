using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestHumanRendererTests
{
    [Fact]
    public void PlainOutputRendersOnlyTheFinalStreamSnapshot()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: true);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "Hello",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 1, "streamType": "streaming" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "Hello world",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 2, "streamType": "streaming" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "Hello world",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 3, "streamType": "final" }
              ]
            }
            """));
        renderer.Finish();

        Assert.Equal(
            "Agent: Hello world\nCLI: SDK response completed; remote action success is not verified\n",
            Normalize(output.ToString()));
    }

    [Fact]
    public void CapableOutputReplacesProgressAndIgnoresStaleSequences()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: false);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "A",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 1, "streamType": "streaming" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "AB",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 2, "streamType": "streaming" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "stale",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 1, "streamType": "streaming" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "AB",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 3, "streamType": "final" }
              ]
            }
            """));
        renderer.Finish();

        var rendered = output.ToString();
        Assert.Equal(2, rendered.Split("Streaming:").Length - 1);
        Assert.DoesNotContain("stale", rendered);
        Assert.Equal(1, rendered.Split("Agent:").Length - 1);
        Assert.Contains("Agent: AB", rendered);
    }

    [Fact]
    public void MissingFinalEmitsWarningWithoutInventingAnswer()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: false);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "Still working",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 1, "streamType": "informative" }
              ]
            }
            """));
        renderer.Finish();

        var rendered = Normalize(output.ToString());
        Assert.Contains("CLI: No final message was supplied for a streamed response", rendered);
        Assert.DoesNotContain("Agent:", rendered);
        Assert.Contains("CLI: SDK response completed; remote action success is not verified", rendered);
    }

    [Fact]
    public void RendersCitationsAndCardFallbacksWithoutExecutingReturnedContent()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: true);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "Answer\u001b[31m\u202e",
              "entities": [
                {
                  "type": "citation",
                  "citationIndex": 2,
                  "title": "Source",
                  "url": "https://example.test/real\u001b[2J"
                }
              ],
              "attachments": [
                {
                  "contentType": "application/vnd.microsoft.card.hero",
                  "name": "weather.card",
                  "content": {
                    "title": "Forecast",
                    "text": "Sunny",
                    "buttons": [
                      { "type": "openUrl", "title": "Misleading", "value": "https://example.test/weather" }
                    ]
                  }
                },
                {
                  "contentType": "application/vnd.example.unknown",
                  "name": "credential-collector",
                  "content": "Do not open this"
                }
              ]
            }
            """));
        renderer.Finish();

        var rendered = Normalize(output.ToString());
        Assert.Contains("Agent: Answer�[31m�", rendered);
        Assert.Contains("[2] Source — https://example.test/real�", rendered);
        Assert.Contains("Card (hero) [noninteractive fallback]", rendered);
        Assert.Contains("Forecast", rendered);
        Assert.Contains("https://example.test/weather", rendered);
        Assert.Contains("Unsupported attachment/card type", rendered);
        Assert.Contains("credential-collector", rendered);
        Assert.DoesNotContain('\u001b', rendered);
    }

    [Fact]
    public void UnknownDeltaGetsLabelledFallback()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: true);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "channelData": { "delta": { "text": "partial" } }
            }
            """));
        renderer.Finish();

        Assert.Contains("CLI: Unrecognized activity update; see JSON for full data", output.ToString());
        Assert.DoesNotContain("partial", output.ToString());
    }

    [Fact]
    public void ColorIsIndependentFromPlainSelection()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: true, color: true);

        renderer.WriteActivity(Activity("""{ "type": "message", "text": "Answer" }"""));
        renderer.Finish();

        Assert.Contains("\u001b[", output.ToString());
        Assert.Contains("Agent:", output.ToString());
    }

    [Fact]
    public void FinalizedStreamDropsTextButRetainsDuplicateSuppressionState()
    {
        var output = new StringWriter();
        var renderer = new LiveTestHumanRenderer(output, plainOutput: true);
        var largeText = new string('s', 4096);

        renderer.WriteActivity(Activity($$"""
            {
              "type": "message",
              "text": "{{largeText}}",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 1, "streamType": "streaming" }
              ]
            }
            """));
        Assert.Equal(largeText.Length, renderer.RetainedStreamTextLength);

        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "done",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 2, "streamType": "final" }
              ]
            }
            """));
        renderer.WriteActivity(Activity(
            """
            {
              "type": "message",
              "text": "duplicate final",
              "entities": [
                { "type": "streaminfo", "streamId": "reply", "streamSequence": 2, "streamType": "final" }
              ]
            }
            """));

        Assert.Equal(0, renderer.RetainedStreamTextLength);
        Assert.Equal(1, renderer.RetainedStreamStateCount);
        Assert.DoesNotContain("duplicate final", output.ToString());
    }

    private static JsonElement Activity(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n");
}
