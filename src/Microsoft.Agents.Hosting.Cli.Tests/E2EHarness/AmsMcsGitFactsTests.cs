using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsGitFactsTests : IDisposable
{
    private const string InitialSha = "1111111111111111111111111111111111111111";
    private const string AddSha = "abcdef0123456789abcdef0123456789abcdef01";
    private const string GitUrl = $"https://{AmsMcsTestKit.GitHost}/appframework/tenants/{AmsMcsTestKit.Tenant}/environments/{AmsMcsTestKit.Dev}/git/repositories/{RepositoryId}";
    private const string RepositoryId = "22222222-aaaa-4000-8000-00000000000b";

    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData(AddSha, true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0", false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef012", false)]
    [InlineData("gbcdef0123456789abcdef0123456789abcdef01", false)]
    [InlineData(null, false)]
    public void AShaIsFortyHexDigits(string? value, bool sha)
    {
        Assert.Equal(sha, GitCommits.IsSha(value));
    }

    [Theory]
    [InlineData("abcdef0", AddSha, true)]
    [InlineData(AddSha, "ABCDEF0123", true)]
    [InlineData("abcdef", "abcdef", false)]
    [InlineData("abcdef0", "abcdef1", false)]
    [InlineData("abcdef0", null, false)]
    [InlineData(null, "abcdef0", false)]
    public void CommitsMatchByAPrefixOfSevenOrMoreEitherWayWithoutCase(string? first, string? second, bool match)
    {
        Assert.Equal(match, GitCommits.Match(first, second));
    }

    [Theory]
    [InlineData(AddSha, "abcdef0123", "", true)]
    [InlineData(AddSha, InitialSha, "", false)]
    [InlineData(AddSha, AddSha, " M notes.txt", false)]
    [InlineData(null, AddSha, "", false)]
    public void AFolderHoldsMainWhenItsHeadIsMainAndNothingIsUncommitted(string? head, string? remote, string status, bool holds)
    {
        Assert.Equal(holds, new GitTip(head, remote, status.Length == 0 ? [] : [status]).HoldsRemote);
    }

    [Theory]
    [InlineData(AddSha, "abcdef0")]
    [InlineData("abcdef01", "abcdef0")]
    [InlineData("abcdef0", "abcdef0")]
    [InlineData("abc", "abc")]
    [InlineData(null, "")]
    public void ACommitIsShortenedToItsFirstSevenDigits(string? sha, string expected)
    {
        Assert.Equal(expected, GitCommits.Short(sha));
    }

    [Fact]
    public void AFolderStatusIsDescribedAsCleanOrByItsLines()
    {
        Assert.Equal("clean", GitTip.Describe([]));
        Assert.Equal(" M notes.txt; ?? stray.txt", GitTip.Describe([" M notes.txt", "?? stray.txt"]));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("README.md", true)]
    [InlineData(".gitignore|README.md", true)]
    [InlineData("README.md|Agent A/settings.mcs.yml", false)]
    [InlineData("readme.md", false)]
    public void ARepositoryHoldsOnlyItsSeedWithJustTheReadmeAndTheIgnoreFile(string files, bool seed)
    {
        Assert.Equal(seed, GitCloneFacts.HoldsOnlySeed(files.Split('|', StringSplitOptions.RemoveEmptyEntries)));
        Assert.False(GitCloneFacts.HoldsOnlySeed(null));
    }

    [Fact]
    public void ACloneShapedAsGrsMakesItHoldsEveryFact()
    {
        var clone = Clone();
        var directory = _scratch.Folder("project");

        Assert.True(clone.IsRootOf(directory + Path.DirectorySeparatorChar));
        Assert.True(clone.OriginIs(GitUrl.ToUpperInvariant()));
        Assert.True(clone.HeadIs(AddSha[..10], "msagent: add MCSAgent A"));
        Assert.True(clone.ParentIs(InitialSha));
        Assert.True(clone.ParentIsGrsInitialCommit);
        Assert.True(clone.ChangesOnly(["Agent A"]));
        Assert.Empty(clone.ChangedOutside(["Agent A"]));
        Assert.True(clone.IsClean);
        Assert.True(clone.KeepsConfigOut);
        Assert.True(clone.KeepsCacheOut);
        Assert.True(clone.PushedMain(AddSha));
        Assert.True(clone.HasMsagentHelper(AmsMcsTestKit.GitHost));
    }

    [Fact]
    public void TheRootTheOriginTheHeadAndItsParentMustBeTheExpectedOnes()
    {
        var clone = Clone();
        var directory = _scratch.Folder("project");

        Assert.False(clone.IsRootOf(Path.Combine(directory, "Agent A")));
        Assert.False((clone with { TopLevel = null }).IsRootOf(directory));
        Assert.False((clone with { Origin = null }).OriginIs(GitUrl));
        Assert.False(clone.OriginIs(GitUrl + "-other"));
        Assert.False(clone.OriginIs(null));
        Assert.False(clone.HeadIs(InitialSha, "msagent: add MCSAgent A"));
        Assert.False(clone.HeadIs(AddSha, "msagent: add mcsagent A"));
        Assert.False((clone with { Head = null }).HeadIs(AddSha, "msagent: add MCSAgent A"));
        Assert.False(clone.ParentIs(AddSha));
        Assert.False((clone with { Parent = null }).ParentIs(InitialSha));
        Assert.False(clone.PushedMain(InitialSha));
        Assert.False((clone with { RemoteMain = null }).PushedMain(AddSha));
    }

    [Fact]
    public void GrsInitialCommitIsByGrsWithNoParentAndOnlyTheReadme()
    {
        var clone = Clone();

        Assert.False((clone with { Parent = null }).ParentIsGrsInitialCommit);
        Assert.False((clone with { Parent = clone.Parent! with { AuthorEmail = "harness@example.invalid" } }).ParentIsGrsInitialCommit);
        Assert.False((clone with { Parent = clone.Parent! with { Parents = [InitialSha] } }).ParentIsGrsInitialCommit);
        Assert.False((clone with { ParentTree = null }).ParentIsGrsInitialCommit);
        Assert.False((clone with { ParentTree = ["README.md", "LICENSE"] }).ParentIsGrsInitialCommit);
        Assert.False((clone with { ParentTree = ["NOTES.md"] }).ParentIsGrsInitialCommit);
    }

    [Fact]
    public void TheCommitMayChangeOnlyItsFoldersAndTheIgnoreFile()
    {
        var clone = Clone();

        Assert.Equal(["Agent A2/settings.mcs.yml", "agent a/topic.yml", "README.md"], (clone with { Changed = ["Agent A/settings.mcs.yml", "Agent A2/settings.mcs.yml", "agent a/topic.yml", "README.md", ".gitignore"] }).ChangedOutside(["Agent A"]));
        Assert.True((clone with { Changed = ["Agent A/settings.mcs.yml", "Agent B/settings.mcs.yml"] }).ChangesOnly(["Agent A", "Agent B"]));
        Assert.False((clone with { Changed = ["Agent B/settings.mcs.yml"] }).ChangesOnly(["Agent A"]));
        Assert.False((clone with { Changed = [] }).ChangesOnly(["Agent A"]));
        Assert.False((clone with { Changed = null }).ChangesOnly(["Agent A"]));
        Assert.Empty((clone with { Changed = null }).ChangedOutside(["Agent A"]));
    }

    [Fact]
    public void TheCloneIsCleanAndKeepsTheConfigAndTheCacheOutOfGit()
    {
        var clone = Clone();

        Assert.False((clone with { Status = ["?? stray.txt"] }).IsClean);
        Assert.False((clone with { Status = null }).IsClean);
        Assert.True((clone with { Ignore = ["/.CONFIG/agent.config.json", ".MCS/  "] }).KeepsConfigOut);
        Assert.True((clone with { Ignore = ["/.CONFIG/agent.config.json", ".MCS/  "] }).KeepsCacheOut);
        Assert.False((clone with { Ignore = [" /.config/agent.config.json", ".mcs"] }).KeepsConfigOut);
        Assert.False((clone with { Ignore = [" /.config/agent.config.json", ".mcs"] }).KeepsCacheOut);
        Assert.False((clone with { TrackedConfig = [".config/agent.config.json"] }).KeepsConfigOut);
        Assert.False((clone with { TrackedConfig = null }).KeepsConfigOut);
        Assert.False((clone with { TrackedCache = ["Agent A/.mcs/conn.json"] }).KeepsCacheOut);
        Assert.False((clone with { TrackedCache = null }).KeepsCacheOut);
    }

    [Fact]
    public void TheHelpersResetThenNameMsagentForTheGitHost()
    {
        var clone = Clone();
        var helper = clone.Helpers![1];

        Assert.False((clone with { Helpers = null }).HasMsagentHelper(AmsMcsTestKit.GitHost));
        Assert.False((clone with { Helpers = [helper] }).HasMsagentHelper(AmsMcsTestKit.GitHost));
        Assert.False((clone with { Helpers = ["manager", helper] }).HasMsagentHelper(AmsMcsTestKit.GitHost));
        Assert.False((clone with { Helpers = ["", helper, "manager"] }).HasMsagentHelper(AmsMcsTestKit.GitHost));
        Assert.False(clone.HasMsagentHelper("other.example.invalid"));
        Assert.False((clone with { Helpers = ["", helper.Replace("AH_CLI_FEATURES=AmsMcsAgents ", string.Empty, StringComparison.Ordinal)] }).HasMsagentHelper(AmsMcsTestKit.GitHost));
        Assert.False((clone with { Helpers = ["", $"!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={AmsMcsTestKit.GitHost} '{Path.Combine(_scratch.Path, "missing.cmd")}' auth git-credential"] }).HasMsagentHelper(AmsMcsTestKit.GitHost));
    }

    [Theory]
    [InlineData(GitUrl, true)]
    [InlineData("http://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/r", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/r/extra", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/", false)]
    [InlineData("https://git.example.invalid/APPFRAMEWORK/Tenants/t/Environments/e/Git/Repositories/r", true)]
    public void ARepositoryUrlIsGrsRouteOverHttps(string gitUrl, bool repository)
    {
        Assert.Equal(repository, GrsUrls.IsRepository(new Uri(gitUrl)));
    }

    [Theory]
    [InlineData(GitUrl, true)]
    [InlineData("https://GIT.example.invalid/appframework/tenants/eeeeeeee000040008000000000000005/environments/aaaaaaaa000040008000000000000001/git/repositories/22222222-AAAA-4000-8000-00000000000B", true)]
    [InlineData("https://other.example.invalid/appframework/tenants/eeeeeeee-0000-4000-8000-000000000005/environments/aaaaaaaa-0000-4000-8000-000000000001/git/repositories/22222222-aaaa-4000-8000-00000000000b", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/ffffffff-0000-4000-8000-000000000005/environments/aaaaaaaa-0000-4000-8000-000000000001/git/repositories/22222222-aaaa-4000-8000-00000000000b", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/eeeeeeee-0000-4000-8000-000000000005/environments/bbbbbbbb-0000-4000-8000-000000000002/git/repositories/22222222-aaaa-4000-8000-00000000000b", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/eeeeeeee-0000-4000-8000-000000000005/environments/aaaaaaaa-0000-4000-8000-000000000001/git/repositories/33333333-aaaa-4000-8000-00000000000b", false)]
    [InlineData("https://git.example.invalid/appframework/tenants/eeeeeeee-0000-4000-8000-000000000005/environments/aaaaaaaa-0000-4000-8000-000000000001/git/repositories/22222222-aaaa-4000-8000-00000000000b/x", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void TheGitUrlNamesTheHostTenantEnvironmentAndRepository(string? gitUrl, bool names)
    {
        Assert.Equal(names, GrsUrls.Names(gitUrl, AmsMcsTestKit.GitHost, AmsMcsTestKit.Tenant, AmsMcsTestKit.Dev, RepositoryId));
    }

    [Fact]
    public void AMissingTenantOrEnvironmentNamesNothing()
    {
        Assert.False(GrsUrls.Names(GitUrl, AmsMcsTestKit.GitHost, null, AmsMcsTestKit.Dev, RepositoryId));
        Assert.False(GrsUrls.Names(GitUrl, AmsMcsTestKit.GitHost, AmsMcsTestKit.Tenant, null, RepositoryId));
        Assert.False(GrsUrls.Names(GitUrl, null, AmsMcsTestKit.Tenant, AmsMcsTestKit.Dev, RepositoryId));
    }

    private GitCloneFacts Clone() => new(_scratch.Folder("project").Replace('\\', '/').ToUpperInvariant(), GitUrl, new GitCommitFacts(AddSha, "harness@example.invalid", [InitialSha], "msagent: add MCSAgent A"), new GitCommitFacts(InitialSha, "GRS@PowerPlatform.com", [], "Initial commit"), ["readme.md"], [".gitignore", "Agent A/settings.mcs.yml", "Agent A/topics/greeting.mcs.yml"], [], ["# msagent", "/.config/agent.config.json", ".mcs/"], [], [], ["", $"!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={AmsMcsTestKit.GitHost} '{_scratch.Place("bin/msagent.cmd")}' auth git-credential"], AddSha);
}
