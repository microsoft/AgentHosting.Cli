using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsShelfTests : IDisposable
{
    private const string First = "1111111111111111111111111111111111111111";

    private const string Second = "abcdef0123456789abcdef0123456789abcdef01";

    private static readonly DateTimeOffset Earlier = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Earlier.AddMinutes(5);

    private readonly ScratchDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", true)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdef0", false)]
    [InlineData("0123456789abcdeg", false)]
    public void AKeyIsSixteenHexDigits(string key, bool valid)
    {
        Assert.Equal(valid, Shelf(key: key).KeyIsValid);
    }

    [Fact]
    public void ACopyIsCompleteWithItsCommitBranchAndSaveTime()
    {
        Assert.True(Shelf(copies: [new(Second, "main", Later), new(First, "feature", Earlier)]).Complete);
        Assert.True(Shelf().Complete);
        Assert.False(Shelf(copies: [new(Second, null, Later)]).Complete);
        Assert.False(Shelf(copies: [new(Second, string.Empty, Later)]).Complete);
        Assert.False(Shelf(copies: [new(Second, "main", null)]).Complete);
        Assert.False(Shelf(copies: [new("abcdef0", "main", Later)]).Complete);
    }

    [Fact]
    public void CopiesAreNewestFirstWhenNoneWasSavedAfterTheOneBeforeIt()
    {
        Assert.True(Shelf(copies: [new(Second, "main", Later), new(First, "main", Earlier)]).NewestFirst);
        Assert.True(Shelf(copies: [new(Second, "main", Later), new(First, "main", Later)]).NewestFirst);
        Assert.True(Shelf(copies: [new(Second, "main", Later)]).NewestFirst);
        Assert.False(Shelf(copies: [new(First, "main", Earlier), new(Second, "main", Later)]).NewestFirst);
        Assert.False(Shelf(copies: [new(Second, "main", null), new(First, "main", Earlier)]).NewestFirst);
        Assert.False(Shelf(copies: [new(Second, "main", Later), new(First, "main", null)]).NewestFirst);
    }

    [Fact]
    public void AShelfHoldsACommitItBothListsAndStores()
    {
        var shelf = Shelf(copies: [new(Second, "main", Later), new(First, "main", Earlier)], stored: [Second, "3333333"]);

        Assert.True(shelf.Holds(Second));
        Assert.True(shelf.Holds(Second.ToUpperInvariant()));
        Assert.False(shelf.Holds(First));
        Assert.False(shelf.Holds("3333333333333333333333333333333333333333"));
        Assert.False(shelf.Holds("abcdef0"));
        Assert.False(shelf.Holds(null));
    }

    [Fact]
    public void AShelfIdentifiesAsItsEnvironmentAndCopilotStudioAgent()
    {
        var shelf = Shelf(identity: $"{AmsMcsTestKit.Dev}|bot-1");

        Assert.True(shelf.IdentifiesAs(AmsMcsTestKit.Dev.ToUpperInvariant(), "BOT-1"));
        Assert.False(shelf.IdentifiesAs(AmsMcsTestKit.Test, "bot-1"));
        Assert.False(shelf.IdentifiesAs(AmsMcsTestKit.Dev, "bot-2"));
        Assert.False(Shelf().IdentifiesAs(AmsMcsTestKit.Dev, "bot-1"));
    }

    [Fact]
    public void TheShelfOfAFolderIsTheOneNamingItOrElseTheOnlyOne()
    {
        var first = Shelf(key: "a", folder: "Agent A");
        var second = Shelf(key: "b", folder: "Agents/Agent B");
        var unnamed = Shelf(key: "c");

        Assert.Same(second, McsShelf.Find([first, second], "Agent B"));
        Assert.Same(unnamed, McsShelf.Find([unnamed], "Agent B"));
        Assert.Same(first, McsShelf.Find([first], "Agent B"));
        Assert.Null(McsShelf.Find([first, unnamed], "Agent B"));
        Assert.Null(McsShelf.Find([], "Agent B"));
    }

    [Fact]
    public void AShelfIsEmptyWithoutCopiesOrStoredFolders()
    {
        Assert.True(McsShelf.IsEmpty(null));
        Assert.True(McsShelf.IsEmpty(Shelf()));
        Assert.False(McsShelf.IsEmpty(Shelf(copies: [new(Second, "main", Later)])));
        Assert.False(McsShelf.IsEmpty(Shelf(stored: [Second])));
    }

    [Fact]
    public void AShelfIsDescribedWithItsIdentityLiveCommitCopiesAndStoredFolders()
    {
        var shelf = Shelf(folder: "Agent A", identity: "env|bot", live: Second, copies: [new(Second, "main", Later), new(First, null, null)], stored: [Second, First]);

        Assert.Equal($"shelf 0123456789abcdef: folder='Agent A' identity='env|bot' live=abcdef0 copies=abcdef0@main@{Later:O},1111111@@ stored=abcdef0,1111111", McsShelf.Describe(shelf));
        Assert.Equal("no shelf", McsShelf.Describe(null));
    }

    [Fact]
    public void TheShelfOfAFolderIsReadFromTheProjectsGitFolder()
    {
        var project = Path.Combine(_scratch.Path, "project");
        _scratch.Place(Path.Combine("project", ".git", "msagent", "mcs", "0123456789abcdef", "index.json"), """{"folder":"Agent A","copies":[]}""");
        _scratch.Place(Path.Combine("project", ".git", "msagent", "mcs", "fedcba9876543210", "index.json"), """{"folder":"Agent B","copies":[]}""");

        Assert.Equal("fedcba9876543210", GitCli.ShelfOf(project, "Agent B")!.Key);
        Assert.Null(GitCli.ShelfOf(project, "Agent C"));
        Assert.Null(GitCli.ShelfOf(_scratch.Folder("bare"), "Agent A"));
    }

    private static McsShelf Shelf(string key = "0123456789abcdef", string? folder = null, string? identity = null, string? live = null, McsShelfCopy[]? copies = null, string[]? stored = null) => new(key, Path.Combine("shelves", key), folder, identity, live, copies ?? [], stored ?? []);
}
