using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsRepositoryResolverTests : IDisposable
{
    private readonly AmsHarness _ams = new();

    public void Dispose() => _ams.Dispose();

    [Fact]
    public void AnEmptyFolder_WithoutARepository_IsMadeAWorkingTreeOfANewOne() =>
        Assert.Equal(new AmsRepositoryChoice(null, AmsRepositoryAction.InitAndLink, null), Choose());

    [Fact]
    public void AnEmptyOrMissingFolder_WithARepository_IsAClone()
    {
        Assert.Equal(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.Clone, null), Choose(repositoryId: McsRecords.RepositoryId.ToUpperInvariant()));
        Assert.Equal(AmsRepositoryAction.Clone, _ams.Repositories.Choose(Path.Combine(_ams.Root, "New"), null, McsRecords.RepositoryId, [], []).Action);
    }

    [Theory]
    [InlineData(null, AmsRepositoryAction.InitAndLink)]
    [InlineData(McsRecords.RepositoryId, AmsRepositoryAction.Attach)]
    public void AFolderHoldingOnlyTheProject_IsLinkedOrAttached(string? repositoryId, AmsRepositoryAction action)
    {
        _ams.AddAgent(registered: false);
        File.WriteAllText(Path.Combine(_ams.Root, ".gitignore"), "bin/\n");
        Directory.CreateDirectory(Path.Combine(_ams.Root, "Helpdesk"));

        Assert.Equal(new AmsRepositoryChoice(repositoryId, action, null), Choose(repositoryId: repositoryId, projectEntries: ["Helpdesk"]));
    }

    [Fact]
    public void AFolderHoldingOtherFiles_IsRefused_NamingThem()
    {
        _ams.AddAgentWorkspace();
        File.WriteAllText(Path.Combine(_ams.Root, "notes.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_ams.Root, "build"));

        var failure = Assert.Throws<AmsAgentException>(() => Choose());

        Assert.Equal((ErrorCodes.AmsAgent.ProjectFolderNotDedicated, ExitCodes.Usage), (failure.ErrorCode, failure.ExitCode));
        Assert.Equal($"{_ams.Root} holds files that aren't part of the project: build, notes.txt.", failure.Message);
    }

    [Fact]
    public void AWorkingTreeAboveTheProjectFolder_IsRefused()
    {
        var parent = Path.GetDirectoryName(_ams.Root)!;

        var failure = Assert.Throws<AmsAgentException>(() => Choose(new GitWorkingTree(parent, [], "main", true)));

        Assert.Equal(ErrorCodes.AmsAgent.ProjectNotRepositoryRoot, failure.ErrorCode);
        Assert.Contains(parent, failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, AmsRepositoryAction.AddOrigin)]
    [InlineData(McsRecords.RepositoryId, AmsRepositoryAction.Attach)]
    public void AWorkingTreeWithoutARemote_GetsOrigin(string? repositoryId, AmsRepositoryAction action)
    {
        var tree = new GitWorkingTree(_ams.Root, [], "main", true);

        Assert.Equal(new AmsRepositoryChoice(repositoryId, action, tree), Choose(tree, repositoryId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CCCCCCCC-3333-4333-8333-000000000003")]
    public void AWorkingTreeOfTheRepository_NeedsNothing(string? repositoryId)
    {
        var tree = new GitWorkingTree(_ams.Root, [new GitRemote("origin", McsRecords.GitUrl)], "main", true);

        Assert.Equal(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.None, tree), Choose(tree, repositoryId));
    }

    [Fact]
    public void AWorkingTreeOfAnotherRepository_RefusesTheRequestedOne()
    {
        var tree = new GitWorkingTree(_ams.Root, [new GitRemote("origin", McsRecords.OtherGitUrl)], "main", true);

        var failure = Assert.Throws<AmsAgentException>(() => Choose(tree, McsRecords.RepositoryId));

        Assert.Equal(ErrorCodes.AmsAgent.RepositoryMismatch, failure.ErrorCode);
        Assert.Equal($"--repository-id {McsRecords.RepositoryId} differs from repository {McsRecords.OtherRepositoryId} that the folder's remote names.", failure.Message);
    }

    [Fact]
    public void TheOnlyRemote_IsTheRepository_WhateverItsName()
    {
        var tree = new GitWorkingTree(_ams.Root, [new GitRemote("grs", McsRecords.GitUrl)], "main", true);

        Assert.Equal(new AmsRepositoryChoice(McsRecords.RepositoryId, AmsRepositoryAction.None, tree), Choose(tree));
    }

    [Theory]
    [InlineData("origin=https://github.com/contoso/agents.git", "origin (https://github.com/contoso/agents.git)")]
    [InlineData("fork=https://github.com/contoso/agents.git;grs=" + McsRecords.GitUrl, "fork (https://github.com/contoso/agents.git), grs (" + McsRecords.GitUrl + ")")]
    public void AWorkingTreeWhoseRemoteIsNoGrsRepository_IsRefused(string remotes, string named)
    {
        var tree = new GitWorkingTree(_ams.Root, [.. remotes.Split(';').Select(remote => remote.Split('=', 2)).Select(parts => new GitRemote(parts[0], parts[1]))], "main", true);

        var failure = Assert.Throws<AmsAgentException>(() => Choose(tree));

        Assert.Equal(ErrorCodes.AmsAgent.NotAGrsRepository, failure.ErrorCode);
        Assert.Equal($"The folder's remote {named} isn't a GRS repository, and AMS deploys only from GRS.", failure.Message);
    }

    [Fact]
    public void AnInterruptedLink_TakesTheRepositoryTheAgentsAreRegisteredIn()
    {
        var agent = _ams.AddAgent() with { SourceControlMetadata = new AgentSourceControlMetadata(McsRecords.OtherRepositoryId, "main", AmsHarness.AgentFolder) };

        Assert.Equal(new AmsRepositoryChoice(McsRecords.OtherRepositoryId, AmsRepositoryAction.Attach, null), Choose(agents: [agent]));
        var failure = Assert.Throws<AmsAgentException>(() => Choose(repositoryId: McsRecords.RepositoryId, agents: [agent]));
        Assert.Equal((ErrorCodes.AmsAgent.RepositoryMismatch, $"--repository-id {McsRecords.RepositoryId} differs from repository {McsRecords.OtherRepositoryId} that holds the project's registered agents."), (failure.ErrorCode, failure.Message));
    }

    [Fact]
    public void ARepositoryIdThatIsNoId_IsAUsageError() =>
        Assert.Throws<UsageException>(() => Choose(repositoryId: "contoso-agents"));

    private AmsRepositoryChoice Choose(GitWorkingTree? tree = null, string? repositoryId = null, IReadOnlyList<ManagedAgentRecord>? agents = null, params string[] projectEntries) =>
        _ams.Repositories.Choose(_ams.Root, tree, repositoryId, agents ?? [], projectEntries);
}
