using System.CommandLine;
using System.CommandLine.Help;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Features;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// The scoped System.CommandLine command host. It discovers the command tree from attributes,
/// materialises it into System.CommandLine, parses the global flags, dispatches each verb to its
/// registered handler, and maps typed failures to the frozen exit taxonomy via
/// <see cref="CliOutputRenderer"/>.
///
/// The host holds only the operation facade, so command-host tests drive the full parse → dispatch →
/// render → exit pipeline with a fake facade and string writers — no MSAL, broker, browser, or Azure
/// transport is reachable from here.
/// </summary>
public sealed class AgentHostCli
{
    internal static bool IsRingIndependent(IReadOnlyList<string> args)
    {
        var flags = FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly);
        var json = CreateGlobalBooleanOption("--json", "");
        var nonInteractive = CreateGlobalBooleanOption("--non-interactive", "");
        var builder = new CommandTreeBuilder(new CliOutputRenderer(TextWriter.Null, TextWriter.Null, false),
            ParseOnlyServices.Instance, flags, json, nonInteractive);
        var root = new RootCommand();
        root.Add(json);
        root.Add(nonInteractive);
        root.Add(CreateGlobalBooleanOption("--debug", ""));
        root.Add(CreateGlobalBooleanOption("--no-color", ""));
        foreach (var option in root.Options.OfType<VersionOption>())
            option.Aliases.Add(OptionCatalog.Alias("--version"));
        foreach (var group in CommandReflector.Discover(typeof(EnvGroup).Assembly, flags))
            root.Add(builder.Build(group));
        var parsed = root.Parse(ExpandEmptyAssignments(root, [.. args]));
        var command = parsed.CommandResult.Command;
        if (parsed.Errors.Count != 0) return false;
        return command == root && parsed.Errors.Count == 0
            || command.Parents.OfType<Command>().Any(parent => parent.Name == "auth")
            || command.Name == "auth"
            || root.Options.OfType<HelpOption>().Any(option => ReferenceEquals(parsed.Action, option.Action))
            || root.Options.OfType<VersionOption>().Any(option => ReferenceEquals(parsed.Action, option.Action));
    }
    internal static bool ResolveInteractionAllowed(string[] args, IFeatureFlags? featureFlags = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var flags = featureFlags ?? FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly);
        var json = CreateGlobalBooleanOption("--json", "");
        var debug = CreateGlobalBooleanOption("--debug", "");
        var noColor = CreateGlobalBooleanOption("--no-color", "");
        var nonInteractive = CreateGlobalBooleanOption("--non-interactive", "");
        // Use command metadata too: an option value must not become a global flag.
        var builder = new CommandTreeBuilder(new CliOutputRenderer(TextWriter.Null, TextWriter.Null, false),
            ParseOnlyServices.Instance, flags, json, debug, nonInteractive);
        var root = new RootCommand();
        root.Add(json);
        root.Add(debug);
        root.Add(noColor);
        root.Add(nonInteractive);
        foreach (var option in root.Options.OfType<VersionOption>())
            option.Aliases.Add(OptionCatalog.Alias("--version"));
        foreach (var group in CommandReflector.Discover(typeof(EnvGroup).Assembly, flags))
            root.Add(builder.Build(group));

        var parsed = root.Parse(ExpandEmptyAssignments(root, args));
        return parsed.Errors.Count == 0 && !parsed.GetValue(nonInteractive);
    }

    private static Option<bool> CreateGlobalBooleanOption(string name, string description) =>
        new(name, OptionCatalog.Alias(name)) { Description = description, Recursive = true };

    private sealed class ParseOnlyServices : IServiceProvider
    {
        internal static ParseOnlyServices Instance { get; } = new();
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("Bootstrap parsing must not activate command services.");
    }

    private static Regex UsageExecutable(string rootName) =>
        new($@"(?m)(^Usage:\r?\n\s*){Regex.Escape(rootName)}(?=\s|$)", RegexOptions.CultureInvariant);

    private readonly CompositeHandlerProvider _services;
    private readonly IFeatureFlags _featureFlags;
    private readonly CommandTelemetrySessionFactory _telemetryFactory;
    private readonly CommandTelemetrySessionAccessor _telemetryAccessor;
    private readonly SystemCommandLineInputFactory _inputFactory;
    private readonly ProgressSpinner? _progressSpinner;

    /// <summary>Creates the host.</summary>
    /// <param name="operations">The operations facade the commands dispatch to.</param>
    /// <param name="services">Resolves command handlers; defaults to a provider backed by <paramref name="operations"/>.</param>
    /// <param name="featureFlags">Controls command-tree inclusion; defaults to shipped feature controls.</param>
    public AgentHostCli(
        IAgentHostingOperations operations,
        IServiceProvider? services = null,
        IFeatureFlags? featureFlags = null)
        : this(
            operations,
            telemetryFactory: null,
            services: services,
            featureFlags: featureFlags,
            telemetryAccessor: null,
            inputFactory: null)
    {
    }

    internal AgentHostCli(
        IAgentHostingOperations operations,
        CommandTelemetrySessionFactory? telemetryFactory,
        IServiceProvider? services = null,
        IFeatureFlags? featureFlags = null,
        CommandTelemetrySessionAccessor? telemetryAccessor = null,
        SystemCommandLineInputFactory? inputFactory = null,
        ProgressSpinner? progressSpinner = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        _featureFlags = featureFlags ?? FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly);
        _services = new CompositeHandlerProvider(
            new FeatureFlagServiceProvider(_featureFlags),
            services ?? new FacadeHandlerProvider(operations));
        _telemetryFactory = telemetryFactory ?? CommandTelemetrySessionFactory.Disabled;
        _telemetryAccessor = telemetryAccessor ?? new CommandTelemetrySessionAccessor();
        _inputFactory =
            inputFactory ?? CommandTreeBuilder.CreateSystemCommandLineInput;
        _progressSpinner = progressSpinner;
    }

    /// <summary>
    /// Parses <paramref name="args"/>, dispatches the resolved verb, and returns the process exit code.
    /// Output is written to <paramref name="stdout"/>/<paramref name="stderr"/> and ANSI color is
    /// emitted only when <paramref name="color"/> is <see langword="true"/>.
    /// </summary>
    public async Task<int> InvokeAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        bool color,
        CancellationToken cancellationToken = default)
    {
        // No command group given (empty, or only global flags) behaves like `--help` and exits 0,
        // mirroring the shipped TypeScript CLI. A positional token is always parsed so an unknown
        // group/verp still surfaces as a usage error.
        var hasPositional = args.Any(a => !a.StartsWith('-'));
        if (!hasPositional && !GlobalOptions.AsksForHelpOrVersion(args))
        {
            args = [.. args, "--help"];
        }

        var renderer = new CliOutputRenderer(stdout, stderr, color);

        var jsonOption = CreateGlobalBooleanOption("--json", "Emit machine-readable JSON output.");
        var debugOption = CreateGlobalBooleanOption("--debug", "Display debugging information, such as redacted API responses, on stderr.");
        var noColorOption = CreateGlobalBooleanOption("--no-color", "Disable colored output.");
        var nonInteractiveOption = CreateGlobalBooleanOption("--non-interactive", "Do not open a browser or broker sign-in, fail instead.");
        var treeBuilder = new CommandTreeBuilder(
            renderer,
            _services,
            _featureFlags,
            jsonOption,
            debugOption,
            nonInteractiveOption,
            _telemetryAccessor,
            _inputFactory,
            _progressSpinner);
        var root = new RootCommand(
            $"{AgentHostingCliInfo.ProductName}{Environment.NewLine}" +
            $"Version: {ThisAssembly.AssemblyInformationalVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}){Environment.NewLine}" +
            "Create, Edit, Deploy & Test Copilot Studio Agents locally");
        root.Add(jsonOption);
        root.Add(debugOption);
        root.Add(noColorOption);
        root.Add(nonInteractiveOption);

        foreach (var option in root.Options.OfType<VersionOption>())
        {
            option.Aliases.Add(OptionCatalog.Alias("--version"));
        }

        foreach (var group in CommandReflector.Discover(typeof(EnvGroup).Assembly, _featureFlags))
        {
            root.Add(treeBuilder.Build(group));
        }

        // Render a group's help split into Subgroups/Commands wherever the group actually holds a
        // subgroup; every other group keeps the System.CommandLine default. A subgroup is a true
        // [CommandGroup] (tracked by the tree builder), not a leaf verb that merely nests a subcommand
        // such as `deployment update connection`. The default recursive HelpOption's action is swapped
        // in place so `--help` behavior is otherwise unchanged.
        var isSubgroup = treeBuilder.Groups.ToHashSet();
        var splitHelpSubtree = SubtreesHoldingASubgroup(root, isSubgroup);
        SplitSubgroupHelp(root, splitHelpSubtree, isSubgroup, treeBuilder.UsageLines);

        AddDeploymentChannelHelp(root);

        var parseResult = root.Parse(ExpandEmptyAssignments(root, args));
        HelpOption[] helpOptions = [.. root.Options.OfType<HelpOption>()];
        VersionOption[] versionOptions = [.. root.Options.OfType<VersionOption>()];
        bool invokesHelp = helpOptions.Any(option =>
            ReferenceEquals(parseResult.Action, option.Action));
        bool invokesVersion = versionOptions.Any(option =>
            ReferenceEquals(parseResult.Action, option.Action));
        var json = parseResult.GetValue(jsonOption);
        await using CommandTelemetrySession telemetry =
            await _telemetryFactory.CreateAsync(
                treeBuilder.SuppressesTelemetry(parseResult.CommandResult.Command),
                cancellationToken);
        using IDisposable telemetryScope = _telemetryAccessor.Push(telemetry);
        using IDisposable? observationScope =
            telemetry.EnterObservationScope();

        var invocationConfiguration = new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = stdout,
            Error = stderr,
        };

        // The selected parser action, rather than raw token spelling, distinguishes actual help
        // from a value such as `auth select --name --help`. Unmatched tokens still make an invalid
        // command path a usage error even when --help is present.
        if (invokesHelp && parseResult.UnmatchedTokens.Count == 0)
        {
            var helpOutput = new StringWriter();
            invocationConfiguration.Output = helpOutput;
            var exitCode = await parseResult.InvokeAsync(invocationConfiguration, cancellationToken);
            await stdout.WriteAsync(
                UsageExecutable(root.Name).Replace(helpOutput.ToString(), "$1msagent", count: 1));
            telemetry.RecordHost(CommandTelemetryHostCategory.Help);
            return exitCode;
        }

        if (parseResult.Errors.Count > 0)
        {
            var message = treeBuilder.SafeParserDiagnostic(parseResult, args);
            int exitCode = renderer.RenderUsageError(message, json);
            telemetry.RecordHost(CommandTelemetryHostCategory.ParserRejection);
            return exitCode;
        }

        int result;
        try
        {
            result = await parseResult.InvokeAsync(invocationConfiguration, cancellationToken);
        }
        catch (OperationCanceledException failure) when (cancellationToken.IsCancellationRequested
            && CliOutputRenderer.HasAuthPartialState(failure))
        {
            // The handler already rendered the recognized durable state before propagating cancellation.
            return 130;
        }
        if (invokesVersion)
        {
            telemetry.RecordHost(CommandTelemetryHostCategory.Version);
        }

        return result;
    }

    private static string[] ExpandEmptyAssignments(Command root, string[] args)
    {
        var valueOptions = ValueOptionNames(root).ToHashSet(StringComparer.Ordinal);
        return args.SelectMany(token => token.Length > 1 && token[^1] is '=' or ':'
            && valueOptions.Contains(token[..^1]) ? new[] { token[..^1], "" } : new[] { token }).ToArray();
    }

    private static IEnumerable<string> ValueOptionNames(Command command) =>
        command.Options.Where(option => option.ValueType == typeof(string))
            .SelectMany(option => new[] { option.Name }.Concat(option.Aliases))
            .Concat(command.Subcommands.SelectMany(ValueOptionNames));

    private sealed class FeatureFlagServiceProvider(IFeatureFlags featureFlags) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IFeatureFlags) ? featureFlags : null;
    }


    /// <summary>
    /// Swaps the recursive help action for one that splits a subtree's help into Subgroups and
    /// Commands, leaving every other group on the System.CommandLine default.
    /// </summary>
    private static void SplitSubgroupHelp(
        RootCommand root,
        HashSet<Command> subtree,
        HashSet<Command> subgroups,
        Func<Command, IReadOnlyList<string>> usageLines)
    {
        foreach (var option in root.Options)
        {
            if (option is System.CommandLine.Help.HelpOption helpOption
                && helpOption.Action is System.CommandLine.Invocation.SynchronousCommandLineAction defaultHelp)
            {
                helpOption.Action = new SubgroupHelpAction(defaultHelp, subtree.Contains, subgroups.Contains, usageLines);
            }
        }
    }

    /// <summary>Wraps create/update help with catalog-generated deployment channel parameter metadata.</summary>
    private static void AddDeploymentChannelHelp(RootCommand root)
    {
        var deployment = root.Subcommands.FirstOrDefault(command => command.Name == "deployment");
        var channel = deployment?.Subcommands.FirstOrDefault(command => command.Name == "channel");
        if (channel is null)
        {
            return;
        }

        var mutationCommands = channel.Subcommands
            .Where(command => command.Name is "create" or "update")
            .ToHashSet();
        if (mutationCommands.Count == 0)
        {
            return;
        }

        foreach (var option in root.Options)
        {
            if (option is HelpOption helpOption &&
                helpOption.Action is System.CommandLine.Invocation.SynchronousCommandLineAction currentHelp)
            {
                helpOption.Action = new DeploymentChannelHelpAction(currentHelp, mutationCommands);
            }
        }
    }

    // `identity` is excluded from the subgroup split: its only child, `create`, is presented as a
    // command rather than a subgroup, so `identity` keeps the System.CommandLine default help.
    private static readonly HashSet<string> GroupsExcludedFromSubgroupSplit = new(StringComparer.Ordinal) { "identity" };

    private static HashSet<Command> SubtreesHoldingASubgroup(RootCommand root, HashSet<Command> subgroups)
    {
        var subtree = new HashSet<Command>();

        foreach (var group in root.Subcommands.Where(command =>
            !GroupsExcludedFromSubgroupSplit.Contains(command.Name) && HoldsASubgroup(command, subgroups)))
        {
            subtree.UnionWith(CollectCommands(group));
        }

        return subtree;
    }

    /// <summary>Whether <paramref name="command"/> directly holds a visible true subgroup child.</summary>
    private static bool HoldsASubgroup(Command command, HashSet<Command> subgroups) =>
        command.Subcommands.Any(child => !child.Hidden && subgroups.Contains(child));

    /// <summary>Collects <paramref name="root"/> and every descendant subcommand into a set.</summary>
    private static HashSet<Command> CollectCommands(Command root)
    {
        var set = new HashSet<Command>();
        var stack = new Stack<Command>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!set.Add(current))
            {
                continue;
            }

            foreach (var child in current.Subcommands)
            {
                stack.Push(child);
            }
        }

        return set;
    }
}
