using System.CommandLine;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal delegate SystemCommandLineInput SystemCommandLineInputFactory(
    ParseResult parseResult,
    IReadOnlyDictionary<string, Symbol> symbols,
    bool json,
    bool nonInteractive);

/// <summary>Materialises discovered command metadata into a System.CommandLine tree.</summary>
public sealed class CommandTreeBuilder
{
    private readonly CliOutputRenderer _renderer;
    private readonly IServiceProvider _services;
    private readonly IFeatureFlags _featureFlags;
    private readonly Option<bool> _json;
    private readonly Option<bool>? _debug;
    private readonly Option<bool> _nonInteractive;
    private readonly CommandTelemetrySessionAccessor _telemetryAccessor;
    private readonly SystemCommandLineInputFactory _inputFactory;
    private readonly ProgressSpinner? _progressSpinner;
    private readonly Dictionary<string, Symbol> _shared = new(StringComparer.Ordinal);
    private readonly HashSet<Command> _telemetrySuppressedCommands = [];
    private readonly HashSet<Command> _groups = [];
    private readonly Dictionary<Command, IReadOnlyList<string>> _usageLines = [];
    private readonly HashSet<Command> _sensitiveCommands = [];
    private readonly HashSet<string> _sensitiveOptions = new(StringComparer.Ordinal);

    /// <summary>Creates the builder.</summary>
    /// <param name="renderer">Renders results and failures.</param>
    /// <param name="services">Resolves handlers at action time.</param>
    /// <param name="featureFlags">Controls which command symbols are materialised.</param>
    /// <param name="json">The recursive JSON flag.</param>
    /// <param name="nonInteractive">The recursive non-interactive flag.</param>
    /// <remarks>This compatibility overload does not enable debug output.</remarks>
    public CommandTreeBuilder(
        CliOutputRenderer renderer,
        IServiceProvider services,
        IFeatureFlags featureFlags,
        Option<bool> json,
        Option<bool> nonInteractive)
        : this(
            renderer,
            services,
            featureFlags,
            json,
            debug: null,
            nonInteractive,
            new CommandTelemetrySessionAccessor(),
            CreateSystemCommandLineInput)
    {
    }

    /// <summary>Creates the builder with an explicit recursive debug-output flag.</summary>
    public CommandTreeBuilder(
        CliOutputRenderer renderer,
        IServiceProvider services,
        IFeatureFlags featureFlags,
        Option<bool> json,
        Option<bool> debug,
        Option<bool> nonInteractive)
        : this(
            renderer,
            services,
            featureFlags,
            json,
            debug,
            nonInteractive,
            new CommandTelemetrySessionAccessor(),
            CreateSystemCommandLineInput)
    {
    }

    internal CommandTreeBuilder(
        CliOutputRenderer renderer,
        IServiceProvider services,
        IFeatureFlags featureFlags,
        Option<bool> json,
        Option<bool>? debug,
        Option<bool> nonInteractive,
        CommandTelemetrySessionAccessor telemetryAccessor,
        SystemCommandLineInputFactory inputFactory,
        ProgressSpinner? progressSpinner = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _featureFlags = featureFlags ?? throw new ArgumentNullException(nameof(featureFlags));
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _debug = debug;
        _nonInteractive = nonInteractive ?? throw new ArgumentNullException(nameof(nonInteractive));
        _telemetryAccessor = telemetryAccessor
            ?? throw new ArgumentNullException(nameof(telemetryAccessor));
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _progressSpinner = progressSpinner;
    }

    /// <summary>The commands materialised from <see cref="CommandGroupMetadata"/> definitions.</summary>
    public IReadOnlyCollection<Command> Groups => _groups;

    /// <summary>Builds one root group and everything under it.</summary>
    /// <param name="group">The discovered group.</param>
    public Command Build(CommandGroupMetadata group) =>
        BuildGroup(group, parentPath: null, inheritedSuppression: false);

    internal bool SuppressesTelemetry(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _telemetrySuppressedCommands.Contains(command);
    }

    internal IReadOnlyList<string> UsageLines(Command command) =>
        command is not null && _usageLines.TryGetValue(command, out var lines) ? lines : [];

    internal string SafeParserDiagnostic(ParseResult result, IReadOnlyList<string> args)
    {
        // A malformed or dash-prefixed secret can become an unmatched token or even an option.
        // Do not use parser message bodies for a credential command or credential-bearing argv.
        bool sensitive = _sensitiveCommands.Contains(result.CommandResult.Command)
            || args.Any(token => _sensitiveOptions.Any(option => token.StartsWith(option, StringComparison.OrdinalIgnoreCase)));
        return sensitive
            ? "Invalid authentication arguments. Check credential options and mutually exclusive modes with `msagent auth create --help`."
            : string.Join(" ", result.Errors.Select(error => error.Message)) + " Run `msagent --help` for usage.";
    }

    private Command BuildGroup(
        CommandGroupMetadata group,
        string? parentPath,
        bool inheritedSuppression)
    {
        ArgumentNullException.ThrowIfNull(group);

        string path = parentPath is null ? group.Name : $"{parentPath} {group.Name}";
        bool suppressTelemetry = inheritedSuppression || group.SuppressTelemetry;
        var command = new Command(group.Name, group.Description);
        _groups.Add(command);
        if (suppressTelemetry)
        {
            _telemetrySuppressedCommands.Add(command);
        }
        var inherited = new Dictionary<string, Symbol>(StringComparer.Ordinal);

        foreach (var declared in OrderForHelp(Visible(group.Symbols)))
        {
            var symbol = Materialise(declared);
            inherited[declared.Name] = symbol;
            Attach(command, symbol);
        }

        foreach (var child in group.Groups.Select(child =>
                     BuildGroup(child, path, suppressTelemetry)))
        {
            command.Add(child);
        }

        foreach (var child in group.Commands.Select(child =>
                     Build(
                         child,
                         inherited,
                         Recursive(Visible(group.Symbols)),
                         path,
                         suppressTelemetry)))
        {
            command.Add(child);
        }

        var usage = group.NoCommandUsage ?? BuildUsageError(group);
        command.SetAction((parseResult, _) =>
        {
            int exitCode =
                _renderer.RenderUsageError(usage, parseResult.GetValue(_json));
            _telemetryAccessor.Current.RecordHost(
                CommandTelemetryHostCategory.GroupUsage);
            return Task.FromResult(exitCode);
        });

        return command;
    }

    private static string BuildUsageError(CommandGroupMetadata group)
    {
        var clauses = new List<string>();

        if (group.Groups.Count > 0)
        {
            clauses.Add($"Valid subgroups: {string.Join(", ", group.Groups.Select(child => child.Name))}");
        }

        if (group.Commands.Count > 0)
        {
            clauses.Add($"Valid commands: {string.Join(", ", group.Commands.Select(child => child.Name))}");
        }

        var advertised = clauses.Count > 0 ? string.Join(". ", clauses) + ". " : string.Empty;
        return $"No {group.Name} command specified. {advertised}Run `msagent --help` for usage.";
    }

    private static HashSet<string> Recursive(IEnumerable<CommandSymbol> symbols) =>
        symbols.OfType<CommandOption>().Where(option => option.Recursive).Select(option => option.Name).ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<CommandSymbol> Visible(IEnumerable<CommandSymbol> symbols) =>
        [.. symbols.Where(symbol => symbol.RequiredFeatures.All(_featureFlags.IsFeatureEnabled))];

    private Command Build(
        CommandMetadata metadata,
        IReadOnlyDictionary<string, Symbol> inherited,
        HashSet<string> inheritable,
        string parentPath,
        bool suppressTelemetry)
    {
        var command = new Command(metadata.Name, metadata.Description);
        if (suppressTelemetry)
        {
            _telemetrySuppressedCommands.Add(command);
        }
        var symbols = new Dictionary<string, Symbol>(inherited, StringComparer.Ordinal);
        var declaredSymbols = Visible(metadata.Implementation.Symbols);
        foreach (var option in declaredSymbols.OfType<CommandOption>().Where(option => option.IsSensitive))
        {
            _sensitiveCommands.Add(command);
            _sensitiveOptions.Add(option.Name);
            if (option.ShortAlias is { } alias) _sensitiveOptions.Add(alias);
            foreach (var extra in option.ExtraAliases) _sensitiveOptions.Add(extra);
        }
        var declaredNames = declaredSymbols.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var declared in OrderForHelp(declaredSymbols))
        {
            var symbol = Materialise(declared);
            symbols[declared.Name] = symbol;
            Attach(command, symbol);
        }

        foreach (var child in metadata.Commands)
        {
            command.Add(Build(
                child,
                symbols,
                new HashSet<string>(
                    inheritable.Concat(Recursive(declaredSymbols)),
                    StringComparer.Ordinal),
                $"{parentPath} {metadata.Name}",
                suppressTelemetry));
        }

        var parentOnly = inherited.Keys.Where(name => !declaredNames.Contains(name) && !inheritable.Contains(name)).ToArray();
        var path = $"{parentPath} {metadata.Name}";
        RecordUsageLines(command, metadata, declaredSymbols, path);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            metadata,
            symbols,
            parentOnly,
            path,
            parseResult,
            cancellationToken));

        return command;
    }

    private void RecordUsageLines(Command command, CommandMetadata metadata, IReadOnlyList<CommandSymbol> declaredSymbols, string path)
    {
        var declared = declaredSymbols.ToDictionary(symbol => symbol.Name, StringComparer.Ordinal);
        var routes = metadata.Implementation.UsageRoutes
            .Where(route => route.RequiredFeatures.All(_featureFlags.IsFeatureEnabled))
            .ToArray();

        string[] lines;

        if (routes.Length > 0)
        {
            var rendered = routes
                .Select(route => (route.Summary, Line: Line(path, route.Options, declared)))
                .DistinctBy(entry => entry.Line, StringComparer.Ordinal)
                .ToArray();

            lines = rendered.Length > 1 && rendered.Any(entry => !string.IsNullOrWhiteSpace(entry.Summary))
                ? [.. rendered.SelectMany(entry => string.IsNullOrWhiteSpace(entry.Summary) ? [entry.Line] : new[] { entry.Summary, "  " + entry.Line })]
                : [.. rendered.Select(entry => entry.Line)];
        }
        else
        {
            var required = declaredSymbols.Where(symbol => symbol.IsRequired).Select(symbol => symbol.Name).ToArray();
            lines = required.Length > 0 ? [Line(path, required, declared)] : [];
        }

        if (lines.Length > 0)
        {
            _usageLines[command] = lines;
        }
    }

    private static string Line(string path, IReadOnlyList<string> tokens, IReadOnlyDictionary<string, CommandSymbol> declared)
    {
        var rendered = tokens
            .Where(token => OptionNames(token).All(declared.ContainsKey))
            .Select(token => Token(token, declared));

        return string.Join(' ', new[] { "msagent", path.Trim() }.Concat(rendered).Append("[options]"));
    }

    private static string Token(string token, IReadOnlyDictionary<string, CommandSymbol> declared) =>
        token.Contains(' ', StringComparison.Ordinal) ? token
        : declared[token] is CommandOption { IsSwitch: true } ? token
        : $"{token} <{token.TrimStart('-')}>";

    internal static IEnumerable<string> OptionNames(string token) => token.Split([' ', '|', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(part => part.StartsWith("--", StringComparison.Ordinal));

    private async Task<int> ExecuteAsync(
        CommandMetadata metadata,
        IReadOnlyDictionary<string, Symbol> symbols,
        IReadOnlyList<string> parentOnly,
        string path,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var json = parseResult.GetValue(_json);
        var debug = _debug is not null && parseResult.GetValue(_debug);
        CommandTelemetrySession telemetry = _telemetryAccessor.Current;
        bool renderingSuccess = false;
        bool streamingOutputOwned = false;

        // Parsing has accepted this canonical leaf. Normal product resolution observes any target
        // later; an invocation with no observation keeps the conservative route.
        telemetry.RecordAttempt(
            CommandTelemetryPath.FromMetadata(path.Split(' ')));

        try
        {
            var input = _inputFactory(
                parseResult,
                symbols,
                json,
                parseResult.GetValue(_nonInteractive));
            if (parentOnly.FirstOrDefault(input.WasProvided) is { } inapplicable)
            {
                throw new CommandUsageException($"{inapplicable} does not apply to `msagent {path}`. Run `msagent {path} --help` for its options.");
            }

            var result = await ExecuteWithProgressAsync(metadata, path, input, json || debug, cancellationToken).ConfigureAwait(false);
            if (result is ILiveTestStreamingResult streaming)
            {
                streamingOutputOwned = true;
                int exitCode = await streaming.RenderAsync(
                    _renderer.Stdout, _renderer.Stderr, _renderer.Color, cancellationToken).ConfigureAwait(false);
                telemetry.RecordOutcome(
                    StreamingOutcome(streaming, exitCode),
                    (streaming as IStreamingFailureResult)?.ErrorCode);
                return exitCode;
            }

            renderingSuccess = true;
            _renderer.RenderSuccess(result, json, debug);
            renderingSuccess = false;
            telemetry.RecordOutcome(CommandTelemetryOutcome.Success);

            return ExitCodes.Success;
        }
        catch (CommandUsageException failure)
        {
            if (streamingOutputOwned)
            {
                return ReportStreamingFailure(telemetry, CommandTelemetryOutcome.Usage);
            }

            int exitCode = _renderer.RenderUsageError(failure.Message, json);
            telemetry.RecordOutcome(CommandTelemetryOutcome.Usage);

            return exitCode;
        }
        catch (OperationCanceledException failure) when (cancellationToken.IsCancellationRequested)
        {
            if (streamingOutputOwned)
            {
                return ReportStreamingFailure(telemetry, CommandTelemetryOutcome.Cancellation);
            }

            telemetry.RecordOutcome(CommandTelemetryOutcome.Cancellation);
            if (CliOutputRenderer.HasAuthPartialState(failure))
                _renderer.RenderAuthCancellation(failure, json);
            throw;
        }
        catch (OperationCanceledException failure)
        {
            if (streamingOutputOwned)
            {
                return ReportStreamingFailure(telemetry, CommandTelemetryOutcome.Timeout);
            }

            int exitCode =
                _renderer.RenderFailure(new TransportTimeoutException(failure), json,
                    suppressExceptionDetails: true, partialState: failure);
            telemetry.RecordOutcome(CommandTelemetryOutcome.Timeout);

            return exitCode;
        }
        catch (Exception failure)
        {
            if (streamingOutputOwned)
            {
                // The streaming operation owns stdout, even when its sink fails. Never append a
                // second JSON envelope or print an untrusted nested exception.
                return ReportStreamingFailure(
                    telemetry, OutcomeForFailure(failure), (failure as AgentHostingException)?.ErrorCode);
            }

            int exitCode = _renderer.RenderFailure(failure, json,
                suppressExceptionDetails: _sensitiveCommands.Contains(parseResult.CommandResult.Command));
            if (!renderingSuccess)
            {
                telemetry.RecordOutcome(OutcomeForFailure(failure), (failure as AgentHostingException)?.ErrorCode);
            }

            return exitCode;
        }
    }

    private async Task<object> ExecuteWithProgressAsync(CommandMetadata metadata, string path, ICommandInput input, bool quiet, CancellationToken cancellationToken)
    {
        using (quiet || SuppressesProgress(metadata) ? null : _progressSpinner?.Start(metadata.ProgressLabel ?? $"Running {path}"))
        {
            return await metadata.Implementation.ExecuteAsync(input, _services, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool SuppressesProgress(CommandMetadata metadata) => metadata.SuppressProgress || metadata.Implementation.ResultTypes.Any(typeof(ILiveTestStreamingResult).IsAssignableFrom);

    internal static CommandTelemetryOutcome StreamingOutcome(
        ILiveTestStreamingResult result,
        int exitCode)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (exitCode == ExitCodes.Success)
        {
            return CommandTelemetryOutcome.Success;
        }

        if (result is IStreamingFailureResult { IsCancellation: true })
        {
            return CommandTelemetryOutcome.Cancellation;
        }

        if (result is IPlaygroundStreamingResult playground)
        {
            return playground.SemanticOutcome switch
            {
                PlaygroundSemanticOutcome.Usage => CommandTelemetryOutcome.Usage,
                PlaygroundSemanticOutcome.Timeout => CommandTelemetryOutcome.Timeout,
                PlaygroundSemanticOutcome.Cancellation => CommandTelemetryOutcome.Cancellation,
                _ => CommandTelemetryOutcome.Failure,
            };
        }

        return CommandTelemetryOutcome.Failure;
    }

    private int ReportStreamingFailure(
        CommandTelemetrySession telemetry,
        CommandTelemetryOutcome outcome,
        int? errorCode = null)
    {
        _renderer.Stderr.WriteLine("The live-test result failed after output began; no second result was written.");
        telemetry.RecordOutcome(outcome, errorCode);
        return ExitCodes.Failure;
    }

    private static CommandTelemetryOutcome OutcomeForFailure(Exception failure) =>
        failure is AgentHostingException { ExitCode: ExitCodes.NotSignedIn }
            ? CommandTelemetryOutcome.NotSignedIn
            : failure is AgentHostingException { ExitCode: ExitCodes.Usage }
                ? CommandTelemetryOutcome.Usage
                : CommandTelemetryOutcome.Failure;

    private static IReadOnlyList<CommandSymbol> OrderForHelp(IReadOnlyList<CommandSymbol> symbols)
    {
        var arguments = symbols.Where(symbol => symbol is CommandArgument).ToList();
        var options = symbols.OfType<CommandOption>().ToList();
        var ordered = new List<CommandOption>(options.Where(option => option.IsRequired));
        ordered.AddRange(options.Where(option => !option.IsRequired));
        return [.. arguments, .. ordered];
    }

    internal static SystemCommandLineInput CreateSystemCommandLineInput(
        ParseResult parseResult,
        IReadOnlyDictionary<string, Symbol> symbols,
        bool json,
        bool nonInteractive) =>
        new(parseResult, symbols, json, nonInteractive);

    private static void Attach(Command command, Symbol symbol)
    {
        switch (symbol)
        {
            case Option option:
                command.Add(option);
                break;
            case Argument argument:
                command.Add(argument);
                break;
        }
    }

    private Symbol Materialise(CommandSymbol declared)
    {
        if (declared is CommandArgument argument)
        {
            return new Argument<string>(argument.Name) { Description = argument.Description };
        }

        var option = (CommandOption)declared;
        var aliases = string.Join("\u0000", option.ExtraAliases.Order(StringComparer.Ordinal));
        var key = $"{option.Name}\u0000{option.Description}\u0000{option.ValueType.FullName}\u0000{option.IsRequired}\u0000{option.IsSwitch}\u0000{option.Recursive}\u0000{option.AllowMultipleArgumentsPerToken}\u0000{option.ShortAlias}\u0000{aliases}\u0000{option.IsSensitive}";

        if (_shared.TryGetValue(key, out var existing))
        {
            return existing;
        }

        Symbol created = option switch
        {
            { IsSwitch: true } when option.ValueType == typeof(bool?) => new Option<bool?>(option.Name) { Description = option.Description, Recursive = option.Recursive },
            { IsSwitch: true } => new Option<bool>(option.Name) { Description = option.Description, Recursive = option.Recursive },
            _ when option.ValueType == typeof(int?) => new Option<int?>(option.Name) { Description = option.Description, Recursive = option.Recursive, Required = option.IsRequired },
            _ when option.ValueType == typeof(string[]) => new Option<string[]>(option.Name)
            {
                Description = option.Description,
                Recursive = option.Recursive,
                AllowMultipleArgumentsPerToken = option.AllowMultipleArgumentsPerToken,
            },
            _ => new Option<string>(option.Name) { Description = option.Description, Recursive = option.Recursive, Required = option.IsRequired },
        };

        AddAliases(created, option);
        _shared[key] = created;

        return created;
    }

    private static void AddAliases(Symbol symbol, CommandOption option)
    {
        if (symbol is not Option created)
        {
            return;
        }

        if (option.ShortAlias is { } shortAlias)
        {
            created.Aliases.Add(shortAlias);
        }

        foreach (var alias in option.ExtraAliases)
        {
            created.Aliases.Add(alias);
        }
    }
}
