using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandLevelFeatureMetadataTests
{
    [CommandGroup(
        "featurefixture",
        Description = "Feature metadata fixture.",
        NoCommandUsage = "Choose always, leaf, sibling, combined, or nested.")]
    public sealed class FeatureFixtureGroup;

    [Command("always", Description = "Always available.", ParentGroup = typeof(FeatureFixtureGroup))]
    public sealed class AlwaysCommand : FixtureCommand;

    [Command("leaf", Description = "Independently controlled leaf.", ParentGroup = typeof(FeatureFixtureGroup))]
    [RequiredFeatureFlag(FeatureName.IdentityFeatures)]
    public sealed class FlaggedLeafCommand : FixtureCommand;

    [Command("sibling", Description = "Separately controlled sibling.", ParentGroup = typeof(FeatureFixtureGroup))]
    [RequiredFeatureFlag(FeatureName.AzureBotServiceFeatures)]
    public sealed class IndependentlyFlaggedCommand : FixtureCommand;

    [Command("combined", Description = "Requires two independent features.", ParentGroup = typeof(FeatureFixtureGroup))]
    [RequiredFeatureFlag(FeatureName.IdentityFeatures)]
    [RequiredFeatureFlag(FeatureName.AzureBotServiceFeatures)]
    public sealed class CombinedFlagsCommand : FixtureCommand;

    [CommandGroup("nested", Description = "Controlled ancestor.", ParentGroup = typeof(FeatureFixtureGroup))]
    [RequiredFeatureFlag(FeatureName.AmsFeatures)]
    public sealed class FlaggedNestedGroup;

    [Command("child", Description = "Leaf under a controlled ancestor.", ParentGroup = typeof(FlaggedNestedGroup))]
    [RequiredFeatureFlag(FeatureName.IdentityFeatures)]
    public sealed class FlaggedNestedLeafCommand : FixtureCommand;

    public abstract class FixtureCommand : ICommandImplementation
    {
        public IReadOnlyList<CommandSymbol> Symbols =>
        [
            OptionCatalog.Value("--name", "Probe value."),
        ];

        public IReadOnlyList<Type> ResultTypes => [typeof(string)];

        public Task<object> ExecuteAsync(
            ICommandInput input,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(input.GetString("--name") ?? "invoked");
    }

    [Fact]
    public void LeafFlags_AreIndependentWithinAnEnabledGroup()
    {
        var identity = Fixture(new TestFeatureFlags(FeatureName.IdentityFeatures));
        var abs = Fixture(new TestFeatureFlags(FeatureName.AzureBotServiceFeatures));

        Assert.Equal(["always", "leaf"], identity.Commands.Select(command => command.Name).Order());
        Assert.Equal(["always", "sibling"], abs.Commands.Select(command => command.Name).Order());
    }

    [Fact]
    public void DisabledAncestor_CannotBeBypassedByAnEnabledLeaf()
    {
        var leafOnly = Fixture(new TestFeatureFlags(FeatureName.IdentityFeatures));
        var ancestorOnly = Fixture(new TestFeatureFlags(FeatureName.AmsFeatures));
        var both = Fixture(new TestFeatureFlags(FeatureName.AmsFeatures, FeatureName.IdentityFeatures));

        Assert.Empty(leafOnly.Groups);
        Assert.Single(ancestorOnly.Groups);
        Assert.Empty(ancestorOnly.Groups[0].Commands);
        Assert.Equal("child", Assert.Single(Assert.Single(both.Groups).Commands).Name);
    }

    [Fact]
    public void MultipleRequiredFlags_AreAnded()
    {
        var identityOnly = Fixture(new TestFeatureFlags(FeatureName.IdentityFeatures));
        var absOnly = Fixture(new TestFeatureFlags(FeatureName.AzureBotServiceFeatures));
        var both = Fixture(new TestFeatureFlags(
            FeatureName.IdentityFeatures,
            FeatureName.AzureBotServiceFeatures));

        Assert.DoesNotContain(identityOnly.Commands, command => command.Name == "combined");
        Assert.DoesNotContain(absOnly.Commands, command => command.Name == "combined");
        Assert.Contains(both.Commands, command => command.Name == "combined");
    }

    [Fact]
    public async Task FilteredChildren_AreNotAdvertisedByCustomNoCommandUsage()
    {
        var result = await InvokeAsync(new TestFeatureFlags(), "featurefixture");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Valid commands: always.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("leaf", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("combined", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("nested", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomNoCommandUsage_IsPreservedWhenNoDirectChildrenAreFiltered()
    {
        var result = await InvokeAsync(
            TestFeatureFlags.AllProductSurfaces,
            "featurefixture");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(
            "Choose always, leaf, sibling, combined, or nested.",
            result.Stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafFlag_ControlsHelpParsingDispatchAndOptionAliases()
    {
        var enabled = await InvokeAsync(
            new TestFeatureFlags(FeatureName.IdentityFeatures),
            "featurefixture",
            "--help");
        var longOption = await InvokeAsync(
            new TestFeatureFlags(FeatureName.IdentityFeatures),
            "featurefixture",
            "leaf",
            "--name",
            "long");
        var alias = await InvokeAsync(
            new TestFeatureFlags(FeatureName.IdentityFeatures),
            "featurefixture",
            "leaf",
            "-n",
            "alias");
        var disabledHelp = await InvokeAsync(
            new TestFeatureFlags(),
            "featurefixture",
            "--help");
        var disabledDirect = await InvokeAsync(
            new TestFeatureFlags(),
            "featurefixture",
            "leaf",
            "-n",
            "blocked");

        Assert.Equal(ExitCodes.Success, enabled.ExitCode);
        Assert.Contains("leaf", enabled.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling", enabled.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, longOption.ExitCode);
        Assert.Contains("long", longOption.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, alias.ExitCode);
        Assert.Contains("alias", alias.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("leaf", disabledHelp.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, disabledDirect.ExitCode);
        Assert.Contains("leaf", disabledDirect.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityConstructor_ExecutesWithoutAnAttachedDebugOption()
    {
        var result = await InvokeAsync(
            TestFeatureFlags.AllProductSurfaces,
            useCompatibilityConstructor: true,
            "featurefixture",
            "always");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("invoked", result.Stdout, StringComparison.Ordinal);
    }

    private static CommandGroupMetadata Fixture(IFeatureFlags flags) =>
        CommandReflector
            .Discover(typeof(CommandLevelFeatureMetadataTests).Assembly, flags)
            .Single(group => group.DeclaringType == typeof(FeatureFixtureGroup));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IFeatureFlags flags,
        params string[] args)
        => await InvokeAsync(flags, useCompatibilityConstructor: false, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IFeatureFlags flags,
        bool useCompatibilityConstructor,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var renderer = new CliOutputRenderer(stdout, stderr, color: false);
        var json = new Option<bool>("--json");
        var debug = new Option<bool>("--debug");
        var nonInteractive = new Option<bool>("--non-interactive");
        var builder = useCompatibilityConstructor
            ? new CommandTreeBuilder(
                renderer,
                new EmptyServiceProvider(),
                flags,
                json,
                nonInteractive)
            : new CommandTreeBuilder(
                renderer,
                new EmptyServiceProvider(),
                flags,
                json,
                debug,
                nonInteractive);
        var root = new RootCommand();
        root.Add(json);
        if (!useCompatibilityConstructor)
        {
            root.Add(debug);
        }
        root.Add(nonInteractive);
        root.Add(builder.Build(Fixture(flags)));

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            var message = string.Join(" ", parseResult.Errors.Select(error => error.Message));
            return (renderer.RenderUsageError(message, json: false), stdout.ToString(), stderr.ToString());
        }

        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = stdout,
            Error = stderr,
        });

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
