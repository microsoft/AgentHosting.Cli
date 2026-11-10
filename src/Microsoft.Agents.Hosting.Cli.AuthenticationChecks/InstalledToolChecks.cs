using System.Diagnostics;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.AuthenticationChecks;

public static class InstalledToolChecks
{
    public static void ConfigureChild(ProcessStartInfo start, string root, string dotnetRoot)
    {
        foreach (var name in start.Environment.Keys.ToArray())
            if (name.StartsWith("AH_CLI_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ACTIONS_ID_TOKEN_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("MSAGENT_ADO_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                name is "DBUS_SESSION_BUS_ADDRESS" or "G_DBUS_DEBUG" or "MSAL_LOG_LEVEL")
                start.Environment.Remove(name);
        start.Environment["AH_CLI_CONFIG_DIR"] = Path.Combine(root, "config");
        start.Environment["AH_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(root, "home");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(root, "nuget-cache");
        start.Environment["NUGET_SCRATCH"] = Path.Combine(root, "scratch");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(root, "http-cache");
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            start.Environment[name] = Path.Combine(root, "scratch");
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME" })
            start.Environment[name] = Path.Combine(root, "home");
        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_ROOT_" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()] = dotnetRoot;
        start.Environment["NO_COLOR"] = "1";
        start.Environment["CI"] = "true";
        start.WorkingDirectory = Path.Combine(root, "project");
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
    }

    public static async Task RunAsync(string packageDirectory, string rid, Func<string, Task>? nativeLibraryCheck = null)
    {
        var family = PackageChecks.CheckFamily(packageDirectory);
        Console.WriteLine("tier=package check=family status=PASS rids=7 packages=8 frameworks=net10+aspnet10");
        var parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        Directory.CreateDirectory(parent);
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Test installation parent must not redirect outside the checkout.");
        var root = Path.Combine(parent, "msagent-package-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root)) throw new InvalidOperationException("Test-owned installation path already exists.");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var directory in new[] { "feed", "project", "config", "home", "nuget-cache", "tool", "scratch", "http-cache" })
                Directory.CreateDirectory(Path.Combine(root, directory));
            foreach (var package in family.Packages)
                File.Copy(package, Path.Combine(root, "feed", Path.GetFileName(package)));
            var config = Path.Combine(root, "nuget.config");
            new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("configuration",
                new System.Xml.Linq.XElement("packageSources", new System.Xml.Linq.XElement("clear"),
                    new System.Xml.Linq.XElement("add", new System.Xml.Linq.XAttribute("key", "test-owned-feed"),
                        new System.Xml.Linq.XAttribute("value", Path.Combine(root, "feed"))))))
                .Save(config);
            var dotnet = Environment.ProcessPath!;
            if (Path.GetFileNameWithoutExtension(dotnet) != "dotnet")
                throw new InvalidOperationException("Run the acceptance harness with the architecture-matching dotnet host.");
            var resolvedDotnet = File.ResolveLinkTarget(dotnet, returnFinalTarget: true)?.FullName ?? dotnet;
            var dotnetRoot = Path.GetDirectoryName(resolvedDotnet)!;
            var installed = Path.Combine(root, "tool");
            var install = await RunChild(dotnet, ["tool", "install", PackageChecks.PackageId, "--version", family.Version,
                "--tool-path", installed, "--configfile", config], root, dotnetRoot);
            Require(install.Exit == 0, "install");
            var runtimeFiles = Directory.GetFiles(installed, PackageChecks.PackageId + ".runtimeconfig.json", SearchOption.AllDirectories);
            Require(runtimeFiles.Length == 1 && Path.GetFileName(Path.GetDirectoryName(runtimeFiles[0])) == rid, "installed-rid");
            var executable = Path.Combine(installed, "msagent" + (OperatingSystem.IsWindows() ? ".cmd" : ""));
            Require(File.Exists(executable), "installed-shim");
            Console.WriteLine($"tier=package check=local-install status=PASS rid={rid} version={family.Version}");
            await Help(["--help"], ["auth", "agent", "deployment", "telemetry"]);
            foreach (var verb in new[] { "create", "list", "select", "delete", "name", "clear", "who", "status" })
                await Help(["auth", verb, "--help"], [$"msagent auth {verb}"]);
            foreach (var verb in new[] { "login", "logout" })
                await Help(["auth", verb, "--help"], [$"msagent auth {verb}"], features: "LegacyAuth");
            await Help(["auth", "create", "--help"], ["--deviceCode", "-dc", "--managedIdentity", "-mi", "--githubFederated", "-ghf", "--azureDevOpsFederated", "-adof"]);
            await Help(["agent", "eval", "--help"], ["run", "testset"]);
            foreach (var args in new[] { new[] { "auth", "switch" }, ["auth", "token"], ["auth", "update", "--help"],
                ["auth", "login"], ["auth", "login", "--help"], ["auth", "logout"], ["auth", "logout", "--help"],
                ["auth", "update", "--index", "1", "--name="],
                ["auth", "update", "--index", "1", "--environment", "env-id"],
                ["auth", "create", "--kind", "user"],
                ["auth", "create", "--url", "https://test.invalid"], ["abs"], ["identity"], ["test"] })
                Require((await RunChild(executable, args, root, dotnetRoot)).Exit == 2, "removed-or-disabled-command");
            var status = await Json(["auth", "status", "--json"], 0);
            using (status)
            {
                Require(!status.RootElement.GetProperty("signedIn").GetBoolean(), "isolated-status");
                Require(status.RootElement.GetProperty("status").GetString() == "signed-out", "status-contract");
            }
            using (var list = await Json(["auth", "list", "--json"], 0))
                Require(list.RootElement.GetProperty("profiles").GetArrayLength() == 0, "isolated-list");
            using (var who = await Json(["auth", "who", "--json"], 0))
                Require(who.RootElement.GetProperty("status").GetString() == "signed-out", "isolated-who");
            using (var telemetry = await Json(["telemetry", "status", "--json"], 0))
                Require(!telemetry.RootElement.GetProperty("effectiveCollectionEnabled").GetBoolean(), "telemetry-optout");
            // Accepted fake-host tests prove these gates precede cache/native/source construction.
            using (var failure = await Json(["auth", "create", "-dc", "--non-interactive", "--json"], 3))
                Require(failure.RootElement.GetProperty("errorCode").GetInt32() == 12007, "early-interaction-gate");
            using (var failure = await Json(["auth", "login", "--non-interactive", "--json"], 3, features: "LegacyAuth"))
                Require(failure.RootElement.GetProperty("errorCode").GetInt32() == 12007, "legacy-early-interaction-gate");
            Require(!Directory.GetFiles(Path.Combine(root, "config"), "*", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f).EndsWith(".bin", StringComparison.Ordinal) ||
                    Path.GetFileName(f).Contains("msal", StringComparison.OrdinalIgnoreCase)), "no-native-cache-artifacts");
            Console.WriteLine("tier=package check=installed-contracts status=PASS help=8-default+2-legacy-auth-verbs aliases=PASS defaults=PASS json=PASS NI12007=exit3 telemetry=off");
            if (nativeLibraryCheck is not null) await nativeLibraryCheck(Path.GetDirectoryName(runtimeFiles[0])!);

            async Task Help(string[] args, string[] required, string? features = null)
            {
                var help = await RunChild(executable, args, root, dotnetRoot, features);
                Require(help.Exit == 0 && required.All(s => help.Output.Contains(s, StringComparison.Ordinal)), "reflection-help");
            }
            async Task<JsonDocument> Json(string[] args, int exit, string? features = null)
            {
                var result = await RunChild(executable, args, root, dotnetRoot, features);
                Require(result.Exit == exit, "json-exit");
                Require(!result.Output.Contains("accessToken", StringComparison.OrdinalIgnoreCase) &&
                    !result.Output.Contains("credentialReference", StringComparison.OrdinalIgnoreCase), "json-privacy");
                return JsonDocument.Parse(result.Output);
            }
        }
        finally
        {
            // Only this newly generated, resolved leaf belongs to this invocation.
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int Exit, string Output)> RunChild(string executable, string[] args, string root, string dotnetRoot, string? features = null)
    {
        var start = new ProcessStartInfo(executable);
        ConfigureChild(start, root, dotnetRoot);
        if (features is not null) start.Environment["AH_CLI_FEATURES"] = features;
        if (OperatingSystem.IsWindows() && executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec")
                ?? throw new InvalidOperationException("The Windows command processor is required.");
            start.Arguments = "/d /s /c \"\"" + executable + "\" "
                + string.Join(" ", args.Select(arg => "\"" + arg + "\"")) + "\"";
        }
        else
        {
            foreach (var argument in args) start.ArgumentList.Add(argument);
        }
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Test child did not start.");
        child.StandardInput.Close();
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        _ = await error; // Never export child diagnostic text or credential-bearing installation errors.
        return (child.ExitCode, await output);
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
        {
            Console.Error.WriteLine($"tier=package check={check} status=FAIL");
            throw new InvalidOperationException($"Installed package check failed: {check}.");
        }
    }
}
