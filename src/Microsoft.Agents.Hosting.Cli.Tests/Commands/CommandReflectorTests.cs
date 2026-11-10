using Microsoft.Agents.Hosting.Cli.Commands.Commands.Env;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Tests.Commands;

public sealed class CommandReflectorTests
{
    private static IReadOnlyList<CommandGroupMetadata> Discovered => CommandReflector.Discover(typeof(EnvGroup).Assembly);

    [Fact]
    public void TheEnvGroupIsDiscovered()
    {
        Assert.Contains(Discovered, group => group.Name == "env");
    }

    [Fact]
    public void TheEnvGroupOwnsItsTwoCommands()
    {
        Assert.Equal(["list", "select"], Discovered.Single(group => group.Name == "env").ChildNames);
    }

    [Fact]
    public void EveryDiscoveredCommandDeclaresItsResultTypes()
    {
        foreach (var command in Discovered.SelectMany(group => group.Commands))
        {
            Assert.NotEmpty(command.Implementation.ResultTypes);
        }
    }

    [Fact]
    public void EnvSelectDeclaresOnePositionalArgument()
    {
        var select = Discovered.Single(group => group.Name == "env").Commands.Single(command => command.Name == "select");

        Assert.IsType<CommandArgument>(Assert.Single(select.Implementation.Symbols));
    }

    [Fact]
    public void EnvListDeclaresNoSymbols()
    {
        Assert.Empty(Discovered.Single(group => group.Name == "env").Commands.Single(command => command.Name == "list").Implementation.Symbols);
    }

    [Fact]
    public void DiscoveryIsCachedPerAssembly()
    {
        Assert.Same(Discovered, CommandReflector.Discover(typeof(EnvGroup).Assembly));
    }

    [Fact]
    public void OnlyTheGitCredentialHelperIsHidden_AndStaysHiddenWhenFeaturesFilterTheTree()
    {
        string[] hidden = ["git-credential"];

        Assert.Equal(hidden, Discovered.SelectMany(CommandTree.AllCommands).Where(command => command.Hidden).Select(command => command.Name));
        Assert.Equal(hidden, CommandReflector.Discover(typeof(EnvGroup).Assembly, TestFeatureFlags.Everything).SelectMany(CommandTree.AllCommands).Where(command => command.Hidden).Select(command => command.Name));
        Assert.DoesNotContain(CommandReflector.Discover(typeof(EnvGroup).Assembly, TestFeatureFlags.AllProductSurfaces).SelectMany(CommandTree.AllCommands), command => command.Name is "git-credential" or "setup-git");
    }

    [Fact]
    public void EveryCatalogAliasIsUnique()
    {
        Assert.Null(OptionCatalog.DeclaredAliases.GroupBy(entry => entry.Value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1));
    }

    [Fact]
    public void AnOptionOutsideTheCatalogFailsLoudly()
    {
        Assert.Throws<CommandConfigurationException>(() => OptionCatalog.Value("--not-in-the-catalog", "x"));
    }

    [Fact]
    public void TheCatalogPreservesEveryAliasTheCurrentSurfaceUses()
    {
        foreach (var (option, alias) in CommandSurfaceSnapshotTests.CurrentAliases)
        {
            Assert.True(OptionCatalog.DeclaredAliases.TryGetValue(option, out var declared), $"{option} is missing from OptionCatalog.");
            Assert.Equal(alias, declared);
        }
    }
}
