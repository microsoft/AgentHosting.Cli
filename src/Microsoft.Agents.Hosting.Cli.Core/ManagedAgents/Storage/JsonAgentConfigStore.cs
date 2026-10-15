using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

/// <summary>
/// Atomic, file-backed implementation of both registry tables over one <c>agent.config.json</c>.
/// </summary>
public sealed class JsonAgentConfigStore : IManagedAgentRegistry, IAgentDeploymentRegistry
{
    private const string InitialETag = "1";

    private readonly AgentConfigLocator _locator;
    private readonly IClock _clock;

    /// <summary>Creates the store.</summary>
    /// <param name="locator">Resolves the configuration path for a project directory.</param>
    /// <param name="clock">Supplies creation and change timestamps.</param>
    public JsonAgentConfigStore(AgentConfigLocator locator, IClock clock)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Reads the whole document, or an empty one when no configuration exists.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The document as stored.</returns>
    public AgentConfigDocument Read(string projectDirectory)
    {
        var path = _locator.ConfigFilePath(projectDirectory);

        if (!File.Exists(path))
        {
            return new AgentConfigDocument();
        }

        var text = File.ReadAllText(path);
        AgentConfigDocument? document;

        try
        {
            using var probe = JsonDocument.Parse(text);
            RequireSupportedSchemaVersion(path, probe);
            document = probe.RootElement.Deserialize(AgentConfigJsonContext.Default.AgentConfigDocument);
        }
        catch (JsonException failure)
        {
            throw ManagedAgentException.ConfigInvalid(path, failure.Message);
        }

        if (document is null)
        {
            throw ManagedAgentException.ConfigInvalid(path, "the file is empty.");
        }

        Validate(document, projectDirectory);

        return document with { SchemaVersion = AgentConfigSchema.Version };
    }

    private static void RequireSupportedSchemaVersion(string path, JsonDocument probe)
    {
        if (probe.RootElement.ValueKind != JsonValueKind.Object || !probe.RootElement.TryGetProperty("schemaVersion", out var property))
        {
            return;
        }

        var version = property.TryGetInt32(out var stored) ? stored : 0;

        if (version < AgentConfigSchema.MinimumReadableVersion || version > AgentConfigSchema.Version)
        {
            throw ManagedAgentException.ConfigUnsupportedVersion(path, version, AgentConfigSchema.Version);
        }
    }

    /// <summary>Requires an existing configuration, failing when the directory holds none.</summary>
    /// <param name="projectDirectory">The project directory holding the configuration.</param>
    /// <returns>The document as stored.</returns>
    public AgentConfigDocument RequireExisting(string projectDirectory)
    {
        var path = _locator.ConfigFilePath(projectDirectory);

        return File.Exists(path) ? Read(projectDirectory) : throw ManagedAgentException.ConfigNotFound(path);
    }

    /// <inheritdoc />
    public IReadOnlyList<ManagedAgentRecord> List(string projectDirectory) => Read(projectDirectory).ManagedAgents;

    /// <inheritdoc />
    public ManagedAgentRecord Add(string projectDirectory, ManagedAgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var now = _clock.UtcNow;
        var stored = record with { EntityTag = InitialETag, CreatedTime = now, ChangedTime = now };

        Mutate(projectDirectory, document => document with { ManagedAgents = [.. document.ManagedAgents, stored] });

        return stored;
    }

    /// <inheritdoc />
    public ManagedAgentRecord Update(string projectDirectory, ManagedAgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stored = record with { EntityTag = NextETag(record.EntityTag), ChangedTime = _clock.UtcNow };

        Mutate(projectDirectory, document =>
        {
            RequireETag(FindAgent(document, record.AgentId, record.AgentId).EntityTag, record.EntityTag, record.AgentId);

            return document with { ManagedAgents = [.. document.ManagedAgents.Select(existing => existing.AgentId == record.AgentId ? stored : existing)] };
        });

        return stored;
    }

    /// <inheritdoc />
    public int RemoveWithDeployments(string projectDirectory, string agentId, string expectedETag)
    {
        var removed = 0;

        Mutate(projectDirectory, document =>
        {
            RequireETag(FindAgent(document, agentId, agentId).EntityTag, expectedETag, agentId);
            removed = document.AgentDeployments.Count(deployment => deployment.AgentId == agentId);

            return document with
            {
                ManagedAgents = [.. document.ManagedAgents.Where(agent => agent.AgentId != agentId)],
                AgentDeployments = [.. document.AgentDeployments.Where(deployment => deployment.AgentId != agentId)],
            };
        });

        return removed;
    }

    /// <inheritdoc />
    IReadOnlyList<AgentDeploymentRecord> IAgentDeploymentRegistry.List(string projectDirectory) => Read(projectDirectory).AgentDeployments;

    /// <inheritdoc />
    public AgentDeploymentRecord Add(string projectDirectory, AgentDeploymentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stored = record with { EntityTag = InitialETag, CreatedTime = _clock.UtcNow, ChangedTime = _clock.UtcNow };

        Mutate(projectDirectory, document => WithSingleDefault(document with { AgentDeployments = [.. document.AgentDeployments, stored] }, stored, _clock.UtcNow));

        return stored;
    }

    /// <inheritdoc />
    public AgentDeploymentRecord Update(string projectDirectory, AgentDeploymentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var stored = record with { EntityTag = NextETag(record.EntityTag), ChangedTime = _clock.UtcNow };

        Mutate(projectDirectory, document =>
        {
            RequireETag(FindDeployment(document, record.DeploymentId).EntityTag, record.EntityTag, record.DeploymentId);

            return WithSingleDefault(document with { AgentDeployments = [.. document.AgentDeployments.Select(existing => existing.DeploymentId == record.DeploymentId ? stored : existing)] }, stored, _clock.UtcNow);
        });

        return stored;
    }

    private static AgentConfigDocument WithSingleDefault(AgentConfigDocument document, AgentDeploymentRecord stored, DateTimeOffset changedTime) => stored.IsDefault != true
        ? document
        : document with { AgentDeployments = [.. document.AgentDeployments.Select(existing => existing.DeploymentId != stored.DeploymentId && existing.AgentId == stored.AgentId && existing.IsDefault == true
            ? existing with { IsDefault = null, EntityTag = NextETag(existing.EntityTag), ChangedTime = changedTime }
            : existing)] };

    /// <inheritdoc />
    public void Remove(string projectDirectory, string deploymentId, string expectedETag) => Mutate(projectDirectory, document =>
    {
        RequireETag(FindDeployment(document, deploymentId).EntityTag, expectedETag, deploymentId);

        return document with { AgentDeployments = [.. document.AgentDeployments.Where(deployment => deployment.DeploymentId != deploymentId)] };
    });

    /// <summary>
    /// Runs a read, compare and write as one transaction, excluding other processes.
    ///
    /// The ETag alone only detects a conflict between two writes this process made. Two CLI processes
    /// in the same project can each read the same document, each see a matching ETag, and each write —
    /// losing whichever landed first. A named system-wide mutex keyed on the configuration path closes
    /// that window; the file is small and every command touches it once, so contention is not a concern.
    /// </summary>
    private void Mutate(string projectDirectory, Func<AgentConfigDocument, AgentConfigDocument> mutate)
    {
        using var transaction = AcquireWriteLock(projectDirectory);

        var mutated = mutate(Read(projectDirectory));

        Validate(mutated, projectDirectory);
        Write(projectDirectory, mutated);
    }

    private ConfigWriteLock AcquireWriteLock(string projectDirectory) => new(_locator.ConfigFilePath(projectDirectory));

    private sealed class ConfigWriteLock : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _held;

        public ConfigWriteLock(string configPath)
        {
            _mutex = new Mutex(initiallyOwned: false, NameFor(configPath));

            try
            {
                _held = _mutex.WaitOne(TimeSpan.FromSeconds(30));
            }
            catch (AbandonedMutexException)
            {
                _held = true;
            }

            if (!_held)
            {
                _mutex.Dispose();
                throw ManagedAgentException.ConfigLocked(configPath);
            }
        }

        public void Dispose()
        {
            if (_held)
            {
                _mutex.ReleaseMutex();
            }

            _mutex.Dispose();
        }

        private static string NameFor(string configPath) =>
            $"Global\\ah-agent-config-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configPath.ToUpperInvariant())))[..32]}";
    }

    private void Write(string projectDirectory, AgentConfigDocument document)
    {
        var path = _locator.ConfigFilePath(projectDirectory);
        var directory = Path.GetDirectoryName(path)!;

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $"{AgentConfigLocator.ConfigFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, AgentConfigJsonContext.Default.AgentConfigDocument));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RequireUniqueWorkflows(AgentConfigDocument document, string path)
    {
        foreach (var clash in document.ManagedAgents
            .Where(agent => string.Equals(agent.AgentType, AgentTypes.McsWorkflow, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(agent.McsWorkflowId))
            .GroupBy(agent => agent.McsWorkflowId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            throw ManagedAgentException.ConfigInvalid(path, $"workflow '{clash.Key}' is registered more than once: {string.Join(", ", clash.Select(agent => agent.AgentId))}.");
        }
    }

    private void Validate(AgentConfigDocument document, string projectDirectory)
    {
        var path = _locator.ConfigFilePath(projectDirectory);

        RequireUnique(document.ManagedAgents.Select(agent => agent.AgentId), path, "agent id");
        RequireUnique(document.ManagedAgents.Select(agent => agent.DisplayName), path, "agent name");
        RequireUnique(document.AgentDeployments.Select(deployment => deployment.DeploymentId), path, "deployment id");
        RequireUniqueWorkflows(document, path);

        var locations = new List<string>();
        var physicalRoot = AgentConfigLocator.PhysicalPath(_locator.ResolveProjectDirectory(projectDirectory));

        foreach (var agent in document.ManagedAgents)
        {
            if (!AgentTypes.All.Contains(agent.AgentType, StringComparer.Ordinal))
            {
                throw ManagedAgentException.ConfigInvalid(path, $"agent '{agent.AgentId}' has unknown type '{agent.AgentType}'.");
            }

            if (!EntityStates.All.Contains(agent.AgentState, StringComparer.Ordinal))
            {
                throw ManagedAgentException.ConfigInvalid(path, $"agent '{agent.AgentId}' has unknown state '{agent.AgentState}'.");
            }

            locations.Add(AgentConfigLocator.ContainedLocationUnder(physicalRoot, agent.AgentLocation) ?? throw ManagedAgentException.ConfigInvalid(path, $"agent '{agent.AgentId}' has a location outside the project: '{agent.AgentLocation}'."));
        }

        RequireUnique(locations, path, "agent location");

        foreach (var deployment in document.AgentDeployments)
        {
            var isCodeAgent = string.Equals(deployment.AgentType, AgentTypes.CodeAgent, StringComparison.Ordinal);
            var owner = document.ManagedAgents.FirstOrDefault(agent => agent.AgentId == deployment.AgentId);

            if (owner is null && !isCodeAgent)
            {
                throw ManagedAgentException.ConfigInvalid(path, $"deployment '{deployment.DeploymentId}' references unknown agent '{deployment.AgentId}'.");
            }

            if (!DeploymentStages.TryParse(deployment.DeploymentType, out _))
            {
                throw ManagedAgentException.ConfigInvalid(path, $"deployment '{deployment.DeploymentId}' has unknown stage '{deployment.DeploymentType}'.");
            }

            if (owner is not null)
            {
                RequireMatchesOwner(path, deployment, "type", deployment.AgentType, owner.AgentType);
                RequireMatchesOwner(path, deployment, "tenant", deployment.TenantId, owner.TenantId);
            }

            if (!isCodeAgent)
            {
                RequireMcsTarget(path, deployment);
                RequireMatchesOwner(path, deployment, "home environment", deployment.HomeEnvironmentId!, owner!.EnvironmentId);
                RequireSingleDeployment(document, deployment, candidate => candidate.AgentId == deployment.AgentId && string.Equals(candidate.EnvironmentId, deployment.EnvironmentId, StringComparison.OrdinalIgnoreCase), "environment", deployment.EnvironmentId!);
            }

            RequireSingleDeployment(document, deployment, candidate => candidate.AgentId == deployment.AgentId && string.Equals(candidate.DeploymentName, deployment.DeploymentName, StringComparison.OrdinalIgnoreCase), "name", deployment.DeploymentName);
        }
    }

    private static void RequireMcsTarget(string path, AgentDeploymentRecord deployment)
    {
        foreach (var (label, value) in new[] { ("home environment", deployment.HomeEnvironmentId), ("environment", deployment.EnvironmentId) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw ManagedAgentException.ConfigInvalid(path, $"MCS deployment '{deployment.DeploymentId}' is missing its {label}.");
            }
        }
    }

    private static void RequireSingleDeployment(AgentConfigDocument document, AgentDeploymentRecord deployment, Func<AgentDeploymentRecord, bool> conflicts, string constraint, string value)
    {
        if (document.AgentDeployments.Any(candidate => candidate.DeploymentId != deployment.DeploymentId && conflicts(candidate)))
        {
            throw ManagedAgentException.SlotAlreadyExists(constraint, value);
        }
    }

    private static void RequireMatchesOwner(string path, AgentDeploymentRecord deployment, string label, string value, string expected)
    {
        if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw ManagedAgentException.ConfigInvalid(path, $"deployment '{deployment.DeploymentId}' has {label} '{value}' but its agent has '{expected}'.");
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string path, string label)
    {
        var duplicate = values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw ManagedAgentException.ConfigInvalid(path, $"{label} '{duplicate.Key}' appears more than once.");
        }
    }

    private static ManagedAgentRecord FindAgent(AgentConfigDocument document, string agentId, string selector) =>
        document.ManagedAgents.FirstOrDefault(agent => agent.AgentId == agentId) ?? throw ManagedAgentException.AgentNotFound(selector);

    private static AgentDeploymentRecord FindDeployment(AgentConfigDocument document, string deploymentId) =>
        document.AgentDeployments.FirstOrDefault(deployment => deployment.DeploymentId == deploymentId) ?? throw ManagedAgentException.SlotNotFound(deploymentId);

    private static void RequireETag(string actual, string expected, string recordId)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ManagedAgentException.ETagMismatch(recordId);
        }
    }

    private static string NextETag(string current) => int.TryParse(current, out var version) ? (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : InitialETag;
}
