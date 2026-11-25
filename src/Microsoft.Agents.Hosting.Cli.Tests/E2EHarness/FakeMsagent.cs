namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

internal sealed class FakeMsagent
{
    private readonly string _invocationLog;
    private readonly string _environmentLog;

    public FakeMsagent(string directory, string standardOutput)
        : this(directory, [(standardOutput, 0)])
    {
    }

    public FakeMsagent(string directory, IReadOnlyList<(string StandardOutput, int ExitCode)> responses)
    {
        Directory.CreateDirectory(directory);

        for (var index = 0; index < responses.Count; index++)
        {
            File.WriteAllText(Path.Combine(directory, $"stdout-{index + 1}.txt"), responses[index].StandardOutput);
            File.WriteAllText(Path.Combine(directory, $"exit-{index + 1}.txt"), $"{responses[index].ExitCode}");
        }

        File.WriteAllText(Path.Combine(directory, "last.txt"), $"{responses.Count}");
        File.WriteAllText(Path.Combine(directory, "count.txt"), "0");
        _invocationLog = Path.Combine(directory, "invocations.log");
        _environmentLog = Path.Combine(directory, "environment.log");
        ExecutablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "msagent.cmd" : "msagent");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(ExecutablePath, "@echo off\r\nmore >nul\r\n>>\"%~dp0invocations.log\" echo(%*\r\n>>\"%~dp0environment.log\" echo(%AH_CLI_FEATURES%^|%AH_CLI_AMS_ENDPOINT%^|%AH_CLI_AMSDP_ENDPOINT%\r\nset /p count=<\"%~dp0count.txt\"\r\nset /a count=count+1\r\n>\"%~dp0count.txt\" echo %count%\r\nset response=%count%\r\nif not exist \"%~dp0stdout-%response%.txt\" set /p response=<\"%~dp0last.txt\"\r\ntype \"%~dp0stdout-%response%.txt\"\r\nset /p code=<\"%~dp0exit-%response%.txt\"\r\nexit /b %code%\r\n");
        }
        else
        {
            File.WriteAllText(ExecutablePath, "#!/bin/sh\ndirectory=\"$(dirname \"$0\")\"\nprintf '%s\\n' \"$*\" >> \"$directory/invocations.log\"\ncat >/dev/null\nprintf '%s|%s|%s\\n' \"$AH_CLI_FEATURES\" \"$AH_CLI_AMS_ENDPOINT\" \"$AH_CLI_AMSDP_ENDPOINT\" >> \"$directory/environment.log\"\ncount=$(($(cat \"$directory/count.txt\") + 1))\necho \"$count\" > \"$directory/count.txt\"\nresponse=$count\n[ -f \"$directory/stdout-$response.txt\" ] || response=$(cat \"$directory/last.txt\")\ncat \"$directory/stdout-$response.txt\"\nexit \"$(cat \"$directory/exit-$response.txt\")\"\n");
            File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Invocations => File.Exists(_invocationLog) ? File.ReadAllLines(_invocationLog) : [];

    public IReadOnlyList<string> Environments => File.Exists(_environmentLog) ? File.ReadAllLines(_environmentLog) : [];
}
