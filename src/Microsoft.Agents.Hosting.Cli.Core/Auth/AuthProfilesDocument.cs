using Microsoft.Agents.Hosting.Cli.Core.Environments;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// The on-disk shape of the AgentHost auth profile store. A single active pointer over a list of
/// Universal profiles. Version 1 is accepted for read-only in-memory migration; mutations write
/// version 2 with opaque identities. No PAC profile or credential cache is imported.
/// </summary>
internal sealed class AuthProfilesDocument
{
    /// <summary>Missing versions denote the original version 1 schema.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Version 1 legacy active pointer; omitted from version 2 writes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveAccountKey { get; set; }

    /// <summary>Version 2 opaque identity of the active profile, or null when signed out.</summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>The persisted profiles. Never token material.</summary>
    public List<StoredAuthProfile> Profiles { get; set; } = [];
}

/// <summary>A single persisted profile record. Contains no token or secret material.</summary>
internal sealed class StoredAuthProfile
{
    public string ProfileId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public long Revision { get; set; }
    public string? HomeAccountId { get; set; }
    public AuthCredentialMethod? CredentialMethod { get; set; }
    public AuthPrincipalType PrincipalType { get; set; } = AuthPrincipalType.User;
    public string? ConnectionUrl { get; set; }
    public string? CredentialReference { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificateThumbprint { get; set; }
    public ActiveEnvironment? DefaultEnvironment { get; set; }
    public string AccountKey { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? Authority { get; set; }
    public CloudInstance Cloud { get; set; } = CloudInstance.Public;

    /// <summary>
    /// The ring the profile signs in against, absent in a profile written before the ring became
    /// selectable. Nullable so a missing field, which defaults, stays distinguishable from a value
    /// no ring is declared for, which is corrupt.
    /// </summary>
    public PowerPlatformApiRing? Ring { get; set; }

    public string? FlowType { get; set; }

    [JsonConverter(typeof(OptionalProfileStringConverter))]
    public string? AuthenticatedTenantId { get; set; }

    [JsonConverter(typeof(OptionalProfileStringConverter))]
    public string? PrincipalObjectId { get; set; }
}

/// <summary>
/// Keeps optional telemetry metadata isolated from the product profile shape. Unknown JSON token
/// shapes are ignored so they cannot make an otherwise valid authentication profile unreadable.
/// </summary>
internal sealed class OptionalProfileStringConverter : JsonConverter<string>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            try
            {
                return reader.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        using JsonDocument ignored = JsonDocument.ParseValue(ref reader);
        return null;
    }

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// Source-generated <c>System.Text.Json</c> metadata for the persisted profile store.
/// <see cref="Microsoft.Agents.Hosting.Cli.Core.Auth.CloudInstance"/> is written as a string
/// so the file stays stable if the enum is reordered.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AuthProfilesDocument))]
internal sealed partial class AuthProfileJsonContext : JsonSerializerContext
{
}
