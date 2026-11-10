using System.Runtime.InteropServices;

namespace Microsoft.Agents.Hosting.Cli.AuthenticationChecks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string tier = args.FirstOrDefault() ?? "unselected";
        try
        {
            if (tier is not ("package" or "native" or "platform" or "control"))
                throw new InvalidOperationException("Select package, native, platform or dedicated control preparation explicitly.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var switches = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] is "--dedicated" or "--prepared")
                {
                    if (!switches.Add(args[i])) throw new InvalidOperationException("Duplicate switch.");
                }
                else if (args[i] is "--expected-rid" or "--package-directory" or "--root" && i + 1 < args.Length)
                {
                    if (!values.TryAdd(args[i], args[++i])) throw new InvalidOperationException("Duplicate option.");
                }
                else throw new InvalidOperationException("Unknown or incomplete option.");
            }
            var rid = values["--expected-rid"];
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
            if (tier == "native")
                AcceptanceChecks.RequireNativePreparation(switches.Contains("--dedicated"), switches.Contains("--prepared"),
                    rid, os, RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
            else
                AcceptanceChecks.RequireArchitecture(rid, os, RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture);
            Console.WriteLine($"tier={tier} os={os} process={RuntimeInformation.ProcessArchitecture} hardware={RuntimeInformation.OSArchitecture} expectedRid={rid}");
            if (tier == "control")
            {
                if (!OperatingSystem.IsLinux() || !switches.Contains("--dedicated"))
                    throw new InvalidOperationException("Control preparation requires explicitly dedicated Linux state.");
                await NativeChecks.CheckDaemonControlDirectoryAsync(Path.GetFullPath(values["--root"]), switches.Contains("--prepared"));
                return 0;
            }
            if (tier == "platform")
            {
                Console.WriteLine("tier=platform check=native-process-architecture status=PASS");
                return 0;
            }
            var packages = Path.GetFullPath(values["--package-directory"]);
            if (tier == "native") await NativeChecks.RunAsync(Path.GetFullPath(values["--root"]), rid, packages);
            else await InstalledToolChecks.RunAsync(packages, rid);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine($"tier={tier} check=acceptance status=FAIL exception={failure.GetType().Name}");
            return 1;
        }
    }
}
