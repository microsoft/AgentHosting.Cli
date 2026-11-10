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

    [CommandGroup("telemetryfixture", Description = "Telemetry metadata fixture.")]
    public sealed class TelemetryFixtureGroup;

    [Command("quiet", Description = "Runs without telemetry.", ParentGroup = typeof(TelemetryFixtureGroup), SuppressTelemetry = true)]
    public sealed class QuietCommand : FixtureCommand;

    [Command("inner", Description = "Nested under the quiet command.", ParentCommand = typeof(QuietCommand))]
    public sealed class QuietInnerCommand : FixtureCommand;

    [Command("loud", Description = "Records telemetry.", ParentGroup = typeof(TelemetryFixtureGroup))]
    public sealed class LoudCommand : FixtureCommand;

    [CommandGroup("describedfixture", Description = "Described metadata fixture.", NoCommandUsage = "Choose probe.")]
    public sealed class DescribedFixtureGroup;

    [Command("probe", Description = "Base command text.", ParentGroup = typeof(DescribedFixtureGroup))]
    public sealed class FeatureDescribedCommand : ICommandImplementation
    {
        public IReadOnlyList<CommandSymbol> Symbols =>
        [
            OptionCatalog.Value("--message", "Base option text.") with { EnabledDescription = new([FeatureName.IdentityFeatures], "Identity option text.") },
            OptionCatalog.Value("--name", "Probe value.", required: true) with { OptionalWithFeatures = [FeatureName.IdentityFeatures] },
        ];

        public FeatureDescription? EnabledDescription => new([FeatureName.IdentityFeatures], "Identity command text.");

        public IReadOnlyList<Type> ResultTypes => [typeof(string)];

        public Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) => Task.FromResult<object>($"name={input.GetString("--name")}");
    }

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

    [Theory]
    [InlineData(new FeatureName[0], "Base option text.", "Base command text.")]
    [InlineData(new[] { FeatureName.IdentityFeatures }, "Identity option text.", "Identity command text.")]
    public async Task FeatureDescriptions_OfOptionsAndCommands_ReplaceTheBaseTextWhileTheirFeaturesAreOn(FeatureName[] enabled, string optionText, string commandText)
    {
        var command = await InvokeDescribedFixtureAsync(new TestFeatureFlags(enabled), "describedfixture", "probe", "--help");
        var group = await InvokeDescribedFixtureAsync(new TestFeatureFlags(enabled), "describedfixture", "--help");

        Assert.Contains(optionText, command.Stdout, StringComparison.Ordinal);
        Assert.Contains(commandText, command.Stdout, StringComparison.Ordinal);
        Assert.Contains(commandText, group.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(enabled.Length == 0 ? "Identity option text." : "Base option text.", command.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new FeatureName[0], ExitCodes.Usage)]
    [InlineData(new[] { FeatureName.IdentityFeatures }, ExitCodes.Success)]
    public async Task OptionalWithFeatures_MakesARequiredOptionOptional_WhileItsFeaturesAreOn(FeatureName[] enabled, int exitCode)
    {
        var withoutName = await InvokeDescribedFixtureAsync(new TestFeatureFlags(enabled), "describedfixture", "probe");
        var withName = await InvokeDescribedFixtureAsync(new TestFeatureFlags(enabled), "describedfixture", "probe", "--name", "given");

        Assert.Equal(exitCode, withoutName.ExitCode);
        Assert.Equal(ExitCodes.Success, withName.ExitCode);
        Assert.Contains("name=given", withName.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressTelemetry_OnACommand_CoversItsNestedCommandsButNotItsGroupOrSiblings()
    {
        var flags = new TestFeatureFlags();
        var builder = Builder(flags);
        var group = builder.Build(Fixture(flags, typeof(TelemetryFixtureGroup)));
        var quiet = group.Subcommands.Single(command => command.Name == "quiet");

        Assert.Equal(
            (false, true, true, false),
            (builder.SuppressesTelemetry(group), builder.SuppressesTelemetry(quiet), builder.SuppressesTelemetry(quiet.Subcommands.Single()), builder.SuppressesTelemetry(group.Subcommands.Single(command => command.Name == "loud"))));
    }

    private static CommandGroupMetadata Fixture(IFeatureFlags flags) => Fixture(flags, typeof(FeatureFixtureGroup));

    private static CommandTreeBuilder Builder(IFeatureFlags flags) => new(new CliOutputRenderer(new StringWriter(), new StringWriter(), color: false), new EmptyServiceProvider(), flags, new Option<bool>("--json"), new Option<bool>("--debug"), new Option<bool>("--non-interactive"));

    private static CommandGroupMetadata Fixture(IFeatureFlags flags, Type group) =>
        CommandReflector
            .Discover(typeof(CommandLevelFeatureMetadataTests).Assembly, flags)
            .Single(candidate => candidate.DeclaringType == group);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IFeatureFlags flags,
        params string[] args)
        => await InvokeAsync(flags, typeof(FeatureFixtureGroup), useCompatibilityConstructor: false, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IFeatureFlags flags,
        bool useCompatibilityConstructor,
        params string[] args)
        => await InvokeAsync(flags, typeof(FeatureFixtureGroup), useCompatibilityConstructor, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeDescribedFixtureAsync(
        IFeatureFlags flags,
        params string[] args)
        => await InvokeAsync(flags, typeof(DescribedFixtureGroup), useCompatibilityConstructor: false, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        IFeatureFlags flags,
        Type group,
        bool useCompatibilityConstructor,
        string[] args)
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
        root.Add(builder.Build(Fixture(flags, group)));

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
