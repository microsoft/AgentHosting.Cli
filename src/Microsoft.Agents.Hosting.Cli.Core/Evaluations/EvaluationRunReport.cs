using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Renders a finished run as the JSON or Markdown written by <c>--output</c>.</summary>
public static class EvaluationRunReport
{
    private static readonly EvaluationReportJsonContext Context = new(new JsonSerializerOptions(EvaluationReportJsonContext.Default.Options) { WriteIndented = true });

    /// <summary>Serialises a run result.</summary>
    /// <param name="result">The run to write.</param>
    public static string ToJson(EvaluationRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return JsonSerializer.Serialize(result, typeof(EvaluationRunResult), Context);
    }

    /// <summary>Renders a run in the format the output file's extension asks for.</summary>
    /// <param name="path">The file being written.</param>
    /// <param name="result">The run to write.</param>
    public static string Render(string path, EvaluationRunResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ToJson(result) : EvaluationTranscriptWriter.Write(result);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EvaluationRunResult))]
internal sealed partial class EvaluationReportJsonContext : JsonSerializerContext
{
}
