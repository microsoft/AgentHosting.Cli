using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Environment inventory, selection persistence, and tenant binding — all against deterministic fakes.
/// Nothing here acquires a live credential, requests consent, or opens a browser.
/// </summary>
public sealed class EnvironmentSelectionServiceTests
{
    private static readonly string[] EnvOneThenTwo = ["env-1", "env-2"];
    [Fact]
    public async Task List_ReturnsInventory_AndMarksTheActiveEnvironment()
    {
        var client = new FakeEnvironmentClient(
            EnvironmentFixtures.Environment("env-1", "Contoso Dev"),
            EnvironmentFixtures.Environment("env-2", "Contoso Test", endpoint: "https://env-2.api.powerplatform.com/"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await service.SelectAsync("env-2", CancellationToken.None);
        var result = await service.ListAsync(CancellationToken.None);

        Assert.Equal("listed", result.Status);
        Assert.Equal(EnvOneThenTwo, result.Environments.Select(e => e.EnvironmentId).ToArray());
        Assert.False(result.Environments[0].IsActive);
        Assert.True(result.Environments[1].IsActive);
    }

    [Fact]
    public async Task List_WithoutASignedInAccount_FailsWithNotSignedIn_AndReadsNoInventory()
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1"));
        var service = EnvironmentFixtures.Service(
            client,
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedOut());

        var error = await Assert.ThrowsAsync<NotSignedInException>(
            () => service.ListAsync(CancellationToken.None));

        Assert.Equal(ExitCodes.NotSignedIn, error.ExitCode);
        Assert.Equal(0, client.ListCalls);
    }

    [Fact]
    public async Task Select_CarriesTheDataverseInstanceUrlIntoTheSelection()
    {
        var client = new FakeEnvironmentClient(
            EnvironmentFixtures.Environment("env-1", instanceUrl: "https://contoso.crm.dynamics.com/"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await service.SelectAsync("env-1", CancellationToken.None);

        Assert.Equal("https://contoso.crm.dynamics.com/", selection.Active?.InstanceUrl);
    }

    [Fact]
    public async Task Select_PersistsTheSelection_SoItSurvivesAcrossCommands()
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1", "Contoso Dev"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var profiles = InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant);

        var result = await EnvironmentFixtures.Service(client, selection, profiles)
            .SelectAsync("env-1", CancellationToken.None);

        Assert.Equal("selected", result.Status);
        Assert.Equal("env-1", result.EnvironmentId);
        Assert.Equal(EnvironmentFixtures.SelectedAt, result.SelectedAt);

        // A brand-new service instance (a later command, a later process) sees the same selection.
        var laterCommand = EnvironmentFixtures.Service(client, selection, profiles);
        var active = await laterCommand.RequireActiveAsync(CancellationToken.None);

        Assert.Equal("env-1", active.EnvironmentId);
        Assert.Equal(EnvironmentFixtures.Audience, active.ApiAudience);
    }

    [Fact]
    public async Task Select_ResolvesAgainstTheLiveInventory_NotTheTypedId()
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAsync<EnvironmentNotFoundException>(
            () => service.SelectAsync("env-does-not-exist", CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Equal("environment-not-found", error.ErrorKind);
        Assert.Equal(1, client.ListCalls);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task Select_RejectsAnEnvironmentInAnotherTenant_AndPersistsNothing()
    {
        var client = new FakeEnvironmentClient(
            EnvironmentFixtures.Environment("env-other", "Fabrikam Dev", EnvironmentFixtures.FabrikamTenant));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.SelectAsync("env-other", CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Equal("tenant-mismatch", error.ErrorKind);
        Assert.Contains(EnvironmentFixtures.FabrikamTenant, error.Message);
        Assert.Equal(0, selection.WriteCalls);
    }

    [Fact]
    public async Task Select_WhenTheProfileHasNoTenant_FailsClosed()
    {
        // A guard that disables itself when it lacks information is not a guard. AuthProfile.TenantId
        // is nullable and comes from a nullable MSAL value, so this state is reachable in practice.
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", tenantId: null));

        await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.SelectAsync("env-1", CancellationToken.None));

        Assert.Equal(0, selection.WriteCalls);
    }

    [Fact]
    public async Task Select_WhenTheInventoryReportsNoTenant_FailsClosed()
    {
        var client = new FakeEnvironmentClient(
            EnvironmentFixtures.Environment("env-1", tenantId: string.Empty));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.SelectAsync("env-1", CancellationToken.None));

        Assert.Equal(0, selection.WriteCalls);
    }

    [Fact]
    public async Task ActiveEnvironment_IsRevalidatedOnReadBack_NotJustOnWrite()
    {
        // Persistence crosses a process boundary. A selection written by an older build, hand-edited,
        // or tampered with on disk must not be trusted merely because it was trusted when written.
        var selection = new InMemoryEnvironmentSelectionStore();
        await selection.SetActiveAsync(
            new ActiveEnvironment
            {
                EnvironmentId = "env-tampered",
                DisplayName = "Tampered",
                TenantId = EnvironmentFixtures.ContosoTenant,
                ApiEndpoint = new Uri("https://attacker.example.com/"),
                ApiAudience = EnvironmentFixtures.Audience,
                SelectedAt = EnvironmentFixtures.SelectedAt,
            },
            CancellationToken.None);

        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await Assert.ThrowsAsync<EndpointTrustException>(
            () => service.RequireActiveAsync(CancellationToken.None));
        await Assert.ThrowsAsync<EndpointTrustException>(
            () => service.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Select_RejectsAnUntrustedEndpoint_AndPersistsNothing()
    {
        var client = new FakeEnvironmentClient(
            EnvironmentFixtures.Environment("env-evil", endpoint: "https://attacker.example.com/"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAsync<EndpointTrustException>(
            () => service.SelectAsync("env-evil", CancellationToken.None));

        Assert.Equal("endpoint-untrusted", error.ErrorKind);
        Assert.Equal(0, selection.WriteCalls);
    }

    [Fact]
    public async Task ResolveForCommand_WithExplicitEnvironment_ValidatesWithoutPersisting()
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-2"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var resolved = await service.ResolveForCommandAsync("env-2", CancellationToken.None);

        Assert.Equal("env-2", resolved.EnvironmentId);
        Assert.Equal(1, client.ListCalls);
        Assert.Equal(0, selection.WriteCalls);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task ResolveForCommand_WithoutExplicitEnvironment_UsesTheActiveSelection()
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1"));
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            client,
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));
        await service.SelectAsync("env-1", CancellationToken.None);

        var resolved = await service.ResolveForCommandAsync(null, CancellationToken.None);

        Assert.Equal("env-1", resolved.EnvironmentId);
        Assert.Equal(1, client.ListCalls);
        Assert.Equal(1, selection.WriteCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveForCommand_WithABlankEnvironmentId_NamesTheOptionRatherThanACommand(string environmentId)
    {
        var client = new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1"));
        var service = EnvironmentFixtures.Service(
            client,
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAsync<UsageException>(() => service.ResolveForCommandAsync(environmentId, CancellationToken.None));

        Assert.StartsWith("--environment-id ", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("agent create", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.ListCalls);
    }

    [Fact]
    public async Task ResolveForCommand_WithCrossTenantEnvironment_RejectsWithoutPersisting()
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                EnvironmentFixtures.Environment(
                    "env-other",
                    "Fabrikam Dev",
                    EnvironmentFixtures.FabrikamTenant)),
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(() =>
            service.ResolveForCommandAsync("env-other", CancellationToken.None));

        Assert.Equal(0, selection.WriteCalls);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task ResolveForCommand_WithUntrustedEndpoint_RejectsWithoutPersisting()
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                EnvironmentFixtures.Environment(
                    "env-evil",
                    endpoint: "https://attacker.example.com/")),
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            service.ResolveForCommandAsync("env-evil", CancellationToken.None));

        Assert.Equal(0, selection.WriteCalls);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task RequireActive_WithoutASelection_FailsClearly()
    {
        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAsync<EnvironmentNotSelectedException>(
            () => service.RequireActiveAsync(CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Contains("msagent env select", error.Remediation!);
    }

    [Fact]
    public async Task RequireActive_AfterASameTenantAccountChange_KeepsTheSelectionUsable()
    {
        var (service, selection, profiles) = await SelectedEnvironmentAsync();

        // A read never discards a selection; only an account or cloud change does.
        await profiles.UpsertAsync(
            new AuthProfile
            {
                AccountKey = "second-key",
                Username = "other@contoso.com",
                TenantId = EnvironmentFixtures.ContosoTenant,
            },
            CancellationToken.None);

        var active = await service.RequireActiveAsync(CancellationToken.None);

        Assert.Equal("env-1", active.EnvironmentId);
        Assert.NotNull(selection.Active);
    }

    [Fact]
    public async Task RequireActive_AfterACrossTenantLogin_Rejects_AndKeepsTheSelection()
    {
        // The tenant was checked when the selection was written, and then the account changed under
        // it. `msagent auth login --tenant` activates the new profile without touching the selection, so a
        // write-time check alone would leave the next command pointing at another tenant.
        var (service, selection, profiles) = await SelectedEnvironmentAsync();

        await profiles.UpsertAsync(
            new AuthProfile
            {
                AccountKey = "fabrikam-key",
                Username = "op@fabrikam.com",
                TenantId = EnvironmentFixtures.FabrikamTenant,
            },
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.RequireActiveAsync(CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Equal("tenant-mismatch", error.ErrorKind);
        Assert.Contains(EnvironmentFixtures.ContosoTenant, error.Message);
        Assert.Contains(EnvironmentFixtures.FabrikamTenant, error.Message);
        Assert.Contains($"msagent auth login --tenant {EnvironmentFixtures.ContosoTenant}", error.Remediation!);

        // Rejected, never cleared: forgetting the selection would send the next command somewhere the
        // operator did not choose, and would destroy the record of what was chosen.
        Assert.Equal("env-1", selection.Active!.EnvironmentId);
    }

    [Fact]
    public async Task RequireActive_AfterASwitchToAnotherTenant_Rejects()
    {
        var (service, _, profiles) = await SelectedEnvironmentAsync();

        await profiles.UpsertAsync(
            new AuthProfile
            {
                AccountKey = "fabrikam-key",
                Username = "op@fabrikam.com",
                TenantId = EnvironmentFixtures.FabrikamTenant,
            },
            CancellationToken.None);
        await profiles.SetActiveAsync("fabrikam-key", CancellationToken.None);

        await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.RequireActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequireActive_WhenTheActiveProfileHasNoTenant_FailsClosed()
    {
        var (service, _, profiles) = await SelectedEnvironmentAsync();

        await profiles.UpsertAsync(
            new AuthProfile { AccountKey = "blank-key", Username = "op@unknown", TenantId = null },
            CancellationToken.None);

        // A comparison that passes because it lacks information is not a check.
        await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.RequireActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequireActive_WhenTheAccountIsGone_ReportsNotSignedIn()
    {
        var (service, selection, profiles) = await SelectedEnvironmentAsync();

        await profiles.ClearAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<NotSignedInException>(
            () => service.RequireActiveAsync(CancellationToken.None));

        Assert.Equal(ExitCodes.NotSignedIn, error.ExitCode);
        Assert.NotNull(selection.Active);
    }

    [Fact]
    public async Task ActiveBinding_StillReportsAnUnboundSelection_SoStatusCanExplainIt()
    {
        var (service, _, profiles) = await SelectedEnvironmentAsync();

        var bound = await service.GetActiveBindingAsync(CancellationToken.None);
        Assert.True(bound!.IsBound);
        Assert.True(bound.IsSignedIn);

        await profiles.UpsertAsync(
            new AuthProfile
            {
                AccountKey = "fabrikam-key",
                Username = "op@fabrikam.com",
                TenantId = EnvironmentFixtures.FabrikamTenant,
            },
            CancellationToken.None);

        var unbound = await service.GetActiveBindingAsync(CancellationToken.None);

        Assert.False(unbound!.IsBound);
        Assert.Equal("env-1", unbound.Environment.EnvironmentId);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, unbound.Environment.TenantId);
        Assert.Equal(EnvironmentFixtures.FabrikamTenant, unbound.AccountTenantId);
    }

    /// <summary>A Contoso environment selected by a signed-in Contoso account.</summary>
    private static Task<(EnvironmentSelectionService Service, InMemoryEnvironmentSelectionStore Selection, InMemoryAuthProfileStore Profiles)>
        SelectedEnvironmentAsync() => EnvironmentFixtures.SelectedEnvironmentAsync();
}

/// <summary>Discarding environment selections when the operator changes account or cloud.</summary>
public sealed class EnvironmentSelectionClearingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ah-env-clear-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenAnotherAccountInTheSameTenantBecomesActive_TheSelectionIsDiscarded()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(accountKey: "other-key", username: "other@contoso.com"),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task WhenAnotherAccountInAnotherTenantBecomesActive_TheSelectionIsDiscarded()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(
                accountKey: "fabrikam-key",
                username: "op@fabrikam.com",
                tenantId: EnvironmentFixtures.FabrikamTenant),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task WhenTheSameAccountMovesToAnotherCloud_TheSelectionIsDiscarded()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(ring: PowerPlatformApiRing.Test),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task WhenNoAccountWasActive_TheSelectionIsDiscarded()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            null,
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task WhenTheSameAccountSignsInAgain_TheSelectionSurvives()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.False(discarded);
        Assert.Equal("env-1", selection.Active!.EnvironmentId);
    }

    [Fact]
    public async Task TheIdentityComparisonIgnoresCase()
    {
        var (service, selection, _) = await SelectedEnvironmentAsync();

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(accountKey: "op@contoso.com-key", tenantId: "aaaaaaaa-1111-2222-3333-444444444444"),
            EnvironmentFixtures.Account(accountKey: "OP@CONTOSO.COM-KEY", tenantId: "AAAAAAAA-1111-2222-3333-444444444444"),
            CancellationToken.None);

        Assert.False(discarded);
        Assert.NotNull(selection.Active);
    }

    [Fact]
    public async Task WithNothingSelected_NothingIsDiscarded()
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            null,
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.False(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task ASelectionThisCloudWouldNotTrust_IsStillDiscarded()
    {
        var selection = new InMemoryEnvironmentSelectionStore();
        await selection.SetActiveAsync(
            EnvironmentFixtures.Selection(
                environmentId: "env-elsewhere",
                displayName: "Another Cloud",
                tenantId: EnvironmentFixtures.FabrikamTenant,
                endpoint: "https://api.powerplatform.microsoft.us/"),
            CancellationToken.None);

        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            selection,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            null,
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(selection.Active);
    }

    [Fact]
    public async Task AnUnreadableSelection_IsDiscardedRatherThanBlockingTheAccountChange()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "environment-selection.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var store = new FileEnvironmentSelectionStore(path);
        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            store,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            null,
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(await store.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EveryCloudsSelectionIsDiscarded_NotOnlyTheOneThisProcessTargets()
    {
        var (production, test) = await SelectedInEveryCloudAsync();

        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            production,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant),
            selectionScopes: Scopes());

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(accountKey: "other-key", username: "other@contoso.com"),
            CancellationToken.None);

        Assert.True(discarded);
        Assert.Null(await production.GetActiveAsync(CancellationToken.None));
        Assert.Null(await test.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheIdentityIsUnchanged_NoCloudsSelectionIsTouched()
    {
        var (production, test) = await SelectedInEveryCloudAsync();

        var service = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            production,
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant),
            selectionScopes: Scopes());

        var discarded = await service.ClearSelectionsOnIdentityChangeAsync(
            EnvironmentFixtures.Account(),
            EnvironmentFixtures.Account(),
            CancellationToken.None);

        Assert.False(discarded);
        Assert.NotNull(await production.GetActiveAsync(CancellationToken.None));
        Assert.NotNull(await test.GetActiveAsync(CancellationToken.None));
    }

    private EnvironmentSelectionScopes Scopes() =>
        EnvironmentSelectionScopes.ForConfigDirectory(_directory, "environment-selection-csharp.json");

    private async Task<(FileEnvironmentSelectionStore Production, FileEnvironmentSelectionStore Test)> SelectedInEveryCloudAsync()
    {
        Directory.CreateDirectory(_directory);

        var production = RingStore(PowerPlatformApiRing.Production);
        var test = RingStore(PowerPlatformApiRing.Test);

        await production.SetActiveAsync(EnvironmentFixtures.Selection(), CancellationToken.None);
        await test.SetActiveAsync(
            EnvironmentFixtures.Selection(environmentId: "env-test", displayName: "Contoso Test"),
            CancellationToken.None);

        return (production, test);
    }

    private FileEnvironmentSelectionStore RingStore(PowerPlatformApiRing ring) =>
        new(Path.Combine(
            _directory,
            PowerPlatformApiRingSelection.RingScopedName("environment-selection-csharp.json", ring)));

    private static Task<(EnvironmentSelectionService Service, InMemoryEnvironmentSelectionStore Selection, InMemoryAuthProfileStore Profiles)>
        SelectedEnvironmentAsync() => EnvironmentFixtures.SelectedEnvironmentAsync();
}

/// <summary>
/// Endpoint-trust coverage for recognized Power Platform hosts and fail-closed rejection.
/// </summary>
public sealed class PowerPlatformCloudMapTests
{
    private static readonly PowerPlatformCloudMap Map = PowerPlatformCloudMap.ForCloud(CloudInstance.Public);

    [Theory]
    [InlineData("https://attacker.example.com")]
    [InlineData("https://api.powerplatform.com.attacker.example")]
    [InlineData("https://notapi.powerplatform.com")]
    [InlineData("https://api.powerplatform.com.evil.co.uk/environmentmanagement/environments")]
    public void CloudMap_RejectsUntrustedHttpsHosts(string endpoint) =>
        Assert.False(Map.IsTrusted(new Uri(endpoint)));

    [Theory]
    [InlineData("https://api.powerplatform.com")]
    [InlineData("https://api.powerplatform.com/environmentmanagement/environments")]
    [InlineData("https://env-1.api.powerplatform.com/")]
    [InlineData("https://ENV-1.API.POWERPLATFORM.COM/")]
    public void CloudMap_TrustsThePowerPlatformApiHostAndItsSubdomains(string endpoint) =>
        Assert.True(Map.IsTrusted(new Uri(endpoint)));

    [Theory]
    [InlineData("http://api.powerplatform.com")]
    [InlineData("https://api.powerplatform.com:8443")]
    [InlineData("https://user:pass@api.powerplatform.com")]
    public void CloudMap_RejectsNonHttpsNonDefaultPortAndEmbeddedCredentials(string endpoint) =>
        Assert.False(Map.IsTrusted(new Uri(endpoint)));

    [Fact]
    public void RequireTrusted_NamesTheContextAndTheAllowlist_WithoutEchoingThePath()
    {
        var error = Assert.Throws<EndpointTrustException>(() =>
            Map.RequireTrusted(new Uri("https://attacker.example.com/secret-path"), "environment 'env-1'"));

        Assert.Contains("environment 'env-1'", error.Message);
        Assert.Contains("api.powerplatform.com", error.Message);
        Assert.DoesNotContain("secret-path", error.Message);
    }

    [Fact]
    public void EveryCloud_ResolvesToANonEmptyAllowlist()
    {
        foreach (var cloud in Enum.GetValues<CloudInstance>())
        {
            var map = PowerPlatformCloudMap.ForCloud(cloud);
            Assert.Equal(cloud, map.Cloud);
            Assert.NotEmpty(map.TrustedHostSuffixes);
            Assert.NotEmpty(map.TrustedAgentManagementHostSuffixes);
        }
    }

    [Theory]
    [InlineData("https://powervamg.us-il301.gateway.prod.island.powerapps.com/", true)]
    [InlineData("https://powervamg.us-il106.gateway.test.island.powerapps.com/", true)]
    [InlineData("https://island.powerapps.com/", true)]
    [InlineData("https://notisland.powerapps.com/", false)]
    [InlineData("https://island.powerapps.com.attacker.example/", false)]
    [InlineData("https://api.powerplatform.com/", false)]
    [InlineData("http://island.powerapps.com/", false)]
    [InlineData("https://island.powerapps.com:8443/", false)]
    public void IsTrustedAgentManagementGateway_AcceptsOnlyTheGatewayHostsTheCloudServes(string endpoint, bool expected) =>
        Assert.Equal(expected, Map.IsTrustedAgentManagementGateway(new Uri(endpoint)));

    [Fact]
    public void IsTrustedAgentManagementGateway_RejectsANullUri() => Assert.False(Map.IsTrustedAgentManagementGateway(null));

    [Fact]
    public void RequireTrustedAgentManagementGateway_NamesTheContextAndTheAllowlist_WithoutEchoingThePath()
    {
        var error = Assert.Throws<EndpointTrustException>(() =>
            Map.RequireTrustedAgentManagementGateway(new Uri("https://attacker.example.com/secret-path"), "environment 'env-1' Copilot Studio agent-management"));

        Assert.Contains("environment 'env-1' Copilot Studio agent-management", error.Message);
        Assert.Contains("island.powerapps.com", error.Message);
        Assert.DoesNotContain("secret-path", error.Message);
    }

    /// <summary>
    /// The host normalization (<c>Trim</c> then <c>TrimEnd('.')</c> then a label-aligned suffix
    /// comparison) is the single control the whole change rests on, so its edge behavior is pinned
    /// rather than left to inspection.
    /// </summary>
    [Theory]
    [InlineData("api.powerplatform.com", true)]
    [InlineData("env-1.api.powerplatform.com", true)]
    [InlineData("api.powerplatform.com.", true)]      // FQDN trailing dot names the same host
    [InlineData("api.powerplatform.com..", true)]
    [InlineData("  api.powerplatform.com  ", true)]
    [InlineData("API.PowerPlatform.COM", true)]
    [InlineData("powerplatform.com", false)]          // parent, not the allowlisted host
    [InlineData("notapi.powerplatform.com", false)]
    [InlineData("xapi.powerplatform.com", false)]
    [InlineData("api.powerplatform.com.attacker.example", false)]
    [InlineData("api-powerplatform.com", false)]
    [InlineData("[::1]", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("xn--pi-via.powerplatform.com", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".api.powerplatform.com", true)]      // leading dot still ends with ".<suffix>"
    public void IsTrustedHost_PinsTheNormalizationBehavior(string host, bool expected) =>
        Assert.Equal(expected, Map.IsTrustedHost(host));

    [Theory]
    [InlineData("https://[::1]/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://xn--pi-via.powerplatform.com/")]
    public void IsTrusted_RejectsLiteralAndLookalikeHosts(string endpoint) =>
        Assert.False(Map.IsTrusted(new Uri(endpoint)));

    [Fact]
    public void IsTrusted_AcceptsTheFqdnTrailingDotForm() =>
        Assert.True(Map.IsTrusted(new Uri("https://api.powerplatform.com./")));

    [Fact]
    public void IsTrusted_RejectsANullOrRelativeUri()
    {
        Assert.False(Map.IsTrusted(null));
        Assert.False(Map.IsTrusted(new Uri("/environments", UriKind.Relative)));
    }

    [Theory]
    [InlineData("https://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm4.dynamics.com/")]
    [InlineData("https://contoso.crm16.dynamics.com/")]
    [InlineData("https://org-with-dashes.crm.dynamics.com/")]
    [InlineData("https://CONTOSO.CRM.DYNAMICS.COM/")]
    [InlineData("https://contoso.crm.dynamics.com./")]
    public void IsTrustedDataverseOrganization_AcceptsACommercialOrganization(string organization) =>
        Assert.True(Map.IsTrustedDataverseOrganization(new Uri(organization)));

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://contoso.crm.dynamics.com.attacker.example/")]
    [InlineData("https://crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com.co/")]
    [InlineData("https://contoso.notcrm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.cn/")]
    [InlineData("https://contoso.crm.microsoftdynamics.us/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    [InlineData("https://127.0.0.1/")]
    public void IsTrustedDataverseOrganization_RejectsAnOrganizationTheCloudDoesNotServe(string organization) =>
        Assert.False(Map.IsTrustedDataverseOrganization(new Uri(organization)));

    [Fact]
    public void IsTrustedDataverseOrganization_RejectsEmbeddedCredentials() =>
        Assert.False(Map.IsTrustedDataverseOrganization(
            new UriBuilder("https", "contoso.crm.dynamics.com") { UserName = "attacker", Password = "secret" }.Uri));

    [Fact]
    public void IsTrustedDataverseOrganization_RejectsANullOrRelativeUri()
    {
        Assert.False(Map.IsTrustedDataverseOrganization(null));
        Assert.False(Map.IsTrustedDataverseOrganization(new Uri("/organization", UriKind.Relative)));
    }

    [Theory]
    [InlineData(CloudInstance.UsGov, "https://contoso.crm.microsoftdynamics.us/")]
    [InlineData(CloudInstance.UsGovHigh, "https://contoso.crm9.microsoftdynamics.us/")]
    [InlineData(CloudInstance.UsGovDod, "https://contoso.crm.appsplatform.us/")]
    [InlineData(CloudInstance.China, "https://contoso.crm.dynamics.cn/")]
    public void IsTrustedDataverseOrganization_AcceptsASovereignOrganizationOnItsOwnCloud(CloudInstance cloud, string organization) =>
        Assert.True(PowerPlatformCloudMap.ForCloud(cloud).IsTrustedDataverseOrganization(new Uri(organization)));

    [Theory]
    [InlineData(CloudInstance.UsGov)]
    [InlineData(CloudInstance.UsGovHigh)]
    [InlineData(CloudInstance.UsGovDod)]
    [InlineData(CloudInstance.China)]
    public void IsTrustedDataverseOrganization_RejectsACommercialOrganizationOnASovereignCloud(CloudInstance cloud) =>
        Assert.False(PowerPlatformCloudMap.ForCloud(cloud).IsTrustedDataverseOrganization(new Uri("https://contoso.crm.dynamics.com/")));

    [Fact]
    public void UntrustedDataverseOrganization_NamesTheOriginAndEnvironment_WithoutEchoingThePath()
    {
        var error = Map.UntrustedDataverseOrganization(new Uri("https://evil.example.com/secret-path"), "env-1");

        Assert.Contains("env-1", error.Message);
        Assert.Contains("https://evil.example.com", error.Message);
        Assert.DoesNotContain("secret-path", error.Message);
    }
}

/// <summary>
/// The file-backed selection store: round-trip, atomic replacement, and the deliberate refusal to
/// report a corrupt selection as "nothing selected".
/// </summary>
public sealed class FileEnvironmentSelectionStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ah-env-selection-" + Guid.NewGuid().ToString("N"));

    private string SelectionPath => Path.Combine(_directory, "environment-selection.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ActiveEnvironment Sample() => EnvironmentFixtures.Selection();

    [Fact]
    public async Task MissingFile_IsNothingSelected_NotAnError()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);

        Assert.Null(await store.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetActive_RoundTripsThroughDisk()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);
        await store.SetActiveAsync(Sample(), CancellationToken.None);

        // A second instance reads what the first wrote — the persistence guarantee `msagent env select`
        // relies on across processes.
        var reloaded = await new FileEnvironmentSelectionStore(SelectionPath)
            .GetActiveAsync(CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal("env-1", reloaded.EnvironmentId);
        Assert.Equal("Contoso Dev", reloaded.DisplayName);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, reloaded.TenantId);
        Assert.Equal(new Uri("https://env-1.api.powerplatform.com/"), reloaded.ApiEndpoint);
        Assert.Equal(EnvironmentFixtures.SelectedAt, reloaded.SelectedAt);
    }

    [Fact]
    public async Task PersistedFile_ContainsNoTokenMaterial()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);
        await store.SetActiveAsync(Sample(), CancellationToken.None);

        var raw = await File.ReadAllTextAsync(SelectionPath);

        foreach (var forbidden in new[] { "token", "bearer", "secret", "authorization", "sig=", "eyJ" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ClearRemovesTheSelection()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);
        await store.SetActiveAsync(Sample(), CancellationToken.None);
        await store.ClearAsync(CancellationToken.None);

        Assert.Null(await store.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptFile_IsATypedError_NotASilentlyForgottenSelection()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SelectionPath, "{ not json");

        var store = new FileEnvironmentSelectionStore(SelectionPath);

        var error = await Assert.ThrowsAsync<AgentHostingStateException>(
            () => store.GetActiveAsync(CancellationToken.None));

        Assert.Equal(ExitCodes.Failure, error.ExitCode);
    }

    [Fact]
    public async Task UnsupportedSchemaVersion_IsATypedError()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SelectionPath, """{"schemaVersion":99,"active":null}""");

        var store = new FileEnvironmentSelectionStore(SelectionPath);

        await Assert.ThrowsAsync<AgentHostingStateException>(
            () => store.GetActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetActive_RoundTripsTheDataverseInstanceUrl()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);
        await store.SetActiveAsync(
            Sample() with { InstanceUrl = "https://contoso.crm.dynamics.com/" },
            CancellationToken.None);

        var reloaded = await new FileEnvironmentSelectionStore(SelectionPath)
            .GetActiveAsync(CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal("https://contoso.crm.dynamics.com/", reloaded.InstanceUrl);
    }

    [Fact]
    public async Task SelectionWrittenWithoutAnInstanceUrl_StillLoads()
    {
        var store = new FileEnvironmentSelectionStore(SelectionPath);
        await store.SetActiveAsync(Sample(), CancellationToken.None);

        var raw = await File.ReadAllTextAsync(SelectionPath);
        Assert.DoesNotContain("\"instanceUrl\": \"", raw, StringComparison.OrdinalIgnoreCase);

        var reloaded = await new FileEnvironmentSelectionStore(SelectionPath)
            .GetActiveAsync(CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal("env-1", reloaded.EnvironmentId);
        Assert.Null(reloaded.InstanceUrl);
    }
}

/// <summary>
/// The Power Platform API inventory adapter, driven entirely by a stub handler: no network, no MSAL,
/// no live token.
/// </summary>
public sealed class PowerPlatformApiEnvironmentClientTests
{
    private static readonly string[] PowerPlatformApiResource = ["https://api.powerplatform.com/"];
    private const string InventoryBody = """
        {
          "value": [
            {
              "id": "env-1",
              "properties": {
                "displayName": "Contoso Dev",
                "tenantId": "11111111-1111-1111-1111-111111111111",
                "apiEndpoint": "https://env-1.api.powerplatform.com/",
                "geoLongName": "Europe"
              }

            }
          ]
        }
        """;

    private static PowerPlatformApiEnvironmentClient Create(
        StubHttpMessageHandler handler,
        StubTokenProvider tokenProvider,
        PowerPlatformApiTargetConfig? target = null) =>
        new(
            new HttpClient(handler),
            tokenProvider,
            target);

    /// <summary>
    /// A token provider returning a fabricated, unsigned JWT that satisfies the inventory claim
    /// contract. Not a credential: nothing issued it and no service accepts it.
    /// </summary>
    private static StubTokenProvider ValidToken(string audience = EnvironmentFixtures.Audience) => new()
    {
        AccessToken = FakeJwt.Create(
            audience: audience,
            scopes: PowerPlatformApiPermissions.EnvironmentsRead),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListEnvironments_RejectsATokenLackingEnvironmentsRead_BeforeCallingTheService()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody));
        var client = Create(handler, new StubTokenProvider
        {
            AccessToken = FakeJwt.Create(scopes: PowerPlatformApiPermissions.AppsRead),
        });

        var error = await Assert.ThrowsAsync<TokenClaimException>(
            () => client.ListEnvironmentsAsync(CancellationToken.None));

        Assert.Equal(TokenClaimFailure.MissingPermission, error.Failure);

        // Nothing was sent: the token is rejected before the bearer header is attached.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListEnvironments_RejectsATokenForTheWrongAudience_BeforeCallingTheService()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody));
        var client = Create(handler, new StubTokenProvider
        {
            AccessToken = FakeJwt.Create(audience: "https://management.azure.com/"),
        });

        var error = await Assert.ThrowsAsync<TokenClaimException>(
            () => client.ListEnvironmentsAsync(CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongAudience, error.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListEnvironments_RequestsTheExactPpapiDefaultScope_Silently()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody));
        var token = ValidToken();
        var client = Create(handler, token);

        await client.ListEnvironmentsAsync(CancellationToken.None);

        // PAC's double-slash //.default form, applied to the Power Platform API audience.
        Assert.Equal("https://api.powerplatform.com//.default", client.InventoryScope);
        Assert.Equal(PowerPlatformApiResource, token.RequestedResources.ToArray());

        // Silent acquisition only: the inventory read never asks for a tenant-scoped interactive flow.
        Assert.Equal(new string?[] { null }, token.RequestedTargetTenants.ToArray());
    }

    [Fact]
    public async Task ListEnvironments_ParsesTheInventory_AndCallsTheTrustedGlobalEndpoint()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody));
        var client = Create(handler, ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        var environment = Assert.Single(environments);
        Assert.Equal("env-1", environment.EnvironmentId);
        Assert.Equal("Contoso Dev", environment.DisplayName);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, environment.TenantId);
        Assert.Equal(new Uri("https://env-1.api.powerplatform.com/"), environment.ApiEndpoint);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.powerplatform.com", request.Uri.Host);
        Assert.Contains("api-version=", request.Uri.Query);
    }

    [Fact]
    public async Task ListEnvironments_TestTarget_UsesThePairedEndpointAudienceAndTrustMap()
    {
        const string testInventory = """
            {
              "value": [
                {
                  "id": "env-test",
                  "properties": {
                    "displayName": "Contoso Test",
                    "tenantId": "11111111-1111-1111-1111-111111111111",
                    "apiEndpoint": "https://env-test.environment.api.test.powerplatform.com/"
                  }
                }
              ]
            }
            """;
        var target = PowerPlatformApiTargetConfig.Test;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, testInventory));
        var token = ValidToken(target.Audience);
        var client = Create(handler, token, target);

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("https://api.test.powerplatform.com//.default", client.InventoryScope);
        Assert.Equal(new[] { target.Audience }, token.RequestedResources.ToArray());
        Assert.Equal("api.test.powerplatform.com", Assert.Single(handler.Requests).Uri.Host);
        Assert.Equal(
            "env-test.environment.api.test.powerplatform.com",
            Assert.Single(environments).ApiEndpoint.Host);
        Assert.True(PowerPlatformCloudMap.ForTarget(target).IsTrusted(environments[0].ApiEndpoint));
        Assert.False(
            PowerPlatformCloudMap.ForTarget(target).IsTrusted(
                new Uri("https://api.powerplatform.com/")));
    }

    [Fact]
    public async Task ListEnvironments_RejectsAnEnvironmentAdvertisingAnUntrustedEndpoint()
    {
        const string hostile = """
            {"value":[{"id":"env-evil","properties":{"displayName":"Evil","tenantId":"t","apiEndpoint":"https://attacker.example.com/"}}]}
            """;
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, hostile));
        var client = Create(handler, ValidToken());

        await Assert.ThrowsAsync<EndpointTrustException>(
            () => client.ListEnvironmentsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListEnvironments_MapsAForbiddenResponse_WithoutEchoingTheTokenOrBody()
    {
        var handler = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.Forbidden,
            """{"error":{"code":"AuthorizationFailed","message":"secret-diagnostic-detail"}}"""));
        var token = ValidToken();
        var client = Create(handler, token);

        var error = await Assert.ThrowsAsync<TargetRequestException>(
            () => client.ListEnvironmentsAsync(CancellationToken.None));

        Assert.Equal(TargetErrorKind.PermissionDenied, error.Kind);
        Assert.DoesNotContain(token.AccessToken, error.Message);
        Assert.DoesNotContain("secret-diagnostic-detail", error.Message);
    }

    [Fact]
    public async Task ListEnvironments_RejectsAMalformedInventoryResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """{"unexpected":true}"""));
        var client = Create(handler, ValidToken());

        await Assert.ThrowsAsync<TargetRequestException>(
            () => client.ListEnvironmentsAsync(CancellationToken.None));
    }

    [Fact]
    public void JsonSerializedSummaries_CarryNoTokenMaterial()
    {
        var summary = new Core.Operations.EnvironmentSummary
        {
            EnvironmentId = "env-1",
            DisplayName = "Contoso Dev",
            TenantId = EnvironmentFixtures.ContosoTenant,
            ApiEndpoint = "https://env-1.api.powerplatform.com/",
            IsActive = true,
        };

        var json = JsonSerializer.Serialize(summary);

        foreach (var forbidden in new[] { "token", "bearer", "secret", "sig=", "eyJ" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ListEnvironments_ReadsTheDataverseInstanceUrlFromLinkedEnvironmentMetadata()
    {
        const string body = """
            {
              "value": [
                {
                  "id": "env-1",
                  "properties": {
                    "displayName": "Contoso Dev",
                    "tenantId": "11111111-1111-1111-1111-111111111111",
                    "apiEndpoint": "https://env-1.api.powerplatform.com/",
                    "linkedEnvironmentMetadata": { "instanceUrl": "https://contoso.crm.dynamics.com/" }
                  }
                }
              ]
            }
            """;
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("https://contoso.crm.dynamics.com/", Assert.Single(environments).InstanceUrl);
    }

    [Fact]
    public async Task ListEnvironments_PrefersLinkedEnvironmentMetadataOverTheTopLevelUrl()
    {
        const string body = """
            {
              "value": [
                {
                  "id": "env-1",
                  "properties": {
                    "displayName": "Contoso Dev",
                    "tenantId": "11111111-1111-1111-1111-111111111111",
                    "apiEndpoint": "https://env-1.api.powerplatform.com/",
                    "url": "https://fallback.crm.dynamics.com/",
                    "linkedEnvironmentMetadata": { "instanceUrl": "https://linked.crm.dynamics.com/" }
                  }
                }
              ]
            }
            """;
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("https://linked.crm.dynamics.com/", Assert.Single(environments).InstanceUrl);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("instanceUrl")]
    public async Task ListEnvironments_FallsBackToATopLevelInstanceUrlProperty(string propertyName)
    {
        var body = $$"""
            {
              "value": [
                {
                  "id": "env-1",
                  "properties": {
                    "displayName": "Contoso Dev",
                    "tenantId": "11111111-1111-1111-1111-111111111111",
                    "apiEndpoint": "https://env-1.api.powerplatform.com/",
                    "{{propertyName}}": "https://fallback.crm.dynamics.com/"
                  }
                }
              ]
            }
            """;
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("https://fallback.crm.dynamics.com/", Assert.Single(environments).InstanceUrl);
    }

    [Fact]
    public async Task ListEnvironments_WithoutALinkedDataverseOrganization_YieldsANullInstanceUrl()
    {
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Null(Assert.Single(environments).InstanceUrl);
    }

    [Fact]
    public async Task ListEnvironments_RequestsThePowerAppsEnvironmentsRoute()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, InventoryBody));
        var client = Create(handler, ValidToken());

        await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("/powerapps/environments", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    private const string ResourcePathInventoryBody = """
        {
          "value": [
            {
              "id": "/providers/Microsoft.PowerApps/environments/e3f97c4c-413b-ee65-b37e-b0545ce1d527",
              "name": "e3f97c4c-413b-ee65-b37e-b0545ce1d527",
              "properties": {
                "displayName": "HoaDevEnv",
                "apiEndpoint": "https://env-1.api.powerplatform.com/"
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task ListEnvironments_ReportsTheBareEnvironmentIdRatherThanTheResourcePath()
    {
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, ResourcePathInventoryBody)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal("e3f97c4c-413b-ee65-b37e-b0545ce1d527", Assert.Single(environments).EnvironmentId);
    }

    [Fact]
    public async Task ListEnvironments_FallsBackToTheResourcePathWhenNoBareIdIsReported()
    {
        const string body = """
            {"value":[{"id":"/providers/Microsoft.PowerApps/environments/env-1","properties":{"displayName":"Contoso"}}]}
            """;
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        var environment = Assert.Single(environments);
        Assert.Equal("/providers/Microsoft.PowerApps/environments/env-1", environment.EnvironmentId);
        Assert.Equal(new Uri("https://en.v1.environment.api.powerplatform.com/"), environment.ApiEndpoint);
    }

    [Fact]
    public async Task ListEnvironments_TakesTheTenantFromTheTokenWhenTheInventoryOmitsIt()
    {
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, ResourcePathInventoryBody)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal(EnvironmentFixtures.ContosoTenant, Assert.Single(environments).TenantId);
    }

    [Fact]
    public async Task ListEnvironments_PrefersTheTenantTheInventoryReports()
    {
        const string body = """
            {
              "value": [
                {
                  "name": "env-1",
                  "properties": {
                    "displayName": "Contoso",
                    "tenantId": "22222222-2222-2222-2222-222222222222",
                    "apiEndpoint": "https://env-1.api.powerplatform.com/"
                  }
                }
              ]
            }
            """;
        var client = Create(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, body)), ValidToken());

        var environments = await client.ListEnvironmentsAsync(CancellationToken.None);

        Assert.Equal(EnvironmentFixtures.FabrikamTenant, Assert.Single(environments).TenantId);
    }
}
