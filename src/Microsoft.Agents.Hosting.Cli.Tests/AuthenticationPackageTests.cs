using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Agents.Hosting.Cli.AuthenticationChecks;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthenticationPackageTests
{
    [Fact]
    public void InstalledChildUsesRecognizedIsolationAndTelemetrySettingsWithoutChangingParent()
    {
        var start = new ProcessStartInfo();
        start.Environment["AZURE_CLIENT_SECRET"] = "FAKE_SECRET";
        start.Environment["ACTIONS_ID_TOKEN_REQUEST_TOKEN"] = "FAKE_ASSERTION_SOURCE";
        start.Environment["AH_CLI_FEATURES"] = "LiveTestFeatures";
        start.Environment["AH_CLI_CONFIG_DIR"] = "FAKE_SHARED_DIRECTORY";
        start.Environment["NUGET_SCRATCH"] = "FAKE_SHARED_SCRATCH";
        start.Environment["TMPDIR"] = "FAKE_SHARED_TEMP";
        var parentValue = Environment.GetEnvironmentVariable("AH_CLI_CONFIG_DIR");
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "msagent-package-" + Guid.NewGuid().ToString("N"));
        InstalledToolChecks.ConfigureChild(start, root, Path.GetDirectoryName(Environment.ProcessPath!)!);
        Assert.False(start.Environment.ContainsKey("AZURE_CLIENT_SECRET"));
        Assert.False(start.Environment.ContainsKey("ACTIONS_ID_TOKEN_REQUEST_TOKEN"));
        Assert.False(start.Environment.ContainsKey("AH_CLI_FEATURES"));
        Assert.Equal(Path.Combine(root, "config"), start.Environment["AH_CLI_CONFIG_DIR"]);
        Assert.Equal("1", start.Environment["AH_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal(Path.Combine(root, "scratch"), start.Environment["NUGET_SCRATCH"]);
        Assert.Equal(Path.Combine(root, "scratch"), start.Environment["TMPDIR"]);
        Assert.Equal(parentValue, Environment.GetEnvironmentVariable("AH_CLI_CONFIG_DIR"));
        Assert.True(start.RedirectStandardInput);
        Assert.False(start.UseShellExecute);
    }

    [Theory]
    [InlineData("shims/win-x86/msagent.exe")]
    [InlineData("featureflags.json")]
    [InlineData("Microsoft.Agents.Hosting.Cli.Auth.dll")]
    [InlineData("Microsoft.Agents.Hosting.Cli.dll")]
    [InlineData("Microsoft.Agents.Hosting.Cli.runtimeconfig.json")]
    [InlineData("Microsoft.Agents.Hosting.Cli.exe")]
    [InlineData("msalruntime.dll")]
    public void IncompleteToolImplementationFailsAcceptance(string missing)
    {
        using var bytes = Fixture(missing);
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidOperationException>(() => PackageChecks.CheckImplementation(zip, "win-x64"));
    }

    [Theory]
    [InlineData("self-contained")]
    [InlineData("feature-enabled")]
    [InlineData("legacy-auth-enabled")]
    [InlineData("legacy-auth-missing")]
    [InlineData("application-support-enabled")]
    [InlineData("application-support-missing")]
    [InlineData("live-test-disabled")]
    [InlineData("command-renamed")]
    [InlineData("dependency-mismatch")]
    [InlineData("extra-shim")]
    public void ChangedRuntimeFeatureCommandOrDependencyContractFailsAcceptance(string mutation)
    {
        using var bytes = Fixture(mutation: mutation);
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidOperationException>(() => PackageChecks.CheckImplementation(zip, "win-x64"));
    }

    [Fact]
    public void CompleteFrameworkDependentToolImplementationIsAccepted()
    {
        using var bytes = Fixture();
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        PackageChecks.CheckImplementation(zip, "win-x64");
    }

    [Fact]
    public void ShippedFeatureDefaultsAreAccepted()
    {
        var featuresPath = Path.Combine(Path.GetDirectoryName(typeof(AgentHostCli).Assembly.Location)!, "featureflags.json");
        using var bytes = Fixture(featuresJson: File.ReadAllText(featuresPath));
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        PackageChecks.CheckImplementation(zip, "win-x64");
    }

    private static MemoryStream Fixture(string? missing = null, string? mutation = null, string? featuresJson = null)
    {
        var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var rid in PackageChecks.RuntimeIdentifiers)
                Add($"shims/{rid}/msagent{(rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "")}", "shim");
            if (mutation == "extra-shim") Add("shims/linux-musl-x64/msagent", "extra-shim");
            foreach (var assembly in new[] { "Microsoft.Agents.Hosting.Cli", "Microsoft.Agents.Hosting.Cli.Auth",
                "Microsoft.Agents.Hosting.Cli.Core", "Microsoft.Agents.Hosting.Cli.Commands", "Microsoft.Agents.Hosting.Cli.CopilotStudio" })
                Add(assembly + ".dll", "assembly");
            Add("Microsoft.Agents.Hosting.Cli.exe", "apphost");
            Add("msalruntime.dll", "native-library");
            Add("DotnetToolSettings.xml", $"<DotNetCliTool><Commands><Command Name=\"{(mutation == "command-renamed" ? "other" : "msagent")}\" EntryPoint=\"Microsoft.Agents.Hosting.Cli.exe\" Runner=\"executable\" /></Commands></DotNetCliTool>");
            var features = new Dictionary<string, string>
            {
                ["amsFeatures"] = mutation == "feature-enabled" ? "on" : "off",
                ["azureBotServiceFeatures"] = "off", ["identityFeatures"] = "off",
                ["liveTestFeatures"] = mutation == "live-test-disabled" ? "off" : "on", ["evaluationFeatures"] = "on",
            };
            if (mutation != "legacy-auth-missing")
                features["legacyAuth"] = mutation == "legacy-auth-enabled" ? "on" : "off";
            if (mutation != "application-support-missing")
                features["appusersupport"] = mutation == "application-support-enabled" ? "on" : "off";
            Add("featureflags.json", featuresJson ?? System.Text.Json.JsonSerializer.Serialize(features));
            Add("Microsoft.Agents.Hosting.Cli.runtimeconfig.json", mutation == "self-contained" ? """{"runtimeOptions":{"includedFrameworks":[]}}""" :
                """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}""");
            Add("Microsoft.Agents.Hosting.Cli.deps.json",
                """{"libraries":{"Microsoft.Identity.Client/VERSION":{},"Microsoft.Identity.Client.Broker/4.92.0":{},"Microsoft.Identity.Client.Extensions.Msal/4.92.0":{},"Azure.Identity/1.21.0":{},"Azure.Core/1.53.0":{},"Microsoft.Agents.CopilotStudio.Sync/1.10.9-prerelease":{}}}"""
                    .Replace("VERSION", mutation == "dependency-mismatch" ? "4.0.0" : "4.92.0", StringComparison.Ordinal));

            void Add(string name, string text)
            {
                if (name == missing) return;
                using var writer = new StreamWriter(zip.CreateEntry("tools/net10.0/win-x64/" + name).Open());
                writer.Write(text);
            }
        }
        result.Position = 0;
        return result;
    }
}
