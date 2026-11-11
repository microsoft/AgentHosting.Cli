using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>A named assertion that did not hold, carrying what was expected and what happened.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string check, string expected, string actual, string? detail, MsagentResult? result)
        : base(detail is null ? $"{check}: expected {expected}, got {actual}" : $"{check}: expected {expected}, got {actual}. {detail}")
    {
        Check = check;
        Expected = expected;
        Actual = actual;
        CommandLine = result?.CommandLine;
    }

    /// <summary>The named check that failed, reported on its own line.</summary>
    public string Check { get; }

    public string Expected { get; }

    public string Actual { get; }

    /// <summary>The command that produced the failure, so a report can be pasted back into a shell.</summary>
    public string? CommandLine { get; }
}

/// <summary>The checks scenarios make, each one named so a batched scenario still reports per edit.</summary>
public sealed partial class Assertions
{
    private readonly Transcript _transcript;
    private readonly List<string> _passed = [];

    public Assertions(Transcript transcript) => _transcript = transcript;

    /// <summary>Every check that passed, listed under the scenario in the report.</summary>
    public IReadOnlyList<string> Passed => _passed;

    /// <summary>Asserts a command succeeded.</summary>
    public void ExitZero(MsagentResult result, string check = "exit code")
    {
        if (result.ExitCode == NotSignedIn)
        {
            throw ConsentRequired(result);
        }

        if (result.ExitCode != 0)
        {
            throw new AssertionException(check, "exit 0", $"exit {result.ExitCode}", Tail(result), result);
        }

        Pass(check);
    }

    /// <summary>Asserts a command failed with a specific code.</summary>
    public void ExitCode(MsagentResult result, int expected, string check = "exit code")
    {
        if (result.ExitCode == NotSignedIn && expected != NotSignedIn)
        {
            throw ConsentRequired(result);
        }

        if (result.ExitCode != expected)
        {
            throw new AssertionException(check, $"exit {expected}", $"exit {result.ExitCode}", Tail(result), result);
        }

        Pass(check);
    }

    /// <summary>Asserts a command failed in any way, where the exact code is not the point.</summary>
    public void NonZero(MsagentResult result, string check = "exit code")
    {
        if (result.ExitCode == NotSignedIn)
        {
            throw ConsentRequired(result);
        }

        if (result.ExitCode == 0)
        {
            throw new AssertionException(check, "a non-zero exit", "exit 0", Tail(result), result);
        }

        Pass(check);
    }

    private const int NotSignedIn = 3;

    private static ScenarioBlockedException ConsentRequired(MsagentResult result)
    {
        var arm = result.Output.Contains("Azure Resource Manager", StringComparison.OrdinalIgnoreCase);
        var graph = result.Output.Contains("Microsoft Graph", StringComparison.OrdinalIgnoreCase);

        var resource = arm ? "AzureResourceManager" : graph ? "MicrosoftGraph" : "Unspecified";
        var remedy = arm ? "run one `abs` command interactively once and complete the browser prompt"
            : graph ? "a tenant administrator must grant consent for the Graph application permissions"
            : "msagent auth login";

        return new ScenarioBlockedException(
            $"exit 3 · consent required for {resource} · {remedy}",
            resource,
            arm ? Capability.AzureRead : null);
    }

    /// <summary>Asserts two values match, ignoring case.</summary>
    public void Equal(string? expected, string? actual, string check)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException(check, Quote(expected), Quote(actual), null, null);
        }

        Pass(check);
    }

    /// <summary>Asserts a condition holds.</summary>
    public void True(bool condition, string check)
    {
        if (!condition)
        {
            throw new AssertionException(check, "true", "false", null, null);
        }

        Pass(check);
    }

    /// <summary>Asserts a condition does not hold.</summary>
    public void False(bool condition, string check) => True(!condition, check);

    /// <summary>Asserts output mentions a fragment, matched case-insensitively.</summary>
    public void Contains(MsagentResult result, string fragment, string check)
    {
        if (!result.Output.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException(check, $"output containing {Quote(fragment)}", "output without it", Tail(result), result);
        }

        Pass(check);
    }

    /// <summary>Asserts output does not mention a fragment.</summary>
    public void DoesNotContain(MsagentResult result, string fragment, string check)
    {
        if (result.Output.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException(check, $"output without {Quote(fragment)}", "output containing it", Tail(result), result);
        }

        Pass(check);
    }

    /// <summary>Asserts a command was refused cleanly: its exit code, a message fragment, no stack trace, and the JSON error envelope when JSON was asked for.</summary>
    public void Refused(MsagentResult result, int expectedExitCode, string fragment, string check)
    {
        ExitCode(result, expectedExitCode, $"{check}: exit {expectedExitCode}");
        Contains(result, fragment, $"{check}: says {Quote(fragment)}");
        Clean(result, check);
    }

    /// <summary>Asserts a command failed cleanly, where the exact exit code is not the point.</summary>
    public void FailedCleanly(MsagentResult result, string check)
    {
        NonZero(result, $"{check}: fails");
        Clean(result, check);
    }

    private void Clean(MsagentResult result, string check)
    {
        NoStackTrace(result, $"{check}: no stack trace");
        False(AccessToken().IsMatch(result.Output), $"{check}: no access token");

        if (result.Arguments.Contains("--json"))
        {
            True(IsErrorEnvelope(result.StdOut, result.ExitCode) || IsErrorEnvelope(result.StdErr, result.ExitCode), $"{check}: JSON error envelope");
        }
    }

    /// <summary>Asserts output carries no .NET stack frame.</summary>
    public void NoStackTrace(MsagentResult result, string check)
    {
        if (StackFrame().IsMatch(result.Output))
        {
            throw new AssertionException(check, "no stack trace", "a stack trace", Tail(result), result);
        }

        Pass(check);
    }

    /// <summary>Asserts a list of differences is empty, naming the first few when it is not.</summary>
    public void NoDifferences(IReadOnlyList<string> differences, string check)
    {
        if (differences.Count > 0)
        {
            throw new AssertionException(check, "no differences", $"{differences.Count} difference(s)", string.Join("; ", differences.Take(10)), null);
        }

        Pass(check);
    }

    private static bool IsErrorEnvelope(string text, int exitCode)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return document.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False && document.RootElement.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var value) && value == exitCode;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^\s+at \S+\(", RegexOptions.Multiline)]
    private static partial Regex StackFrame();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.")]
    private static partial Regex AccessToken();

    /// <summary>Asserts a file exists.</summary>
    public void FileExists(string path, string check)
    {
        if (!File.Exists(path))
        {
            throw new AssertionException(check, $"a file at {path}", "nothing there", null, null);
        }

        Pass(check);
    }

    /// <summary>Asserts a file's contents mention a fragment.</summary>
    public void FileContains(string path, string fragment, string check)
    {
        FileExists(path, $"{check} (file present)");

        if (!File.ReadAllText(path).Contains(fragment, StringComparison.Ordinal))
        {
            throw new AssertionException(check, $"{Path.GetFileName(path)} containing {Quote(fragment)}", "it does not", null, null);
        }

        Pass(check);
    }

    /// <summary>Polls until a condition holds, for the service behaviours that are eventually consistent.</summary>
    public async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, string check, TimeSpan? interval = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var poll = interval ?? TimeSpan.FromSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                Pass(check);
                return;
            }

            await Task.Delay(poll).ConfigureAwait(false);
        }

        throw new AssertionException(check, $"the condition to hold within {timeout.TotalSeconds:N0}s", "it never did", null, null);
    }

    private void Pass(string check)
    {
        _passed.Add(check);
        _transcript.Note($"ok: {check}");
    }

    private static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

    private static string? Tail(MsagentResult result)
    {
        var output = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var trimmed = Redaction.Apply(output.Trim());

        return trimmed.Length == 0 ? null : trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }
}
