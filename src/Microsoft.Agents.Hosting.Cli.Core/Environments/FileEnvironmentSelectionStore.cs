using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>The on-disk shape of the active-environment selection. Never token material.</summary>
internal sealed class EnvironmentSelectionDocument
{
    /// <summary>Missing versions denote legacy version 1; mutations write version 2.</summary>
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
    public EnvironmentSelectionOwner? Owner { get; set; }
    public string? DefaultGeneration { get; set; }
}

/// <summary>Source-generated <c>System.Text.Json</c> metadata for the persisted selection.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
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
        cancellationToken.ThrowIfCancellationRequested();
        var document = ReadDocument();
        if (document.Active is null)
        {
            return Task.FromResult<ActiveEnvironment?>(null);
        }

        var stored = document.Active;
        if (!Uri.TryCreate(stored.ApiEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw Corrupt("the stored endpoint is not an absolute URI");
        }

        var environment = new ActiveEnvironment
        {
            EnvironmentId = stored.EnvironmentId,
            DisplayName = stored.DisplayName,
            TenantId = stored.TenantId,
            ApiEndpoint = endpoint,
            ApiAudience = stored.ApiAudience,
            InstanceUrl = stored.InstanceUrl,
            SelectedAt = stored.SelectedAt,
            Owner = document.SchemaVersion == 2 ? stored.Owner : null,
            DefaultGeneration = document.SchemaVersion == 2 ? stored.DefaultGeneration : null,
        };
        if (!EnvironmentProfileBinding.HasValidOwnershipMetadata(environment))
            throw Corrupt("the stored selection ownership is incomplete or invalid");
        return Task.FromResult<ActiveEnvironment?>(environment);
    }

    /// <inheritdoc />
    public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(environment.EnvironmentId))
        {
            throw new ArgumentException("Environment id is required.", nameof(environment));
        }
        if (!EnvironmentProfileBinding.HasValidOwnershipMetadata(environment))
            throw Corrupt("the selection ownership is incomplete or invalid");

        WriteDocument(new EnvironmentSelectionDocument
        {
            SchemaVersion = 2,
            Active = new StoredEnvironmentSelection
            {
                EnvironmentId = environment.EnvironmentId,
                DisplayName = environment.DisplayName,
                TenantId = environment.TenantId,
                ApiEndpoint = environment.ApiEndpoint.ToString(),
                ApiAudience = environment.ApiAudience,
                InstanceUrl = environment.InstanceUrl,
                SelectedAt = environment.SelectedAt,
                Owner = environment.Owner,
                DefaultGeneration = environment.DefaultGeneration,
            },
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_selectionFilePath))
        {
            File.Delete(_selectionFilePath);
        }

        return Task.CompletedTask;
    }

    private EnvironmentSelectionDocument ReadDocument()
    {
        string json;
        try
        {
            json = File.ReadAllText(_selectionFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EnvironmentSelectionDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Corrupt("it could not be read");
        }

        EnvironmentSelectionDocument? document;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            RejectDuplicateProperties(parsed.RootElement, EnvironmentSelectionJsonContext.Default.EnvironmentSelectionDocument);
            document = JsonSerializer.Deserialize(
                json,
                EnvironmentSelectionJsonContext.Default.EnvironmentSelectionDocument);
        }
        catch (JsonException)
        {
            throw Corrupt("it is not valid JSON");
        }

        if (document is null)
        {
            throw Corrupt("it deserialized to null");
        }

        if (document.SchemaVersion is not (1 or 2))
        {
            throw Corrupt($"unsupported schema version {document.SchemaVersion}");
        }

        if (document.Active is not null && string.IsNullOrWhiteSpace(document.Active.EnvironmentId))
        {
            throw Corrupt("the stored selection has an empty environment id");
        }

        return document;
    }

    private static void RejectDuplicateProperties(JsonElement element, JsonTypeInfo metadata)
    {
        if (element.ValueKind != JsonValueKind.Object || metadata.Kind != JsonTypeInfoKind.Object)
            return;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var mapped = metadata.Properties.FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
            if (mapped is null)
                continue;
            if (!keys.Add(property.Name))
                throw Corrupt("duplicate JSON property");
            if (EnvironmentSelectionJsonContext.Default.GetTypeInfo(mapped.PropertyType) is { Kind: JsonTypeInfoKind.Object } nested)
                RejectDuplicateProperties(property.Value, nested);
        }
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

        var tempPath = _selectionFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _selectionFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(tempPath);
            throw new AgentHostingStateException(
                $"The AgentHost environment selection could not be written: {ex.Message}",
                remediation: "Verify the AgentHost config directory is writable, then retry.");
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the write failure is already being reported.
        }
    }

    private static AgentHostingStateException Corrupt(string reason) =>
        new(
            $"The AgentHost environment selection is unusable: {reason}.",
            remediation: "Run `msagent env select <environment-id>` to re-select an environment.");
}
