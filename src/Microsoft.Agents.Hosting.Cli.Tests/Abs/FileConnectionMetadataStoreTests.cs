using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for <see cref="FileConnectionMetadataStore"/>. They exercise the round-trip,
/// the shared on-disk shape/path, and the cache-not-truth contract: a missing, malformed,
/// wrong-version, or identity-mismatched file reads as <see langword="null"/> (never thrown), and a
/// write refuses any leaked secret material.
/// </summary>
public sealed class FileConnectionMetadataStoreTests
{
    private static readonly ConnectionMetadataRef Reference = new()
    {
        SubscriptionId = "sub-1",
        ResourceGroup = "rg-1",
        BotName = "bot-1",
    };

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);

        await store.WriteAsync(Metadata(), default);
        var read = await store.ReadAsync(Reference, default);

        Assert.NotNull(read);
        Assert.Equal("bot-1", read!.BotName);
        Assert.Equal(BotResourceId, read.BotResourceId);
        Assert.Equal("ClientSecret", read.IdentityMode);
    }

    [Fact]
    public async Task Read_MissingFile_IsNull()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);
        Assert.Null(await store.ReadAsync(Reference, default));
    }

    [Fact]
    public async Task PathFor_UsesSubscriptionResourceGroupBotLayout()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);

        await store.WriteAsync(Metadata(), default);

        var expected = Path.Combine(dir.Path, "sub-1", "rg-1", "bot-1.json");
        Assert.Equal(expected, store.PathFor(Reference));
        Assert.True(File.Exists(expected));
        Assert.EndsWith("\n", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task Write_EmitsCamelCaseJson()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);

        await store.WriteAsync(Metadata(), default);

        var text = await File.ReadAllTextAsync(store.PathFor(Reference));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("bot-1", doc.RootElement.GetProperty("botName").GetString());
        Assert.Equal("SingleTenant", doc.RootElement.GetProperty("msaAppType").GetString());
    }

    [Fact]
    public async Task Read_WrongSchemaVersion_IsNull()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);
        await WriteRaw(store, """
            {"schemaVersion":2,"subscriptionId":"sub-1","resourceGroup":"rg-1","botName":"bot-1",
             "botResourceId":"/x","msaAppType":"SingleTenant","identityMode":"ClientSecret",
             "endpoint":"https://e","createdAt":"t","updatedAt":"t"}
            """);
        Assert.Null(await store.ReadAsync(Reference, default));
    }

    [Fact]
    public async Task Read_IdentityMismatch_IsNull()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);
        await WriteRaw(store, """
            {"schemaVersion":1,"subscriptionId":"OTHER","resourceGroup":"rg-1","botName":"bot-1",
             "botResourceId":"/x","msaAppType":"SingleTenant","identityMode":"ClientSecret",
             "endpoint":"https://e","createdAt":"t","updatedAt":"t"}
            """);
        Assert.Null(await store.ReadAsync(Reference, default));
    }

    [Fact]
    public async Task Read_MalformedJson_IsNull()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);
        await WriteRaw(store, "{ not json ");
        Assert.Null(await store.ReadAsync(Reference, default));
    }

    [Fact]
    public async Task Read_MissingRequiredField_IsNull()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);
        // No createdAt/updatedAt — isValidMetadata equivalent must reject.
        await WriteRaw(store, """
            {"schemaVersion":1,"subscriptionId":"sub-1","resourceGroup":"rg-1","botName":"bot-1",
             "botResourceId":"/x","msaAppType":"SingleTenant","identityMode":"ClientSecret","endpoint":"https://e"}
            """);
        Assert.Null(await store.ReadAsync(Reference, default));
    }

    [Fact]
    public async Task Write_RejectsSecretMaterial()
    {
        using var dir = new TempDir();
        var store = new FileConnectionMetadataStore(dir.Path);

        // A JWT-shaped value in any leaf must trip the secret guard on write.
        var leaky = Metadata() with { BotResourceId = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.abc123" };

        await Assert.ThrowsAsync<SecretMaterialException>(() => store.WriteAsync(leaky, default));
    }

    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/bot-1";

    private static async Task WriteRaw(FileConnectionMetadataStore store, string json)
    {
        var path = store.PathFor(Reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json);
    }

    private static ConnectionMetadata Metadata() => new()
    {
        SchemaVersion = 1,
        SubscriptionId = "sub-1",
        ResourceGroup = "rg-1",
        BotName = "bot-1",
        BotResourceId = BotResourceId,
        AppId = "11111111-1111-1111-1111-111111111111",
        MsaAppType = "SingleTenant",
        IdentityMode = "ClientSecret",
        Endpoint = "https://gateway.contoso.com/api/messages",
        CreatedAt = "2025-01-01T00:00:00.0000000+00:00",
        UpdatedAt = "2025-01-01T00:00:00.0000000+00:00",
    };
}
