using System.Text;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Appends every subprocess invocation to a per-run log, and to a per-scenario one while a scenario is active.</summary>
public sealed class Transcript
{
    private readonly string _runLogPath;
    private readonly Lock _gate = new();
    private string? _scenarioLogPath;

    public Transcript(string runDirectory)
    {
        _runLogPath = Path.Combine(runDirectory, "transcript.log");
        Directory.CreateDirectory(runDirectory);
    }

    /// <summary>Directs subsequent entries at a scenario's own log as well as the run log.</summary>
    public void BeginScenario(string scenarioLogPath)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scenarioLogPath)!);
            _scenarioLogPath = scenarioLogPath;
        }
    }

    /// <summary>Stops writing to the scenario log.</summary>
    public void EndScenario()
    {
        lock (_gate)
        {
            _scenarioLogPath = null;
        }
    }

    /// <summary>Writes one invocation, its exit code, and both streams.</summary>
    public void Record(string fileName, MsagentResult result)
    {
        var entry = new StringBuilder()
            .AppendLine(Redaction.Apply($"$ {Path.GetFileNameWithoutExtension(fileName)} {string.Join(' ', result.Arguments)}"))
            .AppendLine($"  exit {result.ExitCode} in {result.Duration.TotalSeconds:N1}s");

        AppendStream(entry, "stdout", result.StdOut);
        AppendStream(entry, "stderr", result.StdErr);
        entry.AppendLine();

        Write(entry.ToString());
    }

    /// <summary>Writes a free-form note, used for assertions and cleanup steps.</summary>
    public void Note(string message) => Write($"# {Redaction.Apply(message)}{Environment.NewLine}");

    private static void AppendStream(StringBuilder entry, string label, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        entry.AppendLine($"  --- {label} ---");

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            entry.AppendLine("  " + Redaction.Apply(line.TrimEnd('\r')));
        }
    }

    private void Write(string text)
    {
        lock (_gate)
        {
            File.AppendAllText(_runLogPath, text);

            if (_scenarioLogPath is { } scenarioLog)
            {
                File.AppendAllText(scenarioLog, text);
            }
        }
    }
}
