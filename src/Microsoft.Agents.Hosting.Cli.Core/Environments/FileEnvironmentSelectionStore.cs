using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>The on-disk shape of the active-environment selection. Never token material.</summary>
internal sealed class EnvironmentSelectionDocument
{
    /// <summary>Persisted schema version. Only version 1 is understood by this build.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The active selection, or <see langword="null"/> when none is selected.</summary>
    public StoredEnvironmentSelection? Active { get; set; }
}

/// <summary>A single persisted environment selection.</summary>
internal sealed class StoredEnvironmentSelection
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = string.Empty;
    public string ApiAudience { get; set; } = string.Empty;
    public string? InstanceUrl { get; set; }
    public DateTimeOffset SelectedAt { get; set; }
}

/// <summary>Source-generated <c>System.Text.Json</c> metadata for the persisted selection.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EnvironmentSelectionDocument))]
internal sealed partial class EnvironmentSelectionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Atomic, file-backed <see cref="IEnvironmentSelectionStore"/>. It follows the same rules as
/// <see cref="Auth.FileAuthProfileStore"/>: a missing file is a clean "nothing selected" state, writes
/// are temp-file-plus-replace so a crash cannot leave a partial selection, and a corrupt or
/// wrong-schema file is a typed error rather than a silent "no environment selected" — silently
/// forgetting a selection would send the next command at the wrong target.
/// </summary>
public sealed class FileEnvironmentSelectionStore : IEnvironmentSelectionStore
{
    private const int SupportedSchemaVersion = 1;

    private readonly string _selectionFilePath;

    /// <param name="selectionFilePath">
    /// Absolute path to the selection JSON file. The host resolves this under the AgentHost config
    /// directory; Core never reads the environment itself.
    /// </param>
    public FileEnvironmentSelectionStore(string selectionFilePath)
    {
        if (string.IsNullOrWhiteSpace(selectionFilePath))
        {
            throw new ArgumentException("Environment selection file path is required.", nameof(selectionFilePath));
        }

        _selectionFilePath = selectionFilePath;
    }

    /// <inheritdoc />
    public Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var document = ReadDocument();
        if (document.Active is null)
        {
            return Task.FromResult<ActiveEnvironment?>(null);
        }

        var stored = document.Active;
        if (!Uri.TryCreate(stored.ApiEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw Corrupt($"the stored endpoint '{stored.ApiEndpoint}' is not an absolute uri");
        }

        return Task.FromResult<ActiveEnvironment?>(new ActiveEnvironment
        {
            EnvironmentId = stored.EnvironmentId,
            DisplayName = stored.DisplayName,
            TenantId = stored.TenantId,
            ApiEndpoint = endpoint,
            ApiAudience = stored.ApiAudience,
            InstanceUrl = stored.InstanceUrl,
            SelectedAt = stored.SelectedAt,
        });
    }

    /// <inheritdoc />
    public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(environment.EnvironmentId))
        {
            throw new ArgumentException("Environment id is required.", nameof(environment));
        }

        WriteDocument(new EnvironmentSelectionDocument
        {
            Active = new StoredEnvironmentSelection
            {
                EnvironmentId = environment.EnvironmentId,
                DisplayName = environment.DisplayName,
                TenantId = environment.TenantId,
                ApiEndpoint = environment.ApiEndpoint.ToString(),
                ApiAudience = environment.ApiAudience,
                InstanceUrl = environment.InstanceUrl,
                SelectedAt = environment.SelectedAt,
            },
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_selectionFilePath))
        {
            File.Delete(_selectionFilePath);
        }

        return Task.CompletedTask;
    }

    private EnvironmentSelectionDocument ReadDocument()
    {
        if (!File.Exists(_selectionFilePath))
        {
            return new EnvironmentSelectionDocument();
        }

        string json;
        try
        {
            json = AtomicFile.ReadAllText(_selectionFilePath);
        }
        catch (IOException ex)
        {
            throw Corrupt($"it could not be read ({ex.Message})");
        }

        EnvironmentSelectionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                json,
                EnvironmentSelectionJsonContext.Default.EnvironmentSelectionDocument);
        }
        catch (JsonException ex)
        {
            throw Corrupt($"it is not valid JSON ({ex.Message})");
        }

        if (document is null)
        {
            throw Corrupt("it deserialized to null");
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw Corrupt($"unsupported schema version {document.SchemaVersion} (expected {SupportedSchemaVersion})");
        }

        if (document.Active is not null && string.IsNullOrWhiteSpace(document.Active.EnvironmentId))
        {
            throw Corrupt("the stored selection has an empty environment id");
        }

        return document;
    }

    private void WriteDocument(EnvironmentSelectionDocument document)
    {
        var directory = Path.GetDirectoryName(_selectionFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            document,
            EnvironmentSelectionJsonContext.Default.EnvironmentSelectionDocument);

        try
        {
            AtomicFile.WriteAllText(_selectionFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentHostingStateException(
                $"The AgentHost environment selection could not be written: {ex.Message}",
                remediation: "Verify the AgentHost config directory is writable, then retry.");
        }
    }

    private static AgentHostingStateException Corrupt(string reason) =>
        new(
            $"The AgentHost environment selection is unusable: {reason}.",
            remediation: "Run `msagent env select <environment-id>` to re-select an environment.");
}
