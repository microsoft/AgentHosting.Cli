using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;
using HarnessCommands = Microsoft.Agents.Hosting.Cli.E2E.Commands;
using HarnessProgram = Microsoft.Agents.Hosting.Cli.E2E.Program;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class AmsMcsCatalogueTests : IDisposable
{
    private const string AloneMessage = "The ams pack runs alone: msagent keeps one Power Platform ring per account, and the other packs use the prod ring. Run --packs ams on its own, signed in with `msagent auth login --cloud test`.";

    private readonly ScratchDirectory _scratch = new();
    private readonly VariableScope _variables = new();
    private readonly string _runId = "ams-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string? _createdConfig;

    public AmsMcsCatalogueTests()
    {
        var config = Path.Combine(HarnessEnvironment.ProjectDirectory, "e2e.config.json");

        if (!File.Exists(config))
        {
            File.WriteAllText(config, "{}");
            _createdConfig = config;
        }
    }

    public void Dispose()
    {
        var runDirectory = Path.Combine(HarnessEnvironment.RunsRoot, _runId);

        if (Directory.Exists(runDirectory))
        {
            Directory.Delete(runDirectory, recursive: true);
        }

        if (Directory.Exists(HarnessEnvironment.RunsRoot) && !Directory.EnumerateFileSystemEntries(HarnessEnvironment.RunsRoot).Any())
        {
            Directory.Delete(HarnessEnvironment.RunsRoot);
        }

        if (_createdConfig is not null)
        {
            File.Delete(_createdConfig);
        }

        _variables.Dispose();
        _scratch.Dispose();
    }

    [Theory]
    [InlineData("plan", "ams,core")]
    [InlineData("run", "core,ams")]
    [InlineData("plan", "ams,azure")]
    [InlineData("run", "AMS,eval")]
    public void TheAmsPackIsRefusedBesideAnyOtherPack(string command, string packs)
    {
        Assert.Equal(AloneMessage, Assert.Throws<E2EException>(() => CommandLine.Parse([command, "--packs", packs])).Message);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("run")]
    public void TheAmsPackIsAcceptedAlone(string command)
    {
        Assert.Equal(["ams"], CommandLine.Parse([command, "--packs", "ams", "--smoke"]).Filters.Packs);
    }

    [Fact]
    public async Task TheRefusalEndsTheHarnessWithTheConfigurationExitCode()
    {
        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessProgram.Main(["run", "--packs", "core,ams"]));

        Assert.Equal(2, exitCode);
        Assert.Contains(AloneMessage, output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAmsPackRunsOnlyWhenNamedAndThenRunsAlone()
    {
        Scenario[] candidates = [AmsMcsTestKit.AmsScenario() with { Id = "MA-01", Feature = "mcs-agent" }, AmsMcsTestKit.AmsScenario() with { Id = "AM-02" }, AmsMcsTestKit.AmsScenario() with { Id = "SW-01", Feature = "mcs-standaloneworkflow" }];

        Assert.Equal(["MA-01"], ScenarioCatalogue.Select(candidates, new RunFilters()).Select(scenario => scenario.Id));
        Assert.Equal(["AM-02"], ScenarioCatalogue.Select(candidates, new RunFilters { Packs = ["ams"] }).Select(scenario => scenario.Id));
        Assert.Empty(ScenarioCatalogue.Select(candidates, new RunFilters { Features = [AmsMcsServices.Feature] }));
        Assert.Empty(ScenarioCatalogue.Select(candidates, new RunFilters { Packs = ["ams"], Features = ["mcs-agent"] }));
    }

    [Fact]
    public void TheAmsScenariosKeepTheScriptsOrderAndEveryIdIsUnique()
    {
        var ids = ScenarioCatalogue.All().Select(scenario => scenario.Id).ToArray();

        Assert.Equal(["AM-01", "AM-02", "AM-03", "AM-04", "AM-05", "AM-06", "AM-07", "AM-08", "AM-10", "AM-11", "AM-12", "AM-13", "AM-14", "AM-15", "AM-09", "AM-16", "AM-18", "AM-19", "AM-20", "AM-21", "AM-25", "AM-26", "AM-28", "AM-29", "AM-32", "AM-33", "AM-35", "AM-36", "AM-37"], ScenarioCatalogue.Select(new RunFilters { Packs = ["ams"] }).Select(scenario => scenario.Id));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheAmsSmokeRunHoldsTheHomeProjectsCreateDeploymentsDevDeploysAndDelete()
    {
        Assert.Equal(["AM-02", "AM-10", "AM-11", "AM-12", "AM-16"], ScenarioCatalogue.Select(new RunFilters { Packs = ["ams"], Smoke = true }).Select(scenario => scenario.Id));
    }

    [Fact]
    public void AFullAmsRunMakesFourteenRepositoriesAndASmokeRunTwo()
    {
        Assert.Equal(14, AmsRepositories.Count(AmsMcsScenarios.Repositories, ScenarioCatalogue.Select(new RunFilters { Packs = ["ams"] }).Select(scenario => scenario.Id)));
        Assert.Equal(2, AmsRepositories.Count(AmsMcsScenarios.Repositories, ScenarioCatalogue.Select(new RunFilters { Packs = ["ams"], Smoke = true }).Select(scenario => scenario.Id)));
    }

    [Fact]
    public void ASharedSetupsRepositoryIsCountedOnceAndEachScenariosOwnAreAdded()
    {
        var uses = new Dictionary<string, AmsRepositoryUse>(StringComparer.Ordinal)
        {
            ["AM-02"] = new(["home"], 0),
            ["AM-03"] = new(["home"], 1),
            ["AM-07"] = new(["home", "workflows"], 2),
        };

        Assert.Equal(5, AmsRepositories.Count(uses, ["AM-02", "AM-03", "AM-07", "AM-99"]));
        Assert.Equal(2, AmsRepositories.Count(uses, ["AM-03", "AM-02"]));
        Assert.Equal(0, AmsRepositories.Count(uses, ["AM-99"]));
        Assert.Equal(0, AmsRepositories.Count(uses, []));
    }

    [Fact]
    public async Task PlanWithTheAmsPackShowsItsCapabilitiesProjectsAndManualCleanup()
    {
        SetAmsMcsVariables(_scratch.Path);

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.PlanAsync(Options("plan", "--packs", "ams"), CancellationToken.None));

        var projects = Path.Combine(_scratch.Path, _runId);
        Assert.Equal(0, exitCode);
        Assert.Contains($"  {"amsMcs",-18} yes   AmsMcsAgents, data plane https://data-plane.example.invalid", output, StringComparison.Ordinal);
        Assert.Contains($"  {"amsMcs.ring",-18} yes   dev aaaaaaaa…, test bbbbbbbb…, prod cccccccc…", output, StringComparison.Ordinal);
        Assert.Contains($"  {"amsMcs.root",-18} yes   {projects}", output, StringComparison.Ordinal);
        Assert.Contains($"  {"git",-18} ", output, StringComparison.Ordinal);
        Assert.Contains($"  {"amsMcs.oracle",-18} NO    not probed: no mcs-ams scenario is scheduled", output, StringComparison.Ordinal);
        Assert.Contains($"  {"amsMcs.connection",-18} NO    not probed: no scheduled scenario needs it", output, StringComparison.Ordinal);
        Assert.Contains($"  {"amsMcs.endpoints",-18} NO    not probed: no mcs-ams scenario is scheduled", output, StringComparison.Ordinal);
        Assert.Contains("Will run 0 scenarios", output, StringComparison.Ordinal);
        Assert.Contains($"Projects {projects}", output, StringComparison.Ordinal);
        Assert.All(AmsMcsServices.ManualCleanup, example => Assert.Contains($"  {example.Kind,-14} {Cleanup.ManualCommand(example)}", output, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanWithAnUnusableRootKeepsTheProjectsInTheRunFolder()
    {
        SetAmsMcsVariables(null);

        var (_, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.PlanAsync(Options("plan", "--packs", "ams"), CancellationToken.None));

        Assert.Contains($"  {"amsMcs.root",-18} NO    no amsMcs.workspaceRoot supplied", output, StringComparison.Ordinal);
        Assert.Contains($"Projects {Path.Combine(HarnessEnvironment.RunsRoot, _runId, "ws")}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanWithoutTheAmsPackHidesItsCapabilities()
    {
        SetAmsMcsVariables(_scratch.Path);

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.PlanAsync(Options("plan", "--only", "AM-00"), CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Capabilities", output, StringComparison.Ordinal);
        Assert.DoesNotContain("amsMcs", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Projects ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanWithTheStandalonePackShowsItsCapabilitiesAndStillHidesTheAmsOnes()
    {
        using var development = new VariableScope(E2EConfig.EnvironmentPrefix + "MCS_DEV_ENV", E2EConfig.LegacyEnvironmentPrefix + "MCS_DEV_ENV");
        SetAmsMcsVariables(_scratch.Path);

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.PlanAsync(Options("plan", "--packs", "swf", "--only", "SW-E03"), CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains($"  {"sw.oracle",-18} NO    mcs.devEnvironmentId is not set", output, StringComparison.Ordinal);
        Assert.DoesNotContain("amsMcs", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUsageNamesTheAmsPackAndItsFeature()
    {
        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessProgram.Main([]));

        Assert.Equal(2, exitCode);
        Assert.Contains("ams runs MCS on AMS alone, on the test ring", output, StringComparison.Ordinal);
        Assert.Contains("identity, mcs-eval, mcs-ams", output, StringComparison.Ordinal);
        Assert.Contains("SW-B0*, AM-12", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithTheAmsPackReleasesThroughTheAmsServices()
    {
        SetAmsMcsVariables(_scratch.Path);

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.RunAsync(Options("run", "--packs", "ams", "--yes"), CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains($"Projects {Path.Combine(_scratch.Path, _runId)}", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(HarnessEnvironment.RunsRoot, _runId, "report.json")));
    }

    [Fact]
    public async Task ACleanupThatRetainsARepositoryIsNoFailure()
    {
        SetAmsMcsVariables(_scratch.Path);
        var script = AmsMcsTestKit.MsagentScript(_scratch.Folder("bin"), "grs-token", AmsMcsTestKit.AgentList(AmsMcsTestKit.Elsewhere));
        Ledger.Create(Path.Combine(HarnessEnvironment.RunsRoot, _runId), _runId).RecordGrsRepository("repository-1", $"https://{AmsMcsTestKit.GitHost}/appframework/tenants/t/environments/e/git/repositories/repository-1", AmsMcsTestKit.Dev, "AM-02");

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.CleanupAsync(CommandLine.Parse(["cleanup", "--run-id", _runId, "--msagent-path", script]), CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Kept on purpose", output, StringComparison.Ordinal);
        Assert.Equal(ResourceState.Retained, Assert.Single(Ledger.Open(Path.Combine(HarnessEnvironment.RunsRoot, _runId), _runId).Entries).State);
    }

    [Fact]
    public async Task ACleanupThatOrphansARepositoryEndsWithExitCodeThree()
    {
        SetAmsMcsVariables(_scratch.Path);
        var script = AmsMcsTestKit.MsagentScript(_scratch.Folder("bin"), "grs-token", AmsMcsTestKit.AgentList());
        Ledger.Create(Path.Combine(HarnessEnvironment.RunsRoot, _runId), _runId).RecordGrsRepository("repository-1", $"https://{AmsMcsTestKit.GitHost}/not-a-repository", AmsMcsTestKit.Dev, "AM-02");

        var (exitCode, output) = await AmsMcsTestKit.CaptureAsync(() => HarnessCommands.CleanupAsync(CommandLine.Parse(["cleanup", "--run-id", _runId, "--msagent-path", script]), CancellationToken.None));

        Assert.Equal(3, exitCode);
        Assert.Contains("Refused a DELETE request", output, StringComparison.Ordinal);
        Assert.DoesNotContain("grs-token", File.ReadAllText(Path.Combine(HarnessEnvironment.RunsRoot, _runId, "transcript.log")), StringComparison.Ordinal);
    }

    private CommandOptions Options(params string[] arguments) => CommandLine.Parse([.. arguments, "--run-id", _runId, "--only", "AM-00", "--msagent-path", AmsMcsTestKit.MsagentScript(_scratch.Folder("bin"), "plan-token", "{}")]);

    private static void SetAmsMcsVariables(string? workspaceRoot)
    {
        var config = AmsMcsTestKit.Config();
        VariableScope.AmsMcs("FEATURES", config.Features!);
        VariableScope.AmsMcs("DATA_PLANE_ENDPOINT", config.DataPlane.Endpoint!);
        VariableScope.AmsMcs("DATA_PLANE_AUDIENCE", config.DataPlane.Audience!);
        VariableScope.AmsMcs("ON_BEHALF_OF_ENDPOINT", config.OnBehalfOf.Endpoint!);
        VariableScope.AmsMcs("ON_BEHALF_OF_AUDIENCE", config.OnBehalfOf.Audience!);
        VariableScope.AmsMcs("CONTROL_PLANE_ENDPOINT", config.ControlPlane.Endpoint!);
        VariableScope.AmsMcs("CONTROL_PLANE_AUDIENCE", config.ControlPlane.Audience!);
        VariableScope.AmsMcs("GIT_HOST", config.GitHost!);
        VariableScope.AmsMcs("DEV_ENVIRONMENT_ID", AmsMcsTestKit.Dev);
        VariableScope.AmsMcs("TEST_ENVIRONMENT_ID", AmsMcsTestKit.Test);
        VariableScope.AmsMcs("PROD_ENVIRONMENT_ID", AmsMcsTestKit.Prod);
        VariableScope.AmsMcs("ORGANIZATIONS_DEV", config.Organizations.Dev!);
        VariableScope.AmsMcs("ORGANIZATIONS_TEST", config.Organizations.Test!);
        VariableScope.AmsMcs("ORGANIZATIONS_PROD", config.Organizations.Prod!);

        if (workspaceRoot is not null)
        {
            VariableScope.AmsMcs("WORKSPACE_ROOT", workspaceRoot);
        }
    }
}
