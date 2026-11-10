using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using HarnessCommands = Microsoft.Agents.Hosting.Cli.E2E.Commands;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class WorkspaceRetentionTests : IDisposable
{
    private const string AgentId = "11111111-0000-4000-8000-000000000001";
    private const string RepositoryId = "22222222-0000-4000-8000-000000000002";

    private readonly ScratchDirectory _scratch = new();
    private readonly Ledger _ledger;

    public WorkspaceRetentionTests() => _ledger = Ledger.Create(_scratch.Folder("run"), "run-1");

    private string Root => Path.Combine(_scratch.Path, "ws");

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void APassingScenariosFolderThatAnOrphanedAgentPointsIntoIsKept()
    {
        var project = _scratch.Folder(Path.Combine("ws", "AM-04", "join"));
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-04");
        _ledger.MarkOrphaned(AgentId, "agent delete exited 1");

        Assert.Equal([Path.Combine(Root, "AM-04")], HarnessCommands.RemovePassingWorkspaces([Passed("AM-04")], Root, _ledger.Entries));
        Assert.True(Directory.Exists(project));
    }

    [Fact]
    public void AFolderThatAStillCreatedWorkspaceEntryPointsIntoIsKept()
    {
        var copy = _scratch.Folder(Path.Combine("ws", "AM-06-copy"));
        _ledger.RecordLocalWorkspace(Path.Combine(copy, "clone"), "AM-06");

        Assert.Equal([copy], HarnessCommands.RemovePassingWorkspaces([Passed("AM-06")], Root, _ledger.Entries));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public void AFolderWhoseResourcesWereRemovedIsDeletedWithItsReadOnlyGitObjects()
    {
        var project = _scratch.Folder(Path.Combine("ws", "AM-05", "project"));
        File.SetAttributes(_scratch.Place(Path.Combine("ws", "AM-05", "project", ".git", "objects", "c8", "943f7ac60b8a7ce8433c56ffcebe7a4c626a"), "blob"), FileAttributes.ReadOnly | FileAttributes.Hidden);
        _ledger.RecordAmsAgent(AgentId, "Agent A", project, amsOff: false, "AM-05");
        _ledger.MarkDeleted(AgentId);

        Assert.Empty(HarnessCommands.RemovePassingWorkspaces([Passed("AM-05")], Root, _ledger.Entries));
        Assert.False(Directory.Exists(Path.Combine(Root, "AM-05")));
    }

    [Fact]
    public void EntriesPointingIntoAnotherFolderDoNotKeepAPassingScenariosFolder()
    {
        _scratch.Folder(Path.Combine("ws", "AM-02", "scratch"));
        var home = _scratch.Folder(Path.Combine("ws", "home"));
        _ledger.RecordAmsAgent(AgentId, "Agent A", home, amsOff: false, "AM-02");
        _ledger.MarkOrphaned(AgentId, "agent delete exited 1");
        _ledger.RecordGrsRepository(RepositoryId, $"https://{AmsMcsTestKit.GitHost}/git/repositories/{RepositoryId}", AmsMcsTestKit.Dev, "AM-02");

        Assert.Empty(HarnessCommands.RemovePassingWorkspaces([Passed("AM-02")], Root, _ledger.Entries));
        Assert.False(Directory.Exists(Path.Combine(Root, "AM-02")));
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void TheSummaryAndBothReportsListTheKeptWorkspaces()
    {
        var kept = Path.Combine(Root, "AM-04");
        var runDirectory = _scratch.Folder("report");
        var report = RunReport.From("run-1", DateTimeOffset.UtcNow, TimeSpan.Zero, new MsagentBinary("msagent", "test", "test"), [Passed("AM-04")], []) with { KeptWorkspaces = [kept] };

        var output = AmsMcsTestKit.Capture(() => new Reporter(useColour: false).Summary(report, []));
        Reporter.Persist(runDirectory, report, []);

        Assert.Contains($"{Environment.NewLine}  Workspaces kept, because a resource the run didn't remove points into them{Environment.NewLine}    {kept}{Environment.NewLine}", output, StringComparison.Ordinal);
        Assert.Contains($"## Kept workspaces{Environment.NewLine}{Environment.NewLine}A resource the run didn't remove points into each of them.{Environment.NewLine}{Environment.NewLine}- {kept}{Environment.NewLine}", File.ReadAllText(Path.Combine(runDirectory, "report.md")), StringComparison.Ordinal);
        Assert.Equal([kept], JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory, "report.json"))).RootElement.GetProperty("KeptWorkspaces").EnumerateArray().Select(element => element.GetString()));
    }

    private static ScenarioResult Passed(string scenarioId) => new() { Scenario = new ScenarioDescriptor(scenarioId, "a scenario under test", AmsMcsServices.Feature, SafetyTier.Creating), Outcome = Outcome.Pass };
}
