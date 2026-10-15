using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal static class McsLiveActivityCodec
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            ProtocolJsonSerializer.ApplyExtensionOptions(options =>
            {
                var fallback = new JsonSerializerOptions(options)
                {
                    MaxDepth = 256,
                };

                options.MaxDepth = 256;
                options.Converters.Insert(0, new RawActivityConverter(fallback));
                return options;
            });

            _initialized = true;
        }
    }

    public static JsonElement ToJsonElement(IActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity is not RawActivity raw)
        {
            throw new McsLiveTestProtocolException("The SDK returned an activity without lossless raw JSON.");
        }

        return raw.RawJson.Clone();
    }

    private sealed class RawActivityConverter(JsonSerializerOptions fallback) : JsonConverter<Activity>
    {
        public override Activity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("An activity must be a JSON object.");
            }

            var json = document.RootElement.Clone();
            var typed = JsonSerializer.Deserialize<Activity>(json.GetRawText(), fallback)
                ?? throw new JsonException("The SDK could not deserialize an activity.");
            return new RawActivity(json)
            {
                Type = typed.Type,
                Conversation = typed.Conversation,
            };
        }

        public override void Write(Utf8JsonWriter writer, Activity value, JsonSerializerOptions options)
        {
            if (value is RawActivity raw)
            {
                raw.RawJson.WriteTo(writer);
                return;
            }

            JsonSerializer.Serialize(writer, value, fallback);
        }
    }
}

/// <summary>An SDK activity carrying the complete original JSON payload.</summary>
public sealed class RawActivity(JsonElement rawJson) : Activity
{
    /// <summary>Gets the lossless activity JSON.</summary>
    public JsonElement RawJson { get; } = rawJson;
}
