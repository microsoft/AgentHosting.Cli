using Microsoft.Agents.Hosting.Cli.Core.Ams;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsGitDeployStatesTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public AmsGitDeployStatesTests() => _ams.LinkToRepository();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public async Task AFolderThatHoldsThePushedHead_IsExact()
    {
        Assert.Equal(new AmsGitDeployState(McsRecords.Commit, true, true, false, null), await Read());
        Assert.Contains($"status {AmsHarness.AgentFolder}", _ams.Git.Calls);
    }

    [Fact]
    public async Task EditsInTheAgentsFolder_MakeItInexact()
    {
        _ams.Git.Changed.Add($"{AmsHarness.AgentFolder}/topics/Greeting.mcs.yml");
        _ams.Git.Changed.Add("Payroll/topics/Greeting.mcs.yml");

        Assert.Equal(new AmsGitDeployState(McsRecords.Commit, false, true, true, null), await Read());
    }

    [Fact]
    public async Task AHeadTheRemoteLacks_IsBasedOnTheMergeBaseWithMain()
    {
        _ams.Git.HeadPushed = false;
        _ams.Git.MergeBase = McsRecords.OlderCommit;

        Assert.Equal(new AmsGitDeployState(McsRecords.OlderCommit, false, false, false, null), await Read());
        Assert.Contains("merge-base HEAD origin/main", _ams.Git.Calls);
    }

    [Fact]
    public async Task AFolderThatSharesNoHistoryWithTheRepository_NamesNoCommit()
    {
        _ams.Git.HeadPushed = false;
        _ams.Git.MergeBase = null;

        Assert.Equal(new AmsGitDeployState(null, false, false, false, "The folder shares no commit with the repository"), await Read());
    }

    [Fact]
    public async Task AFolderWithoutACommit_NamesNoCommit()
    {
        _ams.Git.Commits.Clear();

        Assert.Equal(new AmsGitDeployState(null, false, false, false, $"{_ams.Root} holds no git commit"), await Read());
    }

    [Fact]
    public async Task WithoutGit_NoCommitIsNamed()
    {
        _ams.Git.Missing = true;

        Assert.Equal(new AmsGitDeployState(null, false, false, false, "git was not found"), await Read());
    }

    private Task<AmsGitDeployState> Read() => _ams.States.ReadAsync(_ams.Root, _ams.AddAgent(), default);
}
