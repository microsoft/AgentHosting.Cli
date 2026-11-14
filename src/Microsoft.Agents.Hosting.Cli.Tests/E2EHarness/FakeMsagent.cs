namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

internal sealed class FakeMsagent
{
    private readonly string _invocationLog;

    public FakeMsagent(string directory, string standardOutput)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stdout.txt"), standardOutput);
        _invocationLog = Path.Combine(directory, "invocations.log");
        ExecutablePath = Path.Combine(directory, OperatingSystem.IsWindows() ? "msagent.cmd" : "msagent");

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(ExecutablePath, "@echo off\r\n>>\"%~dp0invocations.log\" echo(%*\r\ntype \"%~dp0stdout.txt\"\r\n");
        }
        else
        {
            File.WriteAllText(ExecutablePath, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$(dirname \"$0\")/invocations.log\"\ncat \"$(dirname \"$0\")/stdout.txt\"\n");
            File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Invocations => File.Exists(_invocationLog) ? File.ReadAllLines(_invocationLog) : [];
}
