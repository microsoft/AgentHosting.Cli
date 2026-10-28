using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>A discovered group, with its children resolved.</summary>
public sealed class CommandGroupMetadata
{
    /// <summary>The group name as typed.</summary>
    public required string Name { get; init; }

    /// <summary>Help text.</summary>
    public required string Description { get; init; }

    /// <summary>Overrides the generated "no command specified" line.</summary>
    public string? NoCommandUsage { get; init; }

    /// <summary>Orders siblings in help when explicitly provided.</summary>
    public int? SortPriority { get; init; }

    /// <summary>Whether this group and all descendants suppress command telemetry.</summary>
    public bool SuppressTelemetry { get; init; }

    /// <summary>The declaring type.</summary>
    public required Type DeclaringType { get; init; }

    /// <summary>Features that must all be enabled before this group is exposed.</summary>
    public IReadOnlyList<FeatureName> RequiredFeatures { get; init; } = [];

    /// <summary>Options the group owns, typically recursive ones its children share.</summary>
    public IReadOnlyList<CommandSymbol> Symbols { get; init; } = [];

    /// <summary>Nested groups.</summary>
    public List<CommandGroupMetadata> Groups { get; } = [];

    /// <summary>Commands hanging directly from this group.</summary>
    public List<CommandMetadata> Commands { get; } = [];

    /// <summary>The children as they appear in help, subgroups before commands.</summary>
    public IEnumerable<string> ChildNames => Groups.Select(group => group.Name).Concat(Commands.Select(command => command.Name));
}

/// <summary>A discovered command, with any nested children resolved.</summary>
public sealed class CommandMetadata
{
    /// <summary>The verb as typed.</summary>
    public required string Name { get; init; }

    /// <summary>Help text.</summary>
    public required string Description { get; init; }

    /// <summary>Orders siblings in help when explicitly provided.</summary>
    public int? SortPriority { get; init; }

    /// <summary>The progress spinner text shown while the command runs, or null for a generic one.</summary>
    public string? ProgressLabel { get; init; }

    /// <summary>Whether the progress spinner stays hidden while the command runs.</summary>
    public bool SuppressProgress { get; init; }

    /// <summary>The declaring type.</summary>
    public required Type DeclaringType { get; init; }

    /// <summary>Features that must all be enabled before this command is exposed.</summary>
    public IReadOnlyList<FeatureName> RequiredFeatures { get; init; } = [];

    /// <summary>The command's behaviour.</summary>
    public required ICommandImplementation Implementation { get; init; }

    /// <summary>Commands nested under this one.</summary>
    public List<CommandMetadata> Commands { get; } = [];
}

/// <summary>Discovers the command tree from attributes, failing at startup rather than at use.</summary>
public static class CommandReflector
{
    private static readonly Dictionary<Assembly, IReadOnlyList<CommandGroupMetadata>> Cache = [];

    /// <summary>Discovers every root group in an assembly, caching the result per assembly.</summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The root groups, ordered for help.</returns>
    public static IReadOnlyList<CommandGroupMetadata> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Cache)
        {
            if (Cache.TryGetValue(assembly, out var cached))
            {
                return cached;
            }

            var discovered = Build(assembly);
            Cache[assembly] = discovered;

            return discovered;
        }
    }

    /// <summary>
    /// Discovers the command tree and removes feature-controlled groups and commands before help,
    /// parsing, and dispatch can observe them.
    /// </summary>
    public static IReadOnlyList<CommandGroupMetadata> Discover(Assembly assembly, IFeatureFlags featureFlags)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);

        return
        [
            .. Discover(assembly)
                .Where(group => Enabled(group.RequiredFeatures, featureFlags))
                .Select(group => Filter(group, featureFlags)),
        ];
    }

    private static IReadOnlyList<CommandGroupMetadata> Build(Assembly assembly)
    {
        var groups = new Dictionary<Type, CommandGroupMetadata>();
        var commands = new Dictionary<Type, CommandMetadata>();
        var types = LoadTypes(assembly);

        foreach (var type in types.Where(candidate => candidate.GetCustomAttribute<CommandGroupAttribute>() is not null))
        {
            var attribute = type.GetCustomAttribute<CommandGroupAttribute>()!;
            RequireLowercaseName(attribute.Name, type);

            groups[type] = new CommandGroupMetadata
            {
                Name = attribute.Name,
                Description = attribute.Description,
                NoCommandUsage = attribute.NoCommandUsage,
                SortPriority = ExplicitPriority(attribute.SortPriority),
                SuppressTelemetry = attribute.SuppressTelemetry,
                DeclaringType = type,
                RequiredFeatures = RequiredFeatures(type),
                Symbols = GroupSymbols(type),
            };
        }

        foreach (var type in types.Where(candidate => candidate.GetCustomAttribute<CommandAttribute>() is not null))
        {
            var attribute = type.GetCustomAttribute<CommandAttribute>()!;
            RequireLowercaseName(attribute.Name, type);

            if (attribute.ParentGroup is not null && attribute.ParentCommand is not null)
            {
                throw new CommandConfigurationException($"{type.Name} declares both ParentGroup and ParentCommand.");
            }

            commands[type] = new CommandMetadata
            {
                Name = attribute.Name,
                Description = attribute.Description,
                SortPriority = ExplicitPriority(attribute.SortPriority),
                ProgressLabel = attribute.ProgressLabel,
                SuppressProgress = attribute.SuppressProgress,
                DeclaringType = type,
                RequiredFeatures = RequiredFeatures(type),
                Implementation = Instantiate(type),
            };
        }

        foreach (var (type, group) in groups)
        {
            if (type.GetCustomAttribute<CommandGroupAttribute>()!.ParentGroup is { } parent)
            {
                Parent(groups, parent, type).Groups.Add(group);
            }
        }

        foreach (var (type, command) in commands)
        {
            var attribute = type.GetCustomAttribute<CommandAttribute>()!;

            if (attribute.ParentCommand is { } parentCommand)
            {
                if (!commands.TryGetValue(parentCommand, out var owner))
                {
                    throw new CommandConfigurationException($"{type.Name} names ParentCommand {parentCommand.Name}, which is not a [Command] in this assembly.");
                }

                owner.Commands.Add(command);
                continue;
            }

            if (attribute.ParentGroup is not { } parentGroup)
            {
                throw new CommandConfigurationException($"{type.Name} declares neither ParentGroup nor ParentCommand.");
            }

            Parent(groups, parentGroup, type).Commands.Add(command);
        }

        var roots = groups.Values.Where(group => group.DeclaringType.GetCustomAttribute<CommandGroupAttribute>()!.ParentGroup is null).ToList();

        foreach (var group in groups.Values)
        {
            RequireUniqueChildNames(group);
            Sort(group);
        }

        RequireNoCycle(roots, groups.Count);

        return
        [
            .. roots
                .OrderBy(group => group.SortPriority is null)
                .ThenBy(group => group.SortPriority)
                .ThenBy(group => group.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Reads the assembly's types, reporting a load failure as a configuration error rather than the
    /// raw <see cref="ReflectionTypeLoadException"/>, whose message names no type.
    /// </summary>
    private static Type[] LoadTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException failure)
        {
            var reasons = failure.LoaderExceptions.Where(inner => inner is not null).Select(inner => inner!.Message).Distinct(StringComparer.Ordinal);

            throw new CommandConfigurationException($"Types in {assembly.GetName().Name} could not be loaded: {string.Join("; ", reasons)}");
        }
    }

    /// <summary>
    /// Creates a command instance, naming the class when it cannot be constructed. A command is
    /// discovered by reflection, so it must be concrete with a public parameterless constructor —
    /// dependencies are resolved from the service provider at dispatch, not injected here.
    /// </summary>
    internal static ICommandImplementation Instantiate(Type type)
    {
        if (type.IsAbstract)
        {
            throw new CommandConfigurationException($"{type.Name} carries [Command] but is abstract, so it cannot be instantiated.");
        }

        object? instance;

        try
        {
            instance = Activator.CreateInstance(type);
        }
        catch (Exception failure) when (failure is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            throw new CommandConfigurationException(
                $"{type.Name} carries [Command] but could not be constructed: {failure.Message} " +
                "A command needs a public parameterless constructor; resolve dependencies from the service provider in DispatchAsync instead.");
        }

        return instance as ICommandImplementation
            ?? throw new CommandConfigurationException($"{type.Name} carries [Command] but is not an ICommandImplementation.");
    }

    private static CommandGroupMetadata Parent(Dictionary<Type, CommandGroupMetadata> groups, Type parent, Type child) =>
        groups.TryGetValue(parent, out var owner)
            ? owner
            : throw new CommandConfigurationException($"{child.Name} names ParentGroup {parent.Name}, which is not a [CommandGroup] in this assembly.");

    private static IReadOnlyList<CommandSymbol> GroupSymbols(Type type) =>
        type.GetProperty("Symbols", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IReadOnlyList<CommandSymbol> ?? [];

    private static IReadOnlyList<FeatureName> RequiredFeatures(MemberInfo member) =>
        [.. member.GetCustomAttributes<RequiredFeatureFlagAttribute>().Select(attribute => attribute.Feature)];

    private static bool Enabled(IEnumerable<FeatureName> requiredFeatures, IFeatureFlags featureFlags) =>
        requiredFeatures.All(featureFlags.IsFeatureEnabled);

    private static CommandGroupMetadata Filter(CommandGroupMetadata source, IFeatureFlags featureFlags)
    {
        var groups = source.Groups
            .Where(group => Enabled(group.RequiredFeatures, featureFlags))
            .Select(group => Filter(group, featureFlags))
            .ToList();
        var commands = source.Commands
            .Where(command => Enabled(command.RequiredFeatures, featureFlags))
            .Select(command => Filter(command, featureFlags))
            .ToList();
        var removedDirectChild =
            groups.Count != source.Groups.Count
            || commands.Count != source.Commands.Count;

        var filtered = new CommandGroupMetadata
        {
            Name = source.Name,
            Description = source.Description,
            NoCommandUsage = removedDirectChild ? null : source.NoCommandUsage,
            SortPriority = source.SortPriority,
            SuppressTelemetry = source.SuppressTelemetry,
            DeclaringType = source.DeclaringType,
            RequiredFeatures = source.RequiredFeatures,
            Symbols = source.Symbols,
        };

        filtered.Groups.AddRange(groups);
        filtered.Commands.AddRange(commands);

        return filtered;
    }

    private static CommandMetadata Filter(CommandMetadata source, IFeatureFlags featureFlags)
    {
        var filtered = new CommandMetadata
        {
            Name = source.Name,
            Description = source.Description,
            SortPriority = source.SortPriority,
            ProgressLabel = source.ProgressLabel,
            SuppressProgress = source.SuppressProgress,
            DeclaringType = source.DeclaringType,
            RequiredFeatures = source.RequiredFeatures,
            Implementation = source.Implementation,
        };

        filtered.Commands.AddRange(
            source.Commands
                .Where(command => Enabled(command.RequiredFeatures, featureFlags))
                .Select(command => Filter(command, featureFlags)));

        return filtered;
    }

    private static void RequireLowercaseName(string name, Type type)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsUpper))
        {
            throw new CommandConfigurationException($"{type.Name} declares the name '{name}', which must be lowercase and non-empty.");
        }
    }

    private static void RequireUniqueChildNames(CommandGroupMetadata group)
    {
        var duplicate = group.ChildNames.GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(candidate => candidate.Count() > 1);

        if (duplicate is not null)
        {
            throw new CommandConfigurationException($"Group '{group.Name}' has more than one child named '{duplicate.Key}'.");
        }
    }

    private static void RequireNoCycle(IReadOnlyList<CommandGroupMetadata> roots, int groupCount)
    {
        var reachable = 0;
        var pending = new Stack<CommandGroupMetadata>(roots);

        while (pending.Count > 0)
        {
            var group = pending.Pop();
            reachable++;

            if (reachable > groupCount)
            {
                throw new CommandConfigurationException("The command groups form a cycle.");
            }

            foreach (var child in group.Groups)
            {
                pending.Push(child);
            }
        }

        if (reachable != groupCount)
        {
            throw new CommandConfigurationException("A command group's parent chain never reaches a root group.");
        }
    }

    private static void Sort(CommandGroupMetadata group)
    {
        group.Groups.Sort((left, right) => Compare(left.SortPriority, left.Name, right.SortPriority, right.Name));
        group.Commands.Sort((left, right) => Compare(left.SortPriority, left.Name, right.SortPriority, right.Name));

        foreach (var command in group.Commands)
        {
            command.Commands.Sort((left, right) => Compare(left.SortPriority, left.Name, right.SortPriority, right.Name));
        }
    }

    private static int Compare(int? leftPriority, string leftName, int? rightPriority, string rightName)
    {
        if (leftPriority is not null && rightPriority is not null)
        {
            int priorityComparison = leftPriority.Value.CompareTo(rightPriority.Value);
            return priorityComparison != 0
                ? priorityComparison
                : string.CompareOrdinal(leftName, rightName);
        }

        if (leftPriority is not null)
        {
            return -1;
        }

        if (rightPriority is not null)
        {
            return 1;
        }

        return string.CompareOrdinal(leftName, rightName);
    }

    private static int? ExplicitPriority(int priority) => priority == 0 ? null : priority;
}
