using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public static class AmsMcsFiles
{
    public const string ConnectionFileName = "conn.json";

    public const string IgnoreFileName = ".gitignore";

    public const string ConfigIgnoreLine = "/.config/agent.config.json";

    public const string CacheIgnoreLine = ".mcs/";

    private const string ReferencesKey = "connectionReferences:";

    private const string NoReferences = "connectionReferences: []";

    private static readonly string[] SyncFiles = [WorkspaceFileNames.Settings, AgentClassifier.WorkspaceLayoutMarkerFileName];

    private static readonly string[] ReferenceFileExtensions = [".json", ".yml", ".yaml"];

    private static readonly UTF8Encoding NoBom = new(false);

    private static readonly UTF8Encoding WithBom = new(true);

    public static string AgentConfigPath(string project) => Path.Combine(project, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName);

    public static AgentConfigDocument? ReadAgentConfig(string project) => Read(AgentConfigPath(project), path => JsonSerializer.Deserialize(File.ReadAllText(path), AgentConfigJsonContext.Default.AgentConfigDocument));

    public static ManagedAgentRecord? Agent(AgentConfigDocument? config, string? agentId) => config?.ManagedAgents.FirstOrDefault(agent => AmsIds.Same(agent.AgentId, agentId));

    public static AgentDeploymentRecord? Deployment(AgentConfigDocument? config, string deploymentId) => config?.AgentDeployments.FirstOrDefault(deployment => AmsIds.Same(deployment.DeploymentId, deploymentId));

    public static IReadOnlyList<AgentDeploymentRecord> Deployments(AgentConfigDocument? config, string agentId) => [.. (config?.AgentDeployments ?? []).Where(deployment => AmsIds.Same(deployment.AgentId, agentId))];

    public static string SyncFilesHash(string agentFolder) => string.Join(';', SyncFiles.Select(name => Path.Combine(agentFolder, name)).Where(File.Exists).Select(path => $"{Path.GetFileName(path)}={Hash(path)}"));

    public static bool IsMissingOrEmpty(string directory) => !Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any();

    public static string? ReadSettings(string agentFolder) => Read(Path.Combine(agentFolder, WorkspaceFileNames.Settings), File.ReadAllText);

    public static AgentSyncInfo? ReadConnection(string agentFolder) => Read(Path.Combine(agentFolder, WorkflowStateCache.FolderName, ConnectionFileName), path => JsonSerializer.Deserialize<AgentSyncInfo>(File.ReadAllText(path)));

    public static IReadOnlyList<string>? ReadCacheIgnore(string agentFolder) => Read(Path.Combine(agentFolder, WorkflowStateCache.FolderName, IgnoreFileName), File.ReadAllLines);

    public static IReadOnlyList<string>? ReadRootIgnore(string project) => Read(Path.Combine(project, IgnoreFileName), File.ReadAllLines);

    public static string? Fingerprint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var entries = LocalDirectories.Entries(directory).Select(entry => (Relative: Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'), Path: entry)).OrderBy(entry => entry.Relative, StringComparer.Ordinal);

        return string.Join(';', entries.Select(entry => Directory.Exists(entry.Path) ? entry.Relative + "/" : $"{entry.Relative}={Hash(entry.Path)}"));
    }

    public static string SettingsPath(string project, string folder) => Path.Combine(project, folder, WorkspaceFileNames.Settings);

    public static string SettingsEntry(string folder) => $"{folder}/{WorkspaceFileNames.Settings}";

    public static string? RecordedBot(string project, string agentId) => Agent(ReadAgentConfig(project), agentId)?.McsAgentId;

    public static string? CacheFingerprint(string project, string folder) => Fingerprint(Path.Combine(project, folder, WorkflowStateCache.FolderName));

    public static string WriteWorkflowState(string project, string folder)
    {
        var entry = $"{folder}/{WorkflowStateCache.FolderName}/{WorkflowStateCache.FileName}";
        var path = Path.Combine(project, entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}\n", NoBom);
        return entry;
    }

    public static string? ComponentMetadata(IEnumerable<string> entries, string folder, string container, string id) => entries.FirstOrDefault(entry => entry.StartsWith($"{folder}/{container}/", StringComparison.OrdinalIgnoreCase) && entry.EndsWith($"-{id}/{WorkflowWorkspace.MetadataFileName}", StringComparison.OrdinalIgnoreCase));

    public static string? ComponentMetadataPath(string project, string folder, string container, string id) => Directory.Exists(Path.Combine(project, folder, container)) && ComponentMetadata(Directory.EnumerateFiles(Path.Combine(project, folder, container), WorkflowWorkspace.MetadataFileName, SearchOption.AllDirectories).Select(path => Path.GetRelativePath(project, path).Replace(Path.DirectorySeparatorChar, '/')), folder, container, id) is { } entry ? Path.Combine(project, entry) : null;

    public static AmsConnectionFacts Connection(string project, string folder, string agentId) => new(ReadConnection(Path.Combine(project, folder)), ReadCacheIgnore(Path.Combine(project, folder)), RecordedBot(project, agentId));

    public static bool HasLine(IReadOnlyList<string>? lines, string line) => (lines ?? []).Any(candidate => string.Equals(candidate.TrimEnd(), line, StringComparison.OrdinalIgnoreCase));

    public static string? FileHash(string path) => File.Exists(path) ? Hash(path) : null;

    public static string TopEntries(string directory) => Directory.Exists(directory) ? string.Join(',', Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)) : string.Empty;

    public static void SetDisplayName(string path, string name)
    {
        var text = File.ReadAllText(path);
        var renamed = AmsMcsText.WithDisplayName(text, name);

        if (renamed == text)
        {
            throw new E2EException($"{path} declares no displayName, or already holds '{name}'.");
        }

        Write(path, renamed);
    }

    public static void AddYamlComment(string path, string comment) => Append(path, File.ReadAllText(path), $"# {comment}");

    public static void AddConnectionReference(string agentFolder, string logicalName, string connectorId)
    {
        var path = Path.Combine(agentFolder, ConnectionReferenceStore.FlatFileName);
        Append(path, File.Exists(path) ? File.ReadAllText(path).Replace(NoReferences, ReferencesKey, StringComparison.Ordinal) : ReferencesKey, $"  - connectionReferenceLogicalName: {logicalName}", $"    connectorId: {connectorId}");
    }

    public static void SetRootSettings(string directory, string name) => File.WriteAllText(Path.Combine(directory, WorkspaceFileNames.Settings), $"displayName: {name}\n", NoBom);

    public static void CopyWithout(string source, string destination, IReadOnlyCollection<string> excluded)
    {
        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);

            if (excluded.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                CopyWithout(entry, Path.Combine(destination, name), excluded);
            }
            else
            {
                File.Copy(entry, Path.Combine(destination, name), overwrite: true);
            }
        }
    }

    public static (string Content, string Component) AddKnowledgeFile(string project, string folder, string fileName, string text)
    {
        var content = $"{folder}/capabilities/knowledge/files/{fileName}";
        var component = $"{content}.mcs.yml";
        var path = Path.Combine(project, content);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, NoBom);
        File.WriteAllText(Path.Combine(project, component), $"mcs.metadata:\n  componentName: {fileName}\n  description: This knowledge source searches information contained in {fileName}\n", NoBom);
        return (content, component);
    }

    public static void RenameConnectionReference(string workspace, string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories).Where(file => ReferenceFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);

            if (text.Contains(from, StringComparison.Ordinal))
            {
                Write(file, text.Replace(from, to, StringComparison.Ordinal));
            }
        }

        var connections = Path.Combine(workspace, "infrastructure", "connections");

        if (File.Exists(Path.Combine(connections, $"{from}.sync.yaml")))
        {
            File.Move(Path.Combine(connections, $"{from}.sync.yaml"), Path.Combine(connections, $"{to}.sync.yaml"));
        }
    }

    public static string? RestoreSignature(AgentConfigDocument? config) => config is not null && config.ManagedAgents.Count > 0 ? $"{Signature(config.ManagedAgents.Select(agent => $"{agent.AgentId}|{agent.SourceControlMetadata?.Directory}|{agent.McsAgentId}"))} // {Signature(config.AgentDeployments.Select(deployment => $"{deployment.AgentId}|{DeploymentLine(deployment)}"))}" : null;

    public static string DeploymentSignature(AgentConfigDocument? config, string agentId) => Signature(Deployments(config, agentId).Select(DeploymentLine));

    public static IReadOnlyList<string> Mismatched(AgentConfigDocument? config, IEnumerable<ManagedAgentListItem> rows, string repositoryId) => [.. rows.Where(row => !RecordedAsListed(Agent(config, row.AgentId), row, repositoryId)).Select(row => $"{row.DisplayName} ({row.AgentId}): {Describe(Agent(config, row.AgentId))}")];

    public static bool HoldsWithCrlf(string path, string text) => File.Exists(path) && File.ReadAllText(path) is var onDisk && onDisk.Contains("\r\n", StringComparison.Ordinal) && onDisk.Replace("\r\n", "\n", StringComparison.Ordinal) == text;

    private static bool RecordedAsListed(ManagedAgentRecord? agent, ManagedAgentListItem row, string repositoryId) => agent?.SourceControlMetadata is { } metadata && AmsIds.Same(metadata.RepositoryId, repositoryId) && string.Equals(metadata.Branch, "main", StringComparison.OrdinalIgnoreCase) && string.Equals(metadata.Directory, row.AgentFolder, StringComparison.OrdinalIgnoreCase) && AmsIds.Same(agent.AgentType, row.AgentType);

    private static string Describe(ManagedAgentRecord? agent) => agent is null ? "not in .config" : $"type={agent.AgentType} repository={agent.SourceControlMetadata?.RepositoryId} branch={agent.SourceControlMetadata?.Branch} directory={agent.SourceControlMetadata?.Directory}";

    private static string Signature(IEnumerable<string> lines) => string.Join(';', lines.Select(line => line.ToLowerInvariant()).Order(StringComparer.Ordinal));

    private static string DeploymentLine(AgentDeploymentRecord deployment) => $"{deployment.DeploymentId}|{deployment.DeploymentName}|{deployment.DeploymentType}";

    private static void Append(string path, string text, params string[] lines)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Write(path, $"{text}{(text.Length == 0 || text.EndsWith('\n') ? string.Empty : newLine)}{string.Concat(lines.Select(line => line + newLine))}");
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text, File.Exists(path) && File.ReadAllBytes(path).AsSpan().StartsWith(WithBom.Preamble) ? WithBom : NoBom);

    private static T? Read<T>(string path, Func<string, T?> read) where T : class => File.Exists(path) ? read(path) : null;

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);

        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public static class AmsIds
{
    public static bool Same(string? first, string? second) => !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    public static bool IsLowercase(string? id) => Guid.TryParseExact(id, "D", out var parsed) && string.Equals(parsed.ToString("D"), id, StringComparison.Ordinal);

    public static IReadOnlyList<string> NotLowercase(AgentConfigDocument config, IReadOnlyList<string> agentIds) => [.. agentIds.SelectMany(agentId => Unlowered(config, agentId))];

    private static IEnumerable<string> Unlowered(AgentConfigDocument config, string agentId)
    {
        if (config.ManagedAgents.FirstOrDefault(agent => Same(agent.AgentId, agentId)) is not { } agent)
        {
            return [$"{agentId}: not in .config"];
        }

        var deployments = config.AgentDeployments.Where(deployment => Same(deployment.AgentId, agent.AgentId)).SelectMany(DeploymentIds);

        return AgentIds(agent).Concat(deployments).Where(id => !(id.Optional && string.IsNullOrEmpty(id.Value)) && !IsLowercase(id.Value)).Select(id => $"{agentId}: {id.Field}='{id.Value}'");
    }

    private static IEnumerable<(string Field, string? Value, bool Optional)> AgentIds(ManagedAgentRecord agent) =>
    [
        ("agentId", agent.AgentId, false),
        ("tenantId", agent.TenantId, false),
        ("environmentId", agent.EnvironmentId, false),
        ("repositoryId", agent.SourceControlMetadata?.RepositoryId, false),
        ("mcsAgentId", agent.McsAgentId, true),
    ];

    private static IEnumerable<(string Field, string? Value, bool Optional)> DeploymentIds(AgentDeploymentRecord deployment) =>
    [
        ($"{deployment.DeploymentName}.deploymentId", deployment.DeploymentId, false),
        ($"{deployment.DeploymentName}.agentId", deployment.AgentId, false),
        ($"{deployment.DeploymentName}.tenantId", deployment.TenantId, false),
        ($"{deployment.DeploymentName}.homeEnvironmentId", deployment.HomeEnvironmentId, true),
        ($"{deployment.DeploymentName}.environmentId", deployment.EnvironmentId, true),
    ];
}

public sealed record AmsConnectionFacts(AgentSyncInfo? Connection, IReadOnlyList<string>? CacheIgnore, string? RecordedBot)
{
    public bool Names(string? botId, string? environmentId) => Guid.TryParse(botId, out var bot) && Connection is { } connection && connection.AgentId == bot && AmsIds.Same(connection.EnvironmentId, environmentId);

    public bool BindsTo(string? botId, string? environmentId, Uri? organization) => Names(botId, environmentId) && organization is not null && string.Equals(Connection!.DataverseEndpoint?.Host, organization.Host, StringComparison.OrdinalIgnoreCase) && AmsMcsFiles.HasLine(CacheIgnore, "*") && AmsIds.Same(RecordedBot, botId);

    public string Describe() => $"conn.json: AgentId={Connection?.AgentId} EnvironmentId={Connection?.EnvironmentId} DataverseEndpoint={Connection?.DataverseEndpoint}; .mcs\\.gitignore='{string.Join('|', CacheIgnore ?? [])}'; mcsAgentId={RecordedBot}";
}

public sealed record AmsFolderState(string? Cache, string? Bot)
{
    public static AmsFolderState Read(string project, string folder, string agentId) => new(AmsMcsFiles.CacheFingerprint(project, folder), AmsMcsFiles.RecordedBot(project, agentId));

    public bool Kept(AmsFolderState after, bool connected) => after == this && (Cache is not null) == connected;

    public string Describe(AmsFolderState after) => $".mcs: {(after.Cache is null ? "absent" : after.Cache == Cache ? "unchanged" : "changed")} mcsAgentId: {Bot}->{after.Bot}";
}
