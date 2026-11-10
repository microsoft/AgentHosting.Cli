using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsRecordTests : IDisposable
{
    private const string Project = "C:\\work\\home";

    private const string GitUrl = "https://git.example.invalid/r";

    private readonly ScratchDirectory _scratch = new();
    private readonly FakeMsagent _msagent = new();
    private readonly Ledger _ledger;

    public AmsMcsRecordTests() => _ledger = Ledger.Create(_scratch.Folder("ledger"), "run-1");

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void ACreateThatMadeARepositoryRecordsTheAgentItsBotAndTheRepository()
    {
        var outcome = Services().Record(_ledger, Result($$$"""{"success":true,"status":"agent-created","agentId":"agent-1","displayName":"ahe2e Home A","schemaName":"cr123_home","ams":{"repositoryId":"repository-1","gitUrl":"{{{GitUrl}}}","repositoryCreated":true}}"""), Project, amsOff: false, "AM-02");

        Assert.Equal("agent-created", outcome.Status);
        Assert.Equal([ResourceKind.AmsAgent, ResourceKind.LeftoverBot, ResourceKind.GrsRepository], _ledger.Entries.Select(entry => entry.Kind));
        Assert.All(_ledger.Entries, entry => Assert.Equal("AM-02", entry.CreatedBy));
        Assert.Equal(("agent-1", "ahe2e Home A", Project, false), (_ledger.Entries[0].Id, _ledger.Entries[0].DisplayName, _ledger.Entries[0].Cleanup.ProjectDirectory, _ledger.Entries[0].Cleanup.AmsOff));
        Assert.Equal("cr123_home", _ledger.Entries[1].Id);
        Assert.Equal(("repository-1", GitUrl, AmsMcsTestKit.Dev), (_ledger.Entries[2].Id, _ledger.Entries[2].Cleanup.GitUrl, _ledger.Entries[2].Cleanup.EnvironmentId));
    }

    [Fact]
    public void AJoinRecordsTheAgentAndKeepsTheRepositoryItJoinedOutOfTheLedger()
    {
        Services().Record(_ledger, Result($$$"""{"success":true,"agentId":"agent-2","displayName":"ahe2e Home B","ams":{"repositoryId":"repository-1","gitUrl":"{{{GitUrl}}}","repositoryCreated":false}}"""), Project, amsOff: true, "AM-03");

        var agent = Assert.Single(_ledger.Entries);
        Assert.Equal(ResourceKind.AmsAgent, agent.Kind);
        Assert.True(agent.Cleanup.AmsOff);
    }

    [Theory]
    [InlineData("""{"success":true,"status":"cancelled","agentId":"","schemaName":""}""")]
    [InlineData("""{"success":true,"status":"deployment-slot-created"}""")]
    [InlineData("""{"success":false,"errorCode":19010,"agentId":"agent-1","schemaName":"cr123_home"}""")]
    [InlineData("Unhandled exception.")]
    public void AResultWithoutNewIdsRecordsNothing(string stdout)
    {
        Services().Record(_ledger, Result(stdout), Project, amsOff: false, "AM-05");

        Assert.Empty(_ledger.Entries);
    }

    [Fact]
    public async Task TheAmsListIsReadWithAmsOnForAnEnvironmentOneRepositoryOrOneAgentType()
    {
        _msagent.Answer("agent list", AmsMcsTestKit.AgentList("agent-1"));
        var services = Services();

        await services.ListAgentsAsync(AmsMcsTestKit.Dev, null, null, CancellationToken.None);
        var listed = await services.ListAgentsAsync(AmsMcsTestKit.Test, "repository-1", null, CancellationToken.None);
        await services.ListAgentsAsync(AmsMcsTestKit.Dev, "repository-1", "MCSWorkflow", CancellationToken.None);

        Assert.Equal(["agent", "list", "--view", "ams", "--environment-id", AmsMcsTestKit.Dev, "--limit", "1000", "--json", "--non-interactive"], _msagent.Calls[0].Arguments);
        Assert.Equal(["agent", "list", "--view", "ams", "--environment-id", AmsMcsTestKit.Test, "--repository-id", "repository-1", "--limit", "1000", "--json", "--non-interactive"], _msagent.Calls[1].Arguments);
        Assert.Equal(["agent", "list", "--view", "ams", "--environment-id", AmsMcsTestKit.Dev, "--repository-id", "repository-1", "--agent-type", "MCSWorkflow", "--limit", "1000", "--json", "--non-interactive"], _msagent.Calls[2].Arguments);
        Assert.All(_msagent.Calls, call => Assert.Equal(AmsMcsServices.CallTimeout, call.Timeout));
        Assert.All(_msagent.Calls, call => Assert.Equal("AmsMcsAgents", call.Environment["AH_CLI_FEATURES"]));
        Assert.Contains("\"agentId\":\"agent-1\"", listed.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDevOrganizationIsTheOneConfiguredForTheDevEnvironment()
    {
        Assert.Equal(new Uri("https://dev.example.invalid"), Services().DevOrganization);
        Assert.Null(Services(AmsMcsTestKit.Config() with { DevEnvironmentId = null }).DevOrganization);
    }

    private static MsagentResult Result(string stdout) => new(["agent", "create"], 0, stdout, string.Empty, TimeSpan.Zero);

    private AmsMcsServices Services(AmsMcsConfig? config = null)
    {
        var transcript = new Transcript(_scratch.Folder("run"));
        return new AmsMcsServices(config ?? AmsMcsTestKit.Config(), "run-1", _msagent.Cli(transcript, _scratch.Path), transcript, new FakeTokens());
    }
}
