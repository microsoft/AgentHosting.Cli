using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Abs;

/// <summary>
/// File-backed <see cref="IConnectionMetadataStore"/> — a faithful C# port of the shipped TypeScript
/// <c>ConnectionMetadataStore</c>. It reads and writes the shared-format,
/// non-secret connection metadata that seeds correlation across reruns, one file per
/// (subscription, resource group, bot) at
/// <c>{connectionsDirectory}/{subscriptionId}/{resourceGroup}/{botName}.json</c>.
///
/// The store treats the file strictly as a <em>cache</em>, never the source of truth: a missing,
/// malformed (bad JSON / wrong shape), or wrong-schema-version file reads as <see langword="null"/> —
/// never thrown from <see cref="ReadAsync"/> — so the planner re-reads Azure and can never silently
/// act on stale data. On write it asserts the payload carries no secret material
/// (<see cref="SecretMaterialGuard"/>), so a leaked credential fails loudly instead of being
/// persisted, and it emits the same shared on-disk shape (camelCase, 2-space indent, trailing
/// newline) the TypeScript implementation writes, so both implementations interoperate.
/// </summary>
public sealed class FileConnectionMetadataStore : IConnectionMetadataStore
{
    private const int MetadataSchemaVersion = 1;
    private const string WriteContext = "connection metadata file";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _connectionsDirectory;

    public FileConnectionMetadataStore(string connectionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(connectionsDirectory))
        {
            throw new ArgumentException("A connections directory is required.", nameof(connectionsDirectory));
        }

        _connectionsDirectory = connectionsDirectory;
    }

    /// <summary>The metadata file path for a connection reference.</summary>
    public string PathFor(ConnectionMetadataRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Path.Combine(
            _connectionsDirectory,
            reference.SubscriptionId,
            reference.ResourceGroup,
            $"{reference.BotName}.json");
    }

    /// <inheritdoc />
    public async Task<ConnectionMetadata?> ReadAsync(
        ConnectionMetadataRef reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var path = PathFor(reference);
        if (!File.Exists(path))
        {
            return null;
        }

        var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        ConnectionMetadata? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ConnectionMetadata>(raw, ReadOptions);
        }
        catch (JsonException)
        {
            // Malformed JSON or missing required members — treat as a cache miss, re-read Azure.
            return null;
        }

        return IsValid(parsed, reference) ? parsed : null;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ConnectionMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // No-secret boundary: refuse to persist any leaked credential material.
        SecretMaterialGuard.AssertNoSecretMaterial(metadata, WriteContext);

        var path = PathFor(new ConnectionMetadataRef
        {
            SubscriptionId = metadata.SubscriptionId,
            ResourceGroup = metadata.ResourceGroup,
            BotName = metadata.BotName,
        });

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(metadata, WriteOptions);
        await File.WriteAllTextAsync(path, json + "\n", Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Structural validation mirroring the oracle's <c>isValidMetadata</c>: the current schema version,
    /// an identity matching the reference, and string values for the correlation/timestamp fields.
    /// </summary>
    private static bool IsValid(ConnectionMetadata? metadata, ConnectionMetadataRef reference) =>
        metadata is not null &&
        metadata.SchemaVersion == MetadataSchemaVersion &&
        metadata.SubscriptionId == reference.SubscriptionId &&
        metadata.ResourceGroup == reference.ResourceGroup &&
        metadata.BotName == reference.BotName &&
        !string.IsNullOrEmpty(metadata.BotResourceId) &&
        !string.IsNullOrEmpty(metadata.MsaAppType) &&
        !string.IsNullOrEmpty(metadata.IdentityMode) &&
        !string.IsNullOrEmpty(metadata.Endpoint) &&
        metadata.CreatedAt is not null &&
        metadata.UpdatedAt is not null;
}
