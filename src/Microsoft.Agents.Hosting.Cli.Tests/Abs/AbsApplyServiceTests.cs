using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Offline unit tests for <see cref="AbsApplyService"/> — the confirmed apply core. They drive it
/// against the read fake, a <see cref="RecordingAbsMutationPort"/>, and an
/// <see cref="InMemoryConnectionMetadataStore"/> to assert the force gate, the create/update/no-op
/// status branches, the single-upsert idempotency, metadata persistence with <c>createdAt</c>
/// preservation, and the pre-mutation refusal of a blocking conflict or failed preflight.
/// </summary>
public sealed class AbsApplyServiceTests
{
    private const string BotResourceId =
        "/subscriptions/sub-contoso/resourceGroups/rg-agenthost/providers/Microsoft.BotService/botServices/agenthost-bot";
    private const string SuppliedAppId = "11111111-1111-1111-1111-111111111111";
    private const string Endpoint = "https://gateway.contoso.com/api/messages";

    [Fact]
    public async Task WithoutForce_ReturnsPlanned_AndNeverMutates()
    {
        var mutation = new RecordingAbsMutationPort();
        var metadata = new InMemoryConnectionMetadataStore();
        var service = Service(new FakeAbsStateReader(), mutation, metadata);

        var report = await service.ApplyAsync(Request(force: false), default);

        Assert.Equal("planned", report.Status);
        Assert.Empty(mutation.Calls);
        Assert.Empty(metadata.Writes);
    }

    [Fact]
    public async Task ForceCreate_UpsertsOnce_WritesMetadata_StatusCreated()
    {
        var mutation = new RecordingAbsMutationPort();
        var metadata = new InMemoryConnectionMetadataStore();
        var service = Service(new FakeAbsStateReader(), mutation, metadata);

        var report = await service.ApplyAsync(Request(force: true), default);

        Assert.Equal("created", report.Status);
        Assert.Equal(["arm.upsert"], mutation.Calls);

        var written = Assert.Single(metadata.Writes);
        Assert.Equal(1, written.SchemaVersion);
        Assert.Equal("sub-contoso", written.SubscriptionId);
        Assert.Equal("rg-agenthost", written.ResourceGroup);
        Assert.Equal("agenthost-bot", written.BotName);
        Assert.Equal(BotResourceId, written.BotResourceId);
        Assert.Equal(SuppliedAppId, written.AppId);
        Assert.Equal("SingleTenant", written.MsaAppType);
        Assert.Equal(Endpoint, written.Endpoint);
        Assert.NotNull(written.CreatedAt);
        Assert.NotNull(written.UpdatedAt);
    }

    [Fact]
    public async Task ForceNoop_WhenExistingMatches_StatusUnchanged_NoMutation()
    {
        var reader = new FakeAbsStateReader { ExistingBot = MatchingBot() };
        var mutation = new RecordingAbsMutationPort();
        var service = Service(reader, mutation, new InMemoryConnectionMetadataStore());

        var report = await service.ApplyAsync(Request(force: true), default);

        Assert.Equal("unchanged", report.Status);
        Assert.Empty(mutation.Calls);
    }

    [Fact]
    public async Task ForceUpdate_WhenEndpointDiffers_UpsertsOnce_StatusUpdated_PreservesCreatedAt()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = MatchingBot() with { MessagingEndpoint = "https://old.contoso.com/api/messages" },
        };
        var mutation = new RecordingAbsMutationPort();
        var metadata = new InMemoryConnectionMetadataStore
        {
            Seed = ExistingMetadata(createdAt: "2020-01-01T00:00:00.0000000+00:00"),
        };
        var service = Service(reader, mutation, metadata);

        var report = await service.ApplyAsync(Request(force: true), default);

        Assert.Equal("updated", report.Status);
        Assert.Equal(["arm.upsert"], mutation.Calls);
        Assert.Equal("2020-01-01T00:00:00.0000000+00:00", Assert.Single(metadata.Writes).CreatedAt);
    }

    [Fact]
    public async Task ForceConflict_ThrowsNamingConflict_BeforeMutating()
    {
        var reader = new FakeAbsStateReader
        {
            ExistingBot = MatchingBot() with { AppId = "22222222-2222-2222-2222-222222222222" },
        };
        var mutation = new RecordingAbsMutationPort();
        var service = Service(reader, mutation, new InMemoryConnectionMetadataStore());

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            service.ApplyAsync(Request(force: true), default));

        Assert.Equal("naming-conflict", ex.ErrorKind);
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.StartsWith("Cannot apply: botService at", ex.Message);
        Assert.Empty(mutation.Calls);
    }

    [Fact]
    public async Task ForceProviderNotRegistered_ThrowsProviderError_BeforeMutating()
    {
        var reader = new FakeAbsStateReader { ProviderRegistrationState = "NotRegistered" };
        var mutation = new RecordingAbsMutationPort();
        var service = Service(reader, mutation, new InMemoryConnectionMetadataStore());

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            service.ApplyAsync(Request(force: true), default));

        Assert.Equal("provider-not-registered", ex.ErrorKind);
        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.Empty(mutation.Calls);
    }

    [Fact]
    public async Task MissingContext_ThrowsBeforeAnySeam()
    {
        var mutation = new RecordingAbsMutationPort();
        var service = Service(new FakeAbsStateReader(), mutation, new InMemoryConnectionMetadataStore());

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            service.ApplyAsync(new AbsProvisioningRequest { Force = true }, default));

        Assert.Equal("missing-context", ex.ErrorKind);
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Empty(mutation.Calls);
    }

    private static AbsApplyService Service(
        FakeAbsStateReader reader,
        RecordingAbsMutationPort mutation,
        InMemoryConnectionMetadataStore metadata) =>
        new(reader, mutation, metadata);

    private static AbsBotSnapshot MatchingBot() => new()
    {
        BotResourceId = BotResourceId,
        AppId = SuppliedAppId,
        MsaAppType = "SingleTenant",
        MessagingEndpoint = Endpoint,
        Sku = "F0",
    };

    private static ConnectionMetadata ExistingMetadata(string createdAt) => new()
    {
        SchemaVersion = 1,
        SubscriptionId = "sub-contoso",
        ResourceGroup = "rg-agenthost",
        BotName = "agenthost-bot",
        BotResourceId = BotResourceId,
        AppId = SuppliedAppId,
        MsaAppType = "SingleTenant",
        IdentityMode = "ClientSecret",
        Endpoint = Endpoint,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static AbsProvisioningRequest Request(bool force) => new()
    {
        Tenant = "tenant-contoso",
        Subscription = "sub-contoso",
        ResourceGroup = "rg-agenthost",
        Location = "westus",
        BotName = "agenthost-bot",
        Endpoint = Endpoint,
        MsaAppId = SuppliedAppId,
        Force = force,
    };
}
