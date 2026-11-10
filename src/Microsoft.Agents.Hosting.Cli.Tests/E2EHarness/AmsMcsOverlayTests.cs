using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class AmsMcsOverlayTests : IDisposable
{
    private static readonly string[] SevenVariables = ["MSAGENT_AMS_DATAPLANE_ENDPOINT", "MSAGENT_AMS_DATAPLANE_AUDIENCE", "MSAGENT_AMS_ONBEHALFOF_ENDPOINT", "MSAGENT_AMS_ONBEHALFOF_AUDIENCE", "MSAGENT_AMS_CONTROLPLANE_ENDPOINT", "MSAGENT_AMS_CONTROLPLANE_AUDIENCE", "MSAGENT_GIT_HOST"];

    private static readonly string[] CrashDumpVariables = ["DOTNET_DbgEnableMiniDump", "DOTNET_DbgMiniDumpType", "DOTNET_DbgMiniDumpName"];

    private readonly ScratchDirectory _scratch = new();
    private readonly VariableScope _variables = new([.. SevenVariables, "AH_CLI_FEATURES", "GIT_ASKPASS", "SSH_ASKPASS", "MSAGENT_E2E_TEST_MARKER"]);

    public void Dispose()
    {
        _variables.Dispose();
        _scratch.Dispose();
    }

    [Fact]
    public void AmsOnSetsTheFlagAndTheSevenVariables()
    {
        var environment = Services(AmsMcsTestKit.Config() with { Features = "AmsMcsAgents;EvaluationFeatures" }).On.StartInfo(["agent", "list"]).Environment;

        Assert.Equal("AmsMcsAgents;EvaluationFeatures", environment["AH_CLI_FEATURES"]);
        Assert.Equal(["https://data-plane.example.invalid", "https://data-plane-audience.example.invalid", "https://on-behalf-of.example.invalid", "https://on-behalf-of-audience.example.invalid", "https://control-plane.example.invalid", "https://control-plane-audience.example.invalid", AmsMcsTestKit.GitHost], SevenVariables.Select(name => environment[name]));
    }

    [Fact]
    public void AmsOffRemovesTheSevenVariablesAndKeepsEveryOtherFeature()
    {
        foreach (var name in SevenVariables)
        {
            Environment.SetEnvironmentVariable(name, "inherited");
        }

        Environment.SetEnvironmentVariable("AH_CLI_FEATURES", "AmsMcsAgents;EvaluationFeatures");

        var environment = Services(AmsMcsTestKit.Config() with { Features = "EvaluationFeatures;AmsMcsAgents;Preview" }).Off.StartInfo(["agent", "list"]).Environment;

        Assert.Equal("EvaluationFeatures;Preview", environment["AH_CLI_FEATURES"]);
        Assert.All(SevenVariables, name => Assert.False(environment.ContainsKey(name)));
    }

    [Fact]
    public void AmsOffWithOnlyTheAmsFlagRemovesTheFlagVariable()
    {
        Environment.SetEnvironmentVariable("AH_CLI_FEATURES", "AmsMcsAgents");

        Assert.False(Services(AmsMcsTestKit.Config()).Off.StartInfo(["agent", "list"]).Environment.ContainsKey("AH_CLI_FEATURES"));
    }

    [Fact]
    public void ACallWithoutAnOverlayLeavesTheEnvironmentAsItWas()
    {
        Environment.SetEnvironmentVariable("MSAGENT_E2E_TEST_MARKER", "kept");
        Environment.SetEnvironmentVariable("AH_CLI_FEATURES", "AmsMcsAgents");
        var msagent = new MsagentCli("msagent", Transcript(), _scratch.Folder("dumps"));

        var environment = msagent.StartInfo(["agent", "list"]).Environment;
        var inherited = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);

        Assert.Empty(msagent.Overlay.Variables);
        Assert.Equal(inherited.Keys.Union(CrashDumpVariables, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase), environment.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(inherited.Where(entry => !CrashDumpVariables.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)), entry => Assert.Equal(entry.Value, environment[entry.Key]));
    }

    [Fact]
    public void WithComposesOverlaysAndTheLaterOneWins()
    {
        Environment.SetEnvironmentVariable("MSAGENT_E2E_TEST_MARKER", "inherited");
        var msagent = new MsagentCli("msagent", Transcript(), _scratch.Folder("dumps"));

        var composed = msagent.With(EnvironmentOverlay.Empty.Set("MSAGENT_E2E_TEST_MARKER", "first").Set("AH_CLI_FEATURES", "AmsMcsAgents")).With(EnvironmentOverlay.Empty.Remove("MSAGENT_E2E_TEST_MARKER").Set("AH_CLI_FEATURES", "Later"));
        var environment = composed.StartInfo(["agent", "list"]).Environment;

        Assert.False(environment.ContainsKey("MSAGENT_E2E_TEST_MARKER"));
        Assert.Equal("Later", environment["AH_CLI_FEATURES"]);
        Assert.Equal("inherited", msagent.StartInfo(["agent", "list"]).Environment["MSAGENT_E2E_TEST_MARKER"]);
        Assert.Equal(msagent.BinaryPath, composed.BinaryPath);
    }

    [Fact]
    public void AnOverlayIsImmutableAndAppliesSetsAndRemovals()
    {
        var first = EnvironmentOverlay.Empty.Set("A", "1");
        var second = first.Set("B", "2").Remove("A");
        var environment = new Dictionary<string, string?> { ["A"] = "inherited", ["C"] = "kept" };

        second.ApplyTo(environment);

        Assert.Equal("1", Assert.Single(first.Variables).Value);
        Assert.Empty(EnvironmentOverlay.Empty.Variables);
        Assert.Equal(new Dictionary<string, string?> { ["B"] = "2", ["C"] = "kept" }, environment);
    }

    [Fact]
    public async Task RunAsyncHandsTheLauncherTheOverlaidCall()
    {
        var fake = new FakeMsagent().Answer("agent list", "{}");
        var project = _scratch.Folder("project");

        var result = await fake.Cli(Transcript(), _scratch.Path).With(EnvironmentOverlay.Empty.Set("AH_CLI_FEATURES", "AmsMcsAgents")).RunAsync(["agent", "list"], project, "input", TimeSpan.FromSeconds(7), CancellationToken.None);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("{}", result.StdOut);
        Assert.Equal(["agent", "list"], call.Arguments);
        Assert.Equal(project, call.WorkingDirectory);
        Assert.Equal("input", call.Stdin);
        Assert.Equal(TimeSpan.FromSeconds(7), call.Timeout);
        Assert.Equal("AmsMcsAgents", call.Environment["AH_CLI_FEATURES"]);
    }

    [Fact]
    public void TheNoGitPathDropsEveryEntryHoldingGitAndKeepsTheOthers()
    {
        var withExecutable = _scratch.Folder("a");
        var withPlain = _scratch.Folder("b");
        var withQuotedGit = _scratch.Folder("c");
        var without = _scratch.Folder("d");
        File.WriteAllText(Path.Combine(withExecutable, "git.exe"), string.Empty);
        File.WriteAllText(Path.Combine(withPlain, "git"), string.Empty);
        File.WriteAllText(Path.Combine(withQuotedGit, "git"), string.Empty);
        var missing = Path.Combine(_scratch.Path, "missing");

        var path = string.Join(Path.PathSeparator, [withExecutable, $"\"{without}\"", string.Empty, withPlain, $"\"{withQuotedGit}\"", missing]);

        Assert.Equal(string.Join(Path.PathSeparator, [$"\"{without}\"", missing]), AmsMcsServices.WithoutGit(path));
        Assert.Equal(string.Empty, AmsMcsServices.WithoutGit(null));
    }

    [Fact]
    public void TheNoGitClientRunsWithAmsOnAndNoPathEntryHoldingGit()
    {
        var environment = Services(AmsMcsTestKit.Config()).NoGit.StartInfo(["agent", "deploy"]).Environment;

        Assert.Equal(AmsMcsServices.WithoutGit(Environment.GetEnvironmentVariable("PATH")), environment["PATH"]);
        Assert.DoesNotContain(environment["PATH"]!.Split(Path.PathSeparator), entry => File.Exists(Path.Combine(entry, "git")) || File.Exists(Path.Combine(entry, "git.exe")));
        Assert.Equal("AmsMcsAgents", environment["AH_CLI_FEATURES"]);
        Assert.Equal(AmsMcsTestKit.GitHost, environment["MSAGENT_GIT_HOST"]);
    }

    [Fact]
    public void GitRunsOnItsOverlayWithoutPrompts()
    {
        Environment.SetEnvironmentVariable("GIT_ASKPASS", "askpass");
        Environment.SetEnvironmentVariable("SSH_ASKPASS", "askpass");
        var directory = _scratch.Folder("repository");

        var startInfo = new GitCli(Transcript(), EnvironmentOverlay.Empty.Set("GIT_CONFIG_NOSYSTEM", "1").Set("GIT_TERMINAL_PROMPT", "1")).StartInfo(directory, ["status", "--porcelain"]);

        Assert.Equal("git", startInfo.FileName);
        Assert.Equal(["status", "--porcelain"], startInfo.ArgumentList);
        Assert.Equal(directory, startInfo.WorkingDirectory);
        Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("Never", startInfo.Environment["GCM_INTERACTIVE"]);
        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey("SSH_ASKPASS"));
    }

    [Fact]
    public void TheUnreachableClientRunsWithAmsOnAndEveryAmsEndpointOnAnInvalidHost()
    {
        var environment = Services(AmsMcsTestKit.Config()).Unreachable.StartInfo(["agent", "pull"]).Environment;

        Assert.Equal(["https://no-ams.invalid", "https://data-plane-audience.example.invalid", "https://no-ams.invalid", "https://on-behalf-of-audience.example.invalid", "https://no-ams.invalid", "https://control-plane-audience.example.invalid", AmsMcsTestKit.GitHost], SevenVariables.Select(name => environment[name]));
        Assert.Equal("AmsMcsAgents", environment["AH_CLI_FEATURES"]);
    }

    [Fact]
    public void TheServicesGitRunsWithAmsOnSoTheCredentialHelperReachesGrs()
    {
        var startInfo = Services(AmsMcsTestKit.Config()).Git.StartInfo(null, ["--version"]);

        Assert.Equal(Environment.CurrentDirectory, startInfo.WorkingDirectory);
        Assert.Equal("AmsMcsAgents", startInfo.Environment["AH_CLI_FEATURES"]);
        Assert.Equal(AmsMcsTestKit.GitHost, startInfo.Environment["MSAGENT_GIT_HOST"]);
    }

    private Transcript Transcript() => new(_scratch.Folder("run"));

    private AmsMcsServices Services(AmsMcsConfig config)
    {
        var transcript = Transcript();
        return new AmsMcsServices(config, "20261108-050000-ab12", new MsagentCli("msagent", transcript, _scratch.Folder("dumps")), transcript, new FakeTokens());
    }
}
