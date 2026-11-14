using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class HarnessSafetyStopTests : IDisposable
{
    private const string ProtectedEnvironmentId = "0c5a1e3d-0000-4000-8000-00000000beef";
    private const string ProtectedOrganizationHost = "orgprotected.crm.dynamics.com";
    private const string CorpusAgentFolder = "Corpus Agent";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "msagent-safety-" + Guid.NewGuid().ToString("N"));

    public HarnessSafetyStopTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private MsagentCli GuardedMsagent(FakeMsagent fake) => new(fake.ExecutablePath, new Transcript(_root), Path.Combine(_root, "dumps"), [ProtectedEnvironmentId]);

    private Workspaces CorpusWorkspaces() => new(_root, Path.Combine(_root, "workspaces"), new E2EConfig { Mcs = new McsConfig { ForbiddenEnvironmentIds = [ProtectedEnvironmentId], Copy = new CopyConfig { CorpusRoot = Path.Combine(_root, "corpus") } } }, "beef");

    private string CorpusAgent(string topicComment)
    {
        var agent = Path.Combine(_root, "corpus", CorpusAgentFolder);
        Directory.CreateDirectory(Path.Combine(agent, ".mcs"));
        Directory.CreateDirectory(Path.Combine(agent, "topics"));
        File.WriteAllText(Path.Combine(agent, ".mcs", "conn.json"), $$"""{"EnvironmentId":"{{ProtectedEnvironmentId}}","DataverseEndpoint":"https://{{ProtectedOrganizationHost}}/"}""");
        File.WriteAllText(Path.Combine(agent, "topics", "Greeting.mcs.yml"), $"kind: AdaptiveDialog\n# {topicComment}\n");
        return agent;
    }

    [Theory]
    [InlineData($"agent list --environment-id {ProtectedEnvironmentId} --json")]
    [InlineData("agent create --agent-type MCSWorkflow --mcs-clone-environment-id 0C5A1E3D-0000-4000-8000-00000000BEEF")]
    [InlineData($"deployment create --deployment-name dev --environment-id={ProtectedEnvironmentId}")]
    public async Task ACommandNamingAProtectedEnvironmentIsRefusedBeforeMsagentStarts(string commandLine)
    {
        var fake = new FakeMsagent(Path.Combine(_root, "msagent"), "{}");

        var refusal = await Assert.ThrowsAsync<E2EException>(() => GuardedMsagent(fake).RunAsync(commandLine.Split(' ')));

        Assert.Contains(ProtectedEnvironmentId, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Invocations);
    }

    [Fact]
    public async Task ACommandNamingNoProtectedEnvironmentStillRuns()
    {
        var fake = new FakeMsagent(Path.Combine(_root, "msagent"), "{}");

        var result = await GuardedMsagent(fake).RunAsync(["agent", "list", "--environment-id", "env-1", "--json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["agent list --environment-id env-1 --json"], fake.Invocations);
    }

    [Theory]
    [InlineData($"copied from {ProtectedEnvironmentId}")]
    [InlineData("see https://ORGPROTECTED.crm.dynamics.com/api/data/v9.2")]
    public void ACorpusAgentNamingAProtectedEnvironmentOutsideItsBindingIsRefusedAndTheCorpusIsLeftUnchanged(string topicComment)
    {
        var agent = CorpusAgent(topicComment);
        var before = TreeSnapshot.Take(agent);

        var refusal = Assert.Throws<E2EException>(() => CorpusWorkspaces().CorpusCopy("MC-01", CorpusAgentFolder));

        Assert.Contains("names a forbidden environment", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Greeting.mcs.yml", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(TreeSnapshot.Take(agent).DifferencesFrom(before));
    }

    [Fact]
    public void ACorpusAgentBoundToAProtectedEnvironmentIsCopiedWithoutItsBindingAndTheCorpusIsLeftUnchanged()
    {
        var agent = CorpusAgent("greets the user");
        var before = TreeSnapshot.Take(agent);

        var copy = CorpusWorkspaces().CorpusCopy("MC-01", CorpusAgentFolder);

        Assert.True(copy.DeployForbidden);
        Assert.True(File.Exists(Path.Combine(copy.ProjectDirectory, "topics", "Greeting.mcs.yml")));
        Assert.False(Directory.Exists(Path.Combine(copy.ProjectDirectory, ".mcs")));
        Assert.Empty(TreeSnapshot.Take(agent).DifferencesFrom(before));
    }
}
