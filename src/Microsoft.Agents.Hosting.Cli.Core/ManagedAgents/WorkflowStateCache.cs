using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// The workflow activation state last observed in each environment, keyed by environment.
/// </summary>
public sealed class WorkflowStateCache
{
    /// <summary>The value recorded for a workflow the service reports as a draft.</summary>
    public const int Draft = 0;

    /// <summary>The value recorded for a workflow the service reports as activated.</summary>
    public const int Activated = 1;

    internal const string WriteGuardNamePrefix = "ah-workflow-state-";

    private const string CacheFileName = ".mcs/workflow-state.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ICopilotFileSystem _fileSystem;
    private readonly TimeSpan _writeGuardPatience;

    /// <summary>Creates the cache.</summary>
    /// <param name="fileSystem">Reads and writes the cache file.</param>
    public WorkflowStateCache(ICopilotFileSystem fileSystem) : this(fileSystem, TimeSpan.FromSeconds(5))
    {
    }

    internal WorkflowStateCache(ICopilotFileSystem fileSystem, TimeSpan writeGuardPatience)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _writeGuardPatience = writeGuardPatience;
    }

    /// <summary>Reads the states last observed in one environment.</summary>
    /// <param name="workspaceDirectory">The agent workspace holding the cache.</param>
    /// <param name="environmentId">The environment whose observations are wanted.</param>
    /// <returns>Workflow name to state, empty when nothing was observed for that environment.</returns>
    public IReadOnlyDictionary<string, int> Read(string workspaceDirectory, string environmentId)
    {
        var all = ReadAll(workspaceDirectory);

        return all.TryGetValue(environmentId, out var states) ? states : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records the states observed in one environment, leaving other environments intact.</summary>
    /// <param name="workspaceDirectory">The agent workspace holding the cache.</param>
    /// <param name="environmentId">The environment the observation belongs to.</param>
    /// <param name="states">Workflow name to state, as the service reported it.</param>
    public void Write(string workspaceDirectory, string environmentId, IReadOnlyDictionary<string, int> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        Store(workspaceDirectory, environmentId, new Dictionary<string, int>(states, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Forgets what was observed in one environment, leaving other environments intact.</summary>
    /// <param name="workspaceDirectory">The agent workspace holding the cache.</param>
    /// <param name="environmentId">The environment whose observations are no longer trustworthy.</param>
    public void Invalidate(string workspaceDirectory, string environmentId) =>
        Store(workspaceDirectory, environmentId, []);

    private void Store(string workspaceDirectory, string environmentId, Dictionary<string, int> states)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory) || string.IsNullOrWhiteSpace(environmentId))
        {
            return;
        }

        using var guard = NamedMutexLock.TryAcquire(WriteGuardNamePrefix, workspaceDirectory, _writeGuardPatience);

        var all = new Dictionary<string, Dictionary<string, int>>(ReadAll(workspaceDirectory), StringComparer.OrdinalIgnoreCase)
        {
            [environmentId] = states,
        };

        _fileSystem.WriteAllText(PathFor(workspaceDirectory), JsonSerializer.Serialize(all, WriteOptions) + "\n");
    }

    /// <summary>
    /// Reads every environment's observations, degrading an unusable file to no observations.
    ///
    /// Parsing is not enough to trust the shape: a null environment map, a duplicate environment key
    /// differing only by case, or a state value the cache never writes all survive deserialization and
    /// would otherwise reach a caller expecting none of them.
    /// </summary>
    private Dictionary<string, Dictionary<string, int>> ReadAll(string workspaceDirectory)
    {
        var empty = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return empty;
        }

        var path = PathFor(workspaceDirectory);

        if (!_fileSystem.FileExists(path))
        {
            return empty;
        }

        try
        {
            return Accept(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>?>?>(_fileSystem.ReadAllText(path), ReadOptions)) ?? empty;
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return empty;
        }
    }

    /// <summary>
    /// Keeps the file only when every entry is one this cache could itself have written.
    /// </summary>
    /// <param name="parsed">What deserialization produced.</param>
    /// <returns>The observations, or <see langword="null"/> when any part of the shape is unusable.</returns>
    private static Dictionary<string, Dictionary<string, int>>? Accept(Dictionary<string, Dictionary<string, int>?>? parsed)
    {
        if (parsed is null)
        {
            return null;
        }

        var accepted = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var environment in parsed)
        {
            if (environment.Value is null || string.IsNullOrWhiteSpace(environment.Key) || accepted.ContainsKey(environment.Key))
            {
                return null;
            }

            var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var state in environment.Value)
            {
                if (state.Value is not (Draft or Activated) || string.IsNullOrWhiteSpace(state.Key) || states.ContainsKey(state.Key))
                {
                    return null;
                }

                states[state.Key] = state.Value;
            }

            accepted[environment.Key] = states;
        }

        return accepted;
    }

    private static string PathFor(string workspaceDirectory) => Path.Combine(workspaceDirectory, CacheFileName);
}
