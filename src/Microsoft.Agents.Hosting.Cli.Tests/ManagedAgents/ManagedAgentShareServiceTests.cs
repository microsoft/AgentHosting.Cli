using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class ManagedAgentShareServiceTests
{
    private const string Owner = "00000000-0000-0000-0000-0000000000aa";
    private const string Ada = "00000000-0000-0000-0000-0000000000bb";
    private const string Support = "00000000-0000-0000-0000-0000000000cc";
    private const string Grace = "00000000-0000-0000-0000-0000000000dd";
    private const string Stranger = "00000000-0000-0000-0000-0000000000ee";
    private const string Bot = "bot-1";

    private static ManagedAgentShareService Create(JsonAgentConfigStore store, FakeAgentSharingPort sharing, IDirectoryPrincipalPort directory, ICopilotCatalogPort? catalog = null, ITenantUserDirectoryPort? tenantDirectory = null, TimeSpan? tenantNamingTimeout = null) =>
        Construct(store, sharing, directory, catalog ?? Catalog(), tenantDirectory ?? new FakeTenantUserDirectoryPort(), tenantNamingTimeout);

    private static ManagedAgentShareService Construct(JsonAgentConfigStore store, FakeAgentSharingPort sharing, IDirectoryPrincipalPort directory, ICopilotCatalogPort catalog, ITenantUserDirectoryPort tenantDirectory, TimeSpan? tenantNamingTimeout = null) =>
        new(
            ManagedAgentFixtures.AgentResolver(store),
            ManagedAgentFixtures.Resolver(
                EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization),
                EnvironmentFixtures.Environment("env-2", instanceUrl: CopilotTestFixtures.DataverseOrganization)),
            catalog,
            ManagedAgentFixtures.Identities(),
            sharing,
            directory,
            tenantDirectory,
            tenantNamingTimeout);

    private static FakeCopilotCatalogPort Catalog(string? resolves = Bot) => new() { ResolveReturns = resolves };

    private static JsonAgentConfigStore Seeded(TemporaryWorkspace workspace)
    {
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = Bot });

        return store;
    }

    private static FakeDirectoryPrincipalPort Directory() => new FakeDirectoryPrincipalPort()
        .WithUser(Owner, "Hoa Nguyen", "hoa@contoso.com")
        .WithUser(Ada, "Ada Lovelace", "ada@contoso.com")
        .WithGroup(Support, "Support");

    private static FakeAgentSharingPort OwnedAgent() =>
        new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner));

    private static Task<ManagedAgentShareShowResult> ShowGraceAndAStrangerAsync(TemporaryWorkspace workspace, FakeTenantUserDirectoryPort tenantDirectory, TimeSpan? tenantNamingTimeout = null, CancellationToken cancellationToken = default) =>
        Create(
            Seeded(workspace),
            new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Grace, AgentSharingRole.Consumer), AgentShareTarget.For(Stranger, AgentSharingRole.Consumer)),
            Directory(),
            tenantDirectory: tenantDirectory.WithUser(Grace, "Grace Hopper", "grace_fabrikam.com#EXT#@contoso.com"),
            tenantNamingTimeout: tenantNamingTimeout).ShowAsync(new ManagedAgentShareShowRequest { AgentId = "agent-1" }, cancellationToken);

    private static void AssertOnlyGraceIsNamed(ManagedAgentShareShowResult result)
    {
        Assert.Equal("Grace Hopper", result.Shares.Single(share => share.ObjectId == Grace).PrincipalName);
        var stranger = result.Shares.Single(share => share.ObjectId == Stranger);
        Assert.Equal(Stranger, stranger.PrincipalName);
        Assert.Equal("Unknown", stranger.PrincipalType);
    }

    [Fact]
    public async Task Share_GrantsUseAccessAndKeepsTheOwner()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal("use", result.Role);
        Assert.Equal("Ada Lovelace", result.Principal!.PrincipalName);
        Assert.Equal(AgentSharingRole.Consumer, sharing.StateOf(Bot).RoleOf(Ada));
        Assert.Equal(AgentSharingRole.Owner, sharing.StateOf(Bot).RoleOf(Owner));
    }

    [Fact]
    public async Task Share_EditGrantsCollaborator()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "edit" },
            CancellationToken.None);

        Assert.Equal("edit", result.Role);
        Assert.Equal(AgentSharingRole.Collaborator, sharing.StateOf(Bot).RoleOf(Ada));
    }

    [Fact]
    public async Task Share_RepeatingTheSameRoleWritesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent().WithTargets(
            Bot,
            AgentShareTarget.For(Owner, AgentSharingRole.Owner),
            AgentShareTarget.For(Ada, AgentSharingRole.Consumer));

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("unchanged", result.Status);
        Assert.False(result.Changed);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Share_ChangingRoleReplacesTheExistingTargetRatherThanAddingOne()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent().WithTargets(
            Bot,
            AgentShareTarget.For(Owner, AgentSharingRole.Owner),
            AgentShareTarget.For(Ada, AgentSharingRole.Consumer));

        await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "edit" },
            CancellationToken.None);

        var written = Assert.Single(sharing.Writes).State;

        Assert.Equal(2, written.Targets.Count);
        Assert.Equal(AgentSharingRole.Collaborator, written.RoleOf(Ada));
    }

    [Fact]
    public async Task Share_SetsOrgWideAccessWhenAsked()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use", ShareWithOrg = true },
            CancellationToken.None);

        Assert.True(result.ShareWithOrg);
        Assert.True(Assert.Single(sharing.Writes).State.ShareWithOrg);
    }

    [Fact]
    public async Task Share_LeavesOrgWideAccessAloneWhenNotAsked()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState
        {
            ShareWithOrg = true,
            Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)],
        });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.True(result.ShareWithOrg);
        Assert.True(Assert.Single(sharing.Writes).State.ShareWithOrg);
    }

    [Fact]
    public async Task Share_TurningOrgWideAccessOffIsAChangeOnItsOwn()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState
        {
            ShareWithOrg = true,
            Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer)],
        });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use", ShareWithOrg = false },
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(Assert.Single(sharing.Writes).State.ShareWithOrg);
    }

    [Fact]
    public async Task Share_TargetingTheOwnerIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "hoa@contoso.com", Role = "edit" },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("share-owner-not-changeable", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("viewer")]
    [InlineData("owner")]
    [InlineData("collaborator")]
    [InlineData("analyst")]
    public async Task Share_AnUnknownRoleIsAUsageError(string? role)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = role },
            CancellationToken.None));

        Assert.Equal("share-role-invalid", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_AcceptsAnObjectIdTheDirectoryDoesNotKnow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        var unknown = "33333333-3333-3333-3333-333333333333";

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = unknown, Role = "use" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal(unknown, result.Principal!.ObjectId);
        Assert.Equal(AgentSharingRole.Consumer, sharing.StateOf(Bot).RoleOf(unknown));
    }

    [Fact]
    public async Task Share_RejectsASignInAddressTheTenantDoesNotKnow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        var tenantDirectory = new FakeTenantUserDirectoryPort();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory(), tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "nobody@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-principal-not-found", failure.ErrorKind);
        Assert.Contains(EnvironmentFixtures.ContosoTenant, failure.Message, StringComparison.Ordinal);
        Assert.Equal([(EnvironmentFixtures.ContosoTenant, "nobody@contoso.com")], tenantDirectory.Lookups);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Share_ResolvesASignInAddressTheEnvironmentHasNotProvisionedThroughTheTenant()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        var grace = "44444444-4444-4444-4444-444444444444";
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser(grace, "Grace Hopper", "grace@contoso.com");

        var result = await Create(Seeded(workspace), sharing, Directory(), tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "  grace@contoso.com ", Role = "use" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal(grace, result.Principal!.ObjectId);
        Assert.Equal("Grace Hopper", result.Principal.PrincipalName);
        Assert.Equal("grace@contoso.com", result.Principal.SignInAddress);
        Assert.Equal("User", result.Principal.PrincipalType);
        Assert.Equal("use", result.Principal.Role);
        Assert.Equal(AgentSharingRole.Consumer, sharing.StateOf(Bot).RoleOf(grace));
        Assert.Equal(AgentSharingRole.Owner, sharing.StateOf(Bot).RoleOf(Owner));
        Assert.Equal([(EnvironmentFixtures.ContosoTenant, "grace@contoso.com")], tenantDirectory.Lookups);
    }

    [Fact]
    public async Task Share_ReportsTheSignInAddressOfAGuestTheTenantResolvedByEmailAddress()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        var guest = "66666666-6666-6666-6666-666666666666";
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser(guest, "Grace Hopper", "grace_fabrikam.com#EXT#@contoso.com", "grace@fabrikam.com");

        var result = await Create(Seeded(workspace), sharing, Directory(), tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "grace@fabrikam.com", Role = "edit" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal(guest, result.Principal!.ObjectId);
        Assert.Equal("Grace Hopper", result.Principal.PrincipalName);
        Assert.Equal("grace_fabrikam.com#EXT#@contoso.com", result.Principal.SignInAddress);
        Assert.Equal(AgentSharingRole.Collaborator, sharing.StateOf(Bot).RoleOf(guest));
        Assert.Equal(AgentSharingRole.Owner, sharing.StateOf(Bot).RoleOf(Owner));
    }

    [Fact]
    public async Task Share_DoesNotAskTheTenantForAUserTheEnvironmentAlreadyKnows()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser("55555555-5555-5555-5555-555555555555", "Ada Elsewhere", "ada@contoso.com");

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory(), tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal(Ada, result.Principal!.ObjectId);
        Assert.Equal("Ada Lovelace", result.Principal.PrincipalName);
        Assert.Empty(tenantDirectory.Lookups);
    }

    [Fact]
    public async Task Share_DoesNotAskTheTenantForAnObjectId()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var tenantDirectory = new FakeTenantUserDirectoryPort();

        await Create(Seeded(workspace), OwnedAgent(), Directory(), tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "33333333-3333-3333-3333-333333333333", Role = "use" },
            CancellationToken.None);

        Assert.Empty(tenantDirectory.Lookups);
        Assert.Empty(tenantDirectory.NameLookups);
    }

    public static TheoryData<AgentHostingException> TenantLookupFailures => new()
    {
        ManagedAgentException.SharePrincipalLookupFailed("grace@contoso.com", "HTTP 403."),
        new TargetRequestException("Authentication for Graph was rejected (token missing, invalid, or expired).", ExitCodes.NotSignedIn, TargetErrorKind.AuthExpired, status: 401),
    };

    [Theory]
    [MemberData(nameof(TenantLookupFailures))]
    public async Task Share_PassesOnATenantLookupFailureUnchanged(AgentHostingException failure)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        Assert.Same(failure, await Assert.ThrowsAnyAsync<AgentHostingException>(() => Create(Seeded(workspace), sharing, Directory(), tenantDirectory: new FakeTenantUserDirectoryPort { Failure = failure }).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "grace@contoso.com", Role = "use" },
            CancellationToken.None)));
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Unshare_RemovesAPrincipalResolvedThroughTheTenant()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var grace = "44444444-4444-4444-4444-444444444444";
        var sharing = new FakeAgentSharingPort().WithTargets(
            Bot,
            AgentShareTarget.For(Owner, AgentSharingRole.Owner),
            AgentShareTarget.For(grace, AgentSharingRole.Consumer));
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser(grace, "Grace Hopper", "grace@contoso.com");

        var result = await Create(Seeded(workspace), sharing, Directory(), tenantDirectory: tenantDirectory).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "grace@contoso.com" },
            CancellationToken.None);

        Assert.Equal("unshared", result.Status);
        Assert.Equal("Grace Hopper", result.Principal.PrincipalName);
        Assert.Null(sharing.StateOf(Bot).RoleOf(grace));
        Assert.Equal(AgentSharingRole.Owner, sharing.StateOf(Bot).RoleOf(Owner));
    }

    [Fact]
    public async Task Unshare_RejectsASignInAddressTheTenantDoesNotKnow()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "nobody@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-principal-not-found", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Share_SurvivesADirectoryThatCannotBeReached()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, new ThrowingDirectoryPrincipalPort()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = Ada, Role = "use" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal(Ada, result.Principal!.ObjectId);
    }

    [Fact]
    public async Task Share_DoesNotReportASignInAddressAsUnknownWhenTheDirectoryRefusedTheQuestion()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var refused = new ThrowingDirectoryPrincipalPort(CopilotStudioException.ServiceError("GET systemusers", "HTTP 403 Forbidden."));
        var tenantDirectory = new FakeTenantUserDirectoryPort();

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => Create(Seeded(workspace), OwnedAgent(), refused, tenantDirectory: tenantDirectory).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Contains("HTTP 403", failure.Message, StringComparison.Ordinal);
        Assert.Empty(tenantDirectory.Lookups);
    }

    [Fact]
    public async Task Share_ReportsAGrantTheServiceDidNotRecordAsAFailure()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState { Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-not-applied", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_ReportsOrgWideAccessTheServiceDidNotRecordAsAFailure()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState { ShareWithOrg = false, Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = true },
            CancellationToken.None));

        Assert.Equal("share-not-applied", failure.ErrorKind);
    }

    [Fact]
    public async Task Unshare_ReportsARemovalTheServiceDidNotRecordAsAFailure()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer));
        sharing.WriteReturns = sharing.StateOf(Bot);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-not-removed", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_RequiresAnAgentSelectorJustAsTheCommandLineDoes()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-agent-selector-required", failure.ErrorKind);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("view")]
    [InlineData("edti")]
    public async Task Share_RejectsARoleAlongsideOrgWideAccessJustAsTheCommandLineDoes(string role)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", Role = role, ShareWithOrg = true },
            CancellationToken.None));

        Assert.Equal("share-role-not-applicable", failure.ErrorKind);
    }

    [Fact]
    public async Task Unshare_RequiresAUserJustAsTheCommandLineDoes()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), OwnedAgent(), Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1" },
            CancellationToken.None));

        Assert.Equal("share-principal-selector-required", failure.ErrorKind);
    }

    [Fact]
    public async Task ShareShow_SurvivesADirectoryThatCannotBeReached()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer));

        var result = await Create(Seeded(workspace), sharing, new ThrowingDirectoryPrincipalPort()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(Ada, Assert.Single(result.Shares).PrincipalName);
    }

    [Fact]
    public async Task Share_NamesAnUnresolvedPrincipalAsItsKindAndObjectId()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var unknown = "33333333-3333-3333-3333-333333333333";

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = unknown, Role = "use" },
            CancellationToken.None);

        var text = string.Join("\n", result.ToTextLines());

        Assert.False(result.Principal!.IsNamed);
        Assert.Contains($"with unknown {unknown} as 'use'", text, StringComparison.Ordinal);
        Assert.Contains("name unavailable in this environment", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_NamesAResolvedPrincipalPlainly()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        var text = string.Join("\n", result.ToTextLines());

        Assert.True(result.Principal!.IsNamed);
        Assert.Contains("with Ada Lovelace as 'use'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name unavailable", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshare_NamesAnUnresolvedPrincipalAsItsKindAndObjectId()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var unknown = "33333333-3333-3333-3333-333333333333";
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(unknown, AgentSharingRole.Consumer));

        var result = await Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = unknown },
            CancellationToken.None);

        Assert.Contains($"with unknown {unknown}", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Share_SetsOrgWideAccessWithoutNamingAPrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = true },
            CancellationToken.None);

        Assert.True(result.ShareWithOrg);
        Assert.True(result.Changed);
        Assert.Null(result.Principal);
        Assert.Null(result.Role);
        Assert.Contains("Enabled org-wide use access", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_ClearsOrgWideAccessWithoutDisturbingAnyPrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState
        {
            ShareWithOrg = true,
            Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer)],
        });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = false },
            CancellationToken.None);

        var written = Assert.Single(sharing.Writes).State;

        Assert.False(result.ShareWithOrg);
        Assert.False(written.ShareWithOrg);
        Assert.Equal(AgentSharingRole.Owner, written.RoleOf(Owner));
        Assert.Equal(AgentSharingRole.Consumer, written.RoleOf(Ada));
        Assert.Contains("Disabled org-wide use access", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_OrgWideAccessAlreadySetWritesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState { ShareWithOrg = true });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = true },
            CancellationToken.None);

        Assert.Equal("unchanged", result.Status);
        Assert.Empty(sharing.Writes);
        Assert.Contains("was already on", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_WithNeitherAPrincipalNorOrgWideIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-principal-selector-required", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_ResolvesTheAgentInsideTheEnvironmentItIsSharingFrom()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = "stale-bot" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));
        var sharing = new FakeAgentSharingPort();

        var result = await Create(store, sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
        Assert.Equal(Bot, result.McsAgentId);
        Assert.Equal(AgentSharingRole.Consumer, sharing.StateOf(Bot).RoleOf(Ada));
    }

    [Fact]
    public async Task Share_ResolvesTheRecordedIdentifierWhenTheProjectDeclaresNoSchemaName()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = Bot, AgentLocation = "no-such-workspace" });
        var catalog = Catalog();

        var result = await Create(store, new FakeAgentSharingPort(), Directory(), catalog).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal(Bot, result.McsAgentId);
        Assert.Equal([Bot], catalog.ResolvedValues);
    }

    [Fact]
    public async Task Share_RefusesTheRecordedIdentifierTheEnvironmentDoesNotHold()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = Bot, AgentLocation = "no-such-workspace" });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeAgentSharingPort(), Directory(), Catalog(resolves: null)).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-target-not-in-environment", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_StillTargetsAnAgentWhoseRecordedIdentifierWasCleared()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = null });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));
        var sharing = new FakeAgentSharingPort();

        var result = await Create(store, sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
        Assert.Equal(Bot, result.McsAgentId);
        Assert.Equal(AgentSharingRole.Consumer, sharing.StateOf(Bot).RoleOf(Ada));
    }

    [Fact]
    public async Task Share_RefusesAnAgentTheEnvironmentDoesNotHold()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), new FakeAgentSharingPort(), Directory(), Catalog(resolves: null)).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-target-not-in-environment", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_TargetsTheMostRecentlyDeployedEnvironment()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = Bot });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-1", lastDeployedTime: DateTimeOffset.UtcNow.AddDays(-2)));
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-2", name: "prod", stage: DeploymentStages.Prod, environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));

        var result = await Create(store, new FakeAgentSharingPort(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
    }

    [Fact]
    public async Task Share_FallsBackToTheHomeEnvironmentWhenNothingHasDeployed()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: "env-1") with { McsAgentId = Bot });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: null));

        var result = await Create(store, new FakeAgentSharingPort(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("env-1", result.EnvironmentId);
    }

    [Fact]
    public async Task Share_ForARecordWithNoEnvironment_NamesTheRecord()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: string.Empty) with { McsAgentId = Bot });
        var catalog = Catalog();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeAgentSharingPort(), Directory(), catalog).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("agent-environment-missing", failure.ErrorKind);
        Assert.Contains("'Contoso Agent' (agent-1)", failure.Message, StringComparison.Ordinal);
        Assert.Empty(catalog.ResolvedValues);
    }

    [Fact]
    public async Task Share_IgnoresTheHomeEnvironmentOnceASlotHasDeployedElsewhere()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: "env-1") with { McsAgentId = Bot });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));

        var result = await Create(store, new FakeAgentSharingPort(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
    }

    [Fact]
    public async Task ShareShow_ResolvesTheSameEnvironmentAsShare()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent(environmentId: "env-1") with { McsAgentId = Bot });
        store.Add(workspace.Root, ManagedAgentFixtures.Deployment("slot-1", environmentId: "env-2", lastDeployedTime: DateTimeOffset.UtcNow));

        var result = await Create(store, new FakeAgentSharingPort(), Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("env-2", result.EnvironmentId);
    }

    [Fact]
    public async Task Share_AnAgentThatHasNeverDeployedIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { McsAgentId = null });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, new FakeAgentSharingPort(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal("not-deployed-yet", failure.ErrorKind);
    }

    [Fact]
    public async Task Share_OffersTheLinkOnlyWhenItActuallyGranted()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = Seeded(workspace);
        var sharing = OwnedAgent();
        var service = Create(store, sharing, Directory());
        var request = new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" };

        var granted = string.Join("\n", (await service.ShareAsync(request, CancellationToken.None)).ToTextLines());
        var repeated = string.Join("\n", (await service.ShareAsync(request, CancellationToken.None)).ToTextLines());

        Assert.Contains("Send this link to Ada Lovelace:", granted, StringComparison.Ordinal);
        Assert.Contains($"/environments/env-1/agents/{Bot}", granted, StringComparison.Ordinal);
        Assert.DoesNotContain("Send this link", repeated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_OffersNoLinkWhenOnlyOrgWideAccessChanged()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = true },
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.DoesNotContain("Send this link", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_NamesAnUnresolvedPrincipalInTheLinkOffer()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var unknown = "33333333-3333-3333-3333-333333333333";

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = unknown, Role = "use" },
            CancellationToken.None);

        Assert.Contains($"Send this link to unknown {unknown}:", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareShow_OffersTheAgentLink()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(Seeded(workspace), OwnedAgent(), Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Contains($"/environments/env-1/agents/{Bot}", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareShow_OffersTheAgentLinkEvenWhenNobodyHasAccess()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(Seeded(workspace), new FakeAgentSharingPort(), Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        var text = string.Join("\n", result.ToTextLines());

        Assert.Contains("is not shared with anyone", text, StringComparison.Ordinal);
        Assert.Contains($"/agents/{Bot}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_DoesNotAbsorbAFailedSignIn()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<NotSignedInException>(() => Create(Seeded(workspace), OwnedAgent(), new SignedOutDirectoryPrincipalPort()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));
    }

    [Fact]
    public async Task ShareShow_DoesNotAbsorbAFailedSignIn()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer));

        await Assert.ThrowsAsync<NotSignedInException>(() => Create(Seeded(workspace), sharing, new SignedOutDirectoryPrincipalPort()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None));
    }

    [Fact]
    public async Task Share_GrantsViewAsAnalyst()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "view" },
            CancellationToken.None);

        Assert.Equal("view", result.Role);
        Assert.Equal(AgentSharingRole.Analyst, sharing.StateOf(Bot).RoleOf(Ada));
    }

    [Fact]
    public async Task ShareShow_ReportsAnAnalystAsView()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Analyst));

        var result = await Create(Seeded(workspace), sharing, Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("view", Assert.Single(result.Shares).Role);
    }

    [Fact]
    public async Task Share_AgainstACodeAgentIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var store = ManagedAgentFixtures.Store();
        store.Add(workspace.Root, ManagedAgentFixtures.Agent() with { AgentType = AgentTypes.CodeAgent });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(store, OwnedAgent(), Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("not-an-mcs-agent", failure.ErrorKind);
    }

    [Fact]
    public async Task ShareShow_NamesEveryPrincipalAndPutsTheOwnerFirst()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(
            Bot,
            AgentShareTarget.For(Ada, AgentSharingRole.Collaborator),
            AgentShareTarget.For(Owner, AgentSharingRole.Owner),
            AgentShareTarget.For(Support, AgentSharingRole.Consumer));
        var directory = Directory();

        var result = await Create(Seeded(workspace), sharing, directory).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(["Hoa Nguyen", "Ada Lovelace", "Support"], result.Shares.Select(share => share.PrincipalName));
        Assert.Equal(["owner", "edit", "use"], result.Shares.Select(share => share.Role));
        Assert.Equal(1, directory.ResolveManyCalls);
    }

    [Fact]
    public async Task ShareShow_FallsBackToTheObjectIdWhenTheDirectoryDoesNotAnswer()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For("unknown-principal", AgentSharingRole.Consumer));

        var result = await Create(Seeded(workspace), sharing, Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        var share = Assert.Single(result.Shares);

        Assert.Equal("unknown-principal", share.PrincipalName);
        Assert.Equal("Unknown", share.PrincipalType);
    }

    [Fact]
    public async Task ShareShow_NamesThroughTheTenantThePrincipalsTheEnvironmentHasNotProvisioned()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var guest = "66666666-6666-6666-6666-666666666666";
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser(guest, "Grace Hopper", "grace_fabrikam.com#EXT#@contoso.com");

        var result = await Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(guest, AgentSharingRole.Collaborator)), Directory(), tenantDirectory: tenantDirectory).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(["Hoa Nguyen", "Grace Hopper"], result.Shares.Select(share => share.PrincipalName));
        var named = result.Shares[1];
        Assert.Equal(guest, named.ObjectId);
        Assert.Equal("User", named.PrincipalType);
        Assert.Equal("grace_fabrikam.com#EXT#@contoso.com", named.SignInAddress);
        Assert.Equal("edit", named.Role);
        var lookup = Assert.Single(tenantDirectory.NameLookups);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, lookup.TenantId);
        Assert.Equal([guest], lookup.ObjectIds);
        Assert.Empty(tenantDirectory.Lookups);
    }

    [Fact]
    public async Task ShareShow_DoesNotAskTheTenantWhenTheEnvironmentNamesEveryPrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var tenantDirectory = new FakeTenantUserDirectoryPort();

        await Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer), AgentShareTarget.For(Support, AgentSharingRole.Consumer)), Directory(), tenantDirectory: tenantDirectory).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Empty(tenantDirectory.NameLookups);
    }

    [Fact]
    public async Task ShareShow_AsksTheTenantForEveryPrincipalWhenTheEnvironmentCannotBeReached()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var tenantDirectory = new FakeTenantUserDirectoryPort().WithUser(Ada, "Ada Lovelace", "ada@contoso.com");

        var share = Assert.Single((await Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer)), new ThrowingDirectoryPrincipalPort(), tenantDirectory: tenantDirectory).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None)).Shares);

        Assert.Equal("Ada Lovelace", share.PrincipalName);
        Assert.Equal("ada@contoso.com", share.SignInAddress);
        Assert.Equal([Ada], Assert.Single(tenantDirectory.NameLookups).ObjectIds);
    }

    public static TheoryData<Exception> TenantNamingFailures => new()
    {
        new TargetRequestException("The signed-in account lacks permission for this Graph operation.", ExitCodes.Failure, TargetErrorKind.PermissionDenied, status: 403),
        new TargetRequestException("Authentication for Graph was rejected (token missing, invalid, or expired).", ExitCodes.NotSignedIn, TargetErrorKind.AuthExpired, status: 401),
        new InteractionRequiredException("Could not silently obtain the requested access token."),
        new TokenThrottledException("Microsoft Entra ID is throttling token requests from this client."),
        ManagedAgentException.SharePrincipalLookupFailed("66666666-6666-6666-6666-666666666666", "HTTP 500."),
        new HttpRequestException("graph unreachable"),
        new TimeoutException("graph timed out"),
        new IOException("connection reset"),
    };

    [Theory]
    [MemberData(nameof(TenantNamingFailures))]
    public async Task ShareShow_KeepsTheObjectIdWhenTheTenantCannotNameThePrincipal(Exception failure)
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var guest = "66666666-6666-6666-6666-666666666666";

        var result = await Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(guest, AgentSharingRole.Consumer)), Directory(), tenantDirectory: new FakeTenantUserDirectoryPort { Failure = failure }).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(["Hoa Nguyen", guest], result.Shares.Select(share => share.PrincipalName));
        Assert.Equal("Unknown", result.Shares[1].PrincipalType);
    }

    [Fact]
    public async Task ShareShow_DoesNotAbsorbAProgrammingErrorFromTheTenant()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For("66666666-6666-6666-6666-666666666666", AgentSharingRole.Consumer)), Directory(), tenantDirectory: new FakeTenantUserDirectoryPort { Failure = new InvalidOperationException("a bug in this build") }).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None));
    }

    [Fact]
    public async Task ShareShow_KeepsTheNamesTheTenantReturnedBeforeItFailed()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        AssertOnlyGraceIsNamed(await ShowGraceAndAStrangerAsync(workspace, new FakeTenantUserDirectoryPort { Failure = new HttpRequestException("graph unreachable") }));
    }

    [Fact]
    public async Task ShareShow_ShowsTheObjectIdOfWhatTheTenantHasNotNamedOnceNamingRunsOutOfTime()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var tenantDirectory = new FakeTenantUserDirectoryPort { AfterNaming = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) };

        AssertOnlyGraceIsNamed(await ShowGraceAndAStrangerAsync(workspace, tenantDirectory, TimeSpan.FromMilliseconds(1), testTimeout.Token));
        Assert.Single(tenantDirectory.NameLookups);
    }

    [Fact]
    public async Task ShareShow_StopsWhenCancelledWhileTheTenantIsNaming()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        using var cancellation = new CancellationTokenSource();
        var tenantDirectory = new FakeTenantUserDirectoryPort
        {
            AfterNaming = async cancellationToken =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShowGraceAndAStrangerAsync(workspace, tenantDirectory, cancellationToken: cancellation.Token));
        Assert.Single(tenantDirectory.NameLookups);
    }

    [Fact]
    public async Task ShareShow_MatchesATenantNameToItsObjectIdWhateverTheCase()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var share = Assert.Single((await Create(Seeded(workspace), new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Grace.ToUpperInvariant(), AgentSharingRole.Consumer)), Directory(), tenantDirectory: new FakeTenantUserDirectoryPort().WithUser(Grace, "Grace Hopper", "grace@contoso.com")).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None)).Shares);

        Assert.Equal("Grace Hopper", share.PrincipalName);
        Assert.Equal("User", share.PrincipalType);
    }

    [Fact]
    public async Task ShareShow_WithNoSharesIsEmpty()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        var result = await Create(Seeded(workspace), new FakeAgentSharingPort(), Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Empty(result.Shares);
        Assert.Contains("is not shared with anyone", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareShow_ReportsOrgWideAccess()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState { ShareWithOrg = true });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.True(result.ShareWithOrg);
        Assert.Contains("Everyone in the organization", string.Join("\n", result.ToTextLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshare_RemovesOnlyThatPrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(
            Bot,
            AgentShareTarget.For(Owner, AgentSharingRole.Owner),
            AgentShareTarget.For(Ada, AgentSharingRole.Consumer));

        var result = await Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None);

        Assert.Equal("unshared", result.Status);
        Assert.Null(sharing.StateOf(Bot).RoleOf(Ada));
        Assert.Equal(AgentSharingRole.Owner, sharing.StateOf(Bot).RoleOf(Owner));
    }

    [Fact]
    public async Task Unshare_WithNoAccessWritesNothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var result = await Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None);

        Assert.Equal("unchanged", result.Status);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Unshare_TargetingTheOwnerIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "hoa@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-owner-not-changeable", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Unshare_TargetingAnOwnerListedBehindAnotherAccessLevelIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, new AgentShareTarget { ObjectId = Owner, SharingTypes = ["Consumer", "Owner"] });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "hoa@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-owner-not-changeable", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Share_TargetingAnOwnerListedBehindAnotherAccessLevelIsAUsageError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, new AgentShareTarget { ObjectId = Owner, SharingTypes = ["Consumer", "Owner"] });

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "hoa@contoso.com", Role = "edit" },
            CancellationToken.None));

        Assert.Equal("share-owner-not-changeable", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task ShareShow_ReportsEveryAccessLevelAPrincipalHolds()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, new AgentShareTarget { ObjectId = Ada, SharingTypes = ["Consumer", "Analyst"] });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal("use+view", Assert.Single(result.Shares).Role);
    }

    [Fact]
    public async Task Share_TogglingOrgWideAccessLeavesAnUnchangedPrincipalUntouched()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var existing = new AgentShareTarget { ObjectId = Ada, SharingTypes = ["Consumer", "Analyst"], ObjectIdType = "Something", ClientId = "app-1" };
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), existing);

        await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use", ShareWithOrg = true },
            CancellationToken.None);

        var written = Assert.Single(sharing.Writes).State.TargetFor(Ada)!;

        Assert.Equal(["Consumer"], written.SharingTypes);
        Assert.Equal("Something", written.ObjectIdType);
        Assert.Equal("app-1", written.ClientId);
    }

    [Fact]
    public async Task Share_RepeatingTheSameRoleWhileTogglingOrgWideAccessDoesNotRewriteThePrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var existing = new AgentShareTarget { ObjectId = Ada, SharingTypes = ["Consumer"], ObjectIdType = "Something", ClientId = "app-1" };
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), existing);

        await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use", ShareWithOrg = true },
            CancellationToken.None);

        Assert.Same(existing, Assert.Single(sharing.Writes).State.TargetFor(Ada));
    }

    [Fact]
    public async Task Share_ReportsAPrincipalTheServiceDroppedWhileGrantingAnother()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState { Targets = [AgentShareTarget.For(Ada, AgentSharingRole.Consumer)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-not-applied", failure.ErrorKind);
        Assert.Contains(Owner, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_SaysPartOfTheRequestMayHaveTakenEffect()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState { ShareWithOrg = true, Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use", ShareWithOrg = true },
            CancellationToken.None));

        Assert.Contains("part of the request may have taken effect", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing else was changed", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshare_ReportsOrgWideAccessTheServiceEnabledWhileRemovingAPrincipal()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer));
        sharing.WriteReturns = new AgentSharingState { ShareWithOrg = true, Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-not-removed", failure.ErrorKind);
        Assert.Contains("org-wide", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshare_ReportsAPrincipalTheServiceDroppedAlongsideTheOneAskedFor()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer));
        sharing.WriteReturns = new AgentSharingState();

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-not-removed", failure.ErrorKind);
        Assert.Contains(Owner, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The live failure this reproduces: a freshly deployed agent reads back no targets, the write
    /// comes back with the owner filled in, and the grant must still be reported as the success it is.
    /// </summary>
    [Fact]
    public async Task Share_SucceedsWhenTheServiceFillsInAnOwnerTheReadNeverReported()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState());
        sharing.WriteReturns = new AgentSharingState
        {
            Targets = [AgentShareTarget.For(Ada, AgentSharingRole.Consumer), AgentShareTarget.For(Owner, AgentSharingRole.Owner)],
        };

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None);

        Assert.Equal("shared", result.Status);
        Assert.Equal("use", result.Role);
    }

    [Fact]
    public async Task Unshare_SucceedsWhenTheServiceFillsInAnOwnerTheReadNeverReported()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer));
        sharing.WriteReturns = new AgentSharingState { Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var result = await Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None);

        Assert.Equal("unshared", result.Status);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task Share_SetsOrgWideAccessWhenTheServiceFillsInAnOwnerTheReadNeverReported()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithState(Bot, new AgentSharingState());
        sharing.WriteReturns = new AgentSharingState { ShareWithOrg = true, Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var result = await Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", ShareWithOrg = true },
            CancellationToken.None);

        Assert.True(result.ShareWithOrg);
        Assert.True(result.Changed);
    }

    [Fact]
    public async Task Share_ReportsASettingTheServiceChangedWhileGranting()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState
        {
            StoreDiscoverable = true,
            Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer)],
        };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-not-applied", failure.ErrorKind);
        Assert.Contains("agent-store discoverability", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_ReportsTargetMetadataTheServiceRewroteWhileGranting()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = OwnedAgent();
        sharing.WriteReturns = new AgentSharingState
        {
            Targets =
            [
                AgentShareTarget.For(Owner, AgentSharingRole.Owner),
                new AgentShareTarget { ObjectId = Ada, SharingTypes = ["Consumer"], ObjectIdType = "Something" },
            ],
        };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "use" },
            CancellationToken.None));

        Assert.Equal("share-not-applied", failure.ErrorKind);
        Assert.Contains("recorded as 'Something'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshare_ReportsASettingTheServiceChangedWhileRemoving()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Owner, AgentSharingRole.Owner), AgentShareTarget.For(Ada, AgentSharingRole.Consumer));
        sharing.WriteReturns = new AgentSharingState { EnableActivitySharing = true, Targets = [AgentShareTarget.For(Owner, AgentSharingRole.Owner)] };

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-not-removed", failure.ErrorKind);
        Assert.Contains("activity sharing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_RefusesAStateListingThePrincipalTwice()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer), AgentShareTarget.For(Ada, AgentSharingRole.Owner));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).ShareAsync(
            new ManagedAgentShareRequest { AgentId = "agent-1", User = "ada@contoso.com", Role = "edit" },
            CancellationToken.None));

        Assert.Equal("share-state-ambiguous", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task Unshare_RefusesAStateListingThePrincipalTwiceRatherThanRemovingBoth()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(Bot, AgentShareTarget.For(Ada, AgentSharingRole.Consumer), AgentShareTarget.For(Ada, AgentSharingRole.Owner));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => Create(Seeded(workspace), sharing, Directory()).UnshareAsync(
            new ManagedAgentUnshareRequest { AgentId = "agent-1", User = "ada@contoso.com" },
            CancellationToken.None));

        Assert.Equal("share-state-ambiguous", failure.ErrorKind);
        Assert.Empty(sharing.Writes);
    }

    [Fact]
    public async Task ShareShow_PutsAnOwnerListedBehindAnotherAccessLevelFirst()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);
        var sharing = new FakeAgentSharingPort().WithTargets(
            Bot,
            AgentShareTarget.For(Ada, AgentSharingRole.Consumer),
            new AgentShareTarget { ObjectId = Owner, SharingTypes = ["Consumer", "Owner"] });

        var result = await Create(Seeded(workspace), sharing, Directory()).ShowAsync(
            new ManagedAgentShareShowRequest { AgentId = "agent-1" },
            CancellationToken.None);

        Assert.Equal(Owner, result.Shares[0].ObjectId);
        Assert.Equal("use+owner", result.Shares[0].Role);
    }

    [Fact]
    public async Task ShareShow_DoesNotAbsorbAProgrammingError()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(
            Seeded(workspace),
            OwnedAgent(),
            new ThrowingDirectoryPrincipalPort(new InvalidOperationException("a bug in this build"))).ShowAsync(
                new ManagedAgentShareShowRequest { AgentId = "agent-1" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Share_DoesNotAbsorbARequestBlockedBeforeItsCredentialWasSent()
    {
        using var workspace = new TemporaryWorkspace();
        using var scope = new CurrentDirectoryScope(workspace.Root);

        await Assert.ThrowsAsync<CopilotStudioException>(() => Create(
            Seeded(workspace),
            OwnedAgent(),
            new ThrowingDirectoryPrincipalPort(CopilotStudioException.UntrustedDataverseOrigin("https://elsewhere.example", "https://contoso.crm.dynamics.com"))).ShareAsync(
                new ManagedAgentShareRequest { AgentId = "agent-1", User = Ada, Role = "use" },
                CancellationToken.None));
    }

    [Fact]
    public void Constructor_RequiresATenantDirectory() =>
        Assert.Equal("tenantDirectory", Assert.Throws<ArgumentNullException>(() => Construct(ManagedAgentFixtures.Store(), OwnedAgent(), Directory(), Catalog(), null!)).ParamName);
}

public sealed class AgentShareRolesTests
{
    [Theory]
    [InlineData("use", AgentSharingRole.Consumer)]
    [InlineData("USE", AgentSharingRole.Consumer)]
    [InlineData("  Use  ", AgentSharingRole.Consumer)]
    [InlineData("edit", AgentSharingRole.Collaborator)]
    [InlineData("EDIT", AgentSharingRole.Collaborator)]
    [InlineData("view", AgentSharingRole.Analyst)]
    [InlineData("VIEW", AgentSharingRole.Analyst)]
    [InlineData("  View  ", AgentSharingRole.Analyst)]
    public void TryParse_AcceptsEverySpelling(string value, AgentSharingRole expected)
    {
        Assert.True(AgentShareRoles.TryParse(value, out var role));
        Assert.Equal(expected, role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]    [InlineData("owner")]
    [InlineData("consumer")]
    [InlineData("collaborator")]
    [InlineData("analyst")]
    public void TryParse_RejectsEverythingElse(string? value) => Assert.False(AgentShareRoles.TryParse(value, out _));

    [Theory]
    [InlineData(AgentSharingRole.Owner, "owner")]
    [InlineData(AgentSharingRole.Collaborator, "edit")]
    [InlineData(AgentSharingRole.Consumer, "use")]
    [InlineData(AgentSharingRole.Analyst, "view")]
    public void NameOf_ReportsThePortalWording(AgentSharingRole role, string expected) => Assert.Equal(expected, AgentShareRoles.NameOf(role));

    [Fact]
    public void NameOf_ReportsNoAccessAsNone() => Assert.Equal("none", AgentShareRoles.NameOf((AgentShareTarget?)null));

    [Fact]
    public void NameOf_ReportsAnAccessLevelThisBuildDoesNotKnowAsTheServiceSpellsIt() =>
        Assert.Equal("SomethingNew", AgentShareRoles.NameOf(new AgentShareTarget { ObjectId = "principal-1", SharingTypes = ["SomethingNew"] }));

    [Fact]
    public void NameOf_JoinsSeveralUnknownAccessLevels() =>
        Assert.Equal("First+Second", AgentShareRoles.NameOf(new AgentShareTarget { ObjectId = "principal-1", SharingTypes = ["First", "Second"] }));
    [Fact]
    public void NameOf_ReportsAnEmptyAccessListAsNone() =>
        Assert.Equal("none", AgentShareRoles.NameOf(new AgentShareTarget { ObjectId = "principal-1", SharingTypes = [] }));

    [Fact]
    public void NameOf_ReportsEveryAccessLevelAPrincipalHolds() =>
        Assert.Equal("use+owner", AgentShareRoles.NameOf(new AgentShareTarget { ObjectId = "11111111-1111-1111-1111-1111111111aa", SharingTypes = ["Consumer", "Owner"] }));

    [Fact]
    public void NameOf_MixesKnownAndUnknownAccessLevels() =>
        Assert.Equal("use+SomethingNew", AgentShareRoles.NameOf(new AgentShareTarget { ObjectId = "11111111-1111-1111-1111-1111111111aa", SharingTypes = ["Consumer", "SomethingNew"] }));

    [Fact]
    public void NameOf_RejectsARoleValueTheEnumDoesNotDefine() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentShareRoles.NameOf((AgentSharingRole)99));
}

public sealed class AgentSharingStateTests
{
    private const string First = "aaaaaaaa-1111-1111-1111-1111111111ff";
    private const string Second = "bbbbbbbb-2222-2222-2222-2222222222ee";

    private static AgentSharingState Owned() => new()
    {
        Targets = [AgentShareTarget.For(First, AgentSharingRole.Owner)],
    };

    [Fact]
    public void With_AddsAPrincipalWithoutDisturbingTheOthers()
    {
        var state = Owned().With(Second, AgentSharingRole.Consumer);

        Assert.Equal(2, state.Targets.Count);
        Assert.Equal(AgentSharingRole.Owner, state.RoleOf(First));
        Assert.Equal(AgentSharingRole.Consumer, state.RoleOf(Second));
    }

    [Fact]
    public void With_ReplacesAnExistingRoleRatherThanDuplicatingIt()
    {
        var state = Owned().With(Second, AgentSharingRole.Consumer).With(Second, AgentSharingRole.Collaborator);

        Assert.Equal(2, state.Targets.Count);
        Assert.Equal(AgentSharingRole.Collaborator, state.RoleOf(Second));
    }

    [Fact]
    public void Without_RemovesOnlyTheNamedPrincipal()
    {
        var state = Owned().With(Second, AgentSharingRole.Consumer).Without(Second);

        Assert.Equal(AgentSharingRole.Owner, Assert.Single(state.Targets).SoleRole());
        Assert.Null(state.RoleOf(Second));
    }

    [Fact]
    public void RoleOf_IgnoresObjectIdCase() => Assert.Equal(AgentSharingRole.Owner, Owned().RoleOf(First.ToUpperInvariant()));

    [Fact]
    public void IsOwner_FindsTheOwnerBehindAnotherAccessLevel() =>
        Assert.True(new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer", "Owner"] }.IsOwner);

    [Fact]
    public void IsOwner_IsFalseForAPrincipalThatOnlyHoldsOtherAccess() =>
        Assert.False(AgentShareTarget.For(First, AgentSharingRole.Consumer).IsOwner);

    [Fact]
    public void Roles_ReportsEveryAccessLevelThisBuildRecognises() =>
        Assert.Equal([AgentSharingRole.Consumer, AgentSharingRole.Owner], new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer", "SomethingNew", "Owner"] }.Roles);

    [Theory]
    [InlineData(AgentSharingRole.Consumer, true)]
    [InlineData(AgentSharingRole.Analyst, false)]
    public void HoldsExactly_IsTrueOnlyForTheSoleRoleHeld(AgentSharingRole role, bool expected) =>
        Assert.Equal(expected, AgentShareTarget.For(First, AgentSharingRole.Consumer).HoldsExactly(role));

    [Fact]
    public void HoldsExactly_IsFalseWhenThePrincipalAlsoHoldsAnotherAccessLevel() =>
        Assert.False(new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer", "Analyst"] }.HoldsExactly(AgentSharingRole.Consumer));

    [Fact]
    public void With_KeepsTheMetadataTheServiceRecordedAgainstAnExistingPrincipal()
    {
        var state = new AgentSharingState
        {
            Targets = [new AgentShareTarget { ObjectId = Second, SharingTypes = ["Consumer"], ObjectIdType = "Something", ClientId = "app-1" }],
        }.With(Second, AgentSharingRole.Collaborator);

        var target = Assert.Single(state.Targets);

        Assert.Equal(["Collaborator"], target.SharingTypes);
        Assert.Equal("Something", target.ObjectIdType);
        Assert.Equal("app-1", target.ClientId);
    }

    [Fact]
    public void DifferencesFrom_ReportsNothingWhenTheServiceAppliedTheStateAsAsked() =>
        Assert.Empty(Owned().With(Second, AgentSharingRole.Consumer).DifferencesFrom(Owned().With(Second, AgentSharingRole.Consumer)));

    [Fact]
    public void DifferencesFrom_ReportsAPrincipalTheServiceDropped()
    {
        var intended = Owned().With(Second, AgentSharingRole.Consumer);
        var applied = new AgentSharingState { Targets = [AgentShareTarget.For(Second, AgentSharingRole.Consumer)] };

        Assert.Contains(First, Assert.Single(applied.DifferencesFrom(intended)), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesFrom_ReportsAnAccessLevelTheServiceChanged()
    {
        var applied = Owned().With(Second, AgentSharingRole.Analyst);

        Assert.Contains(Second, Assert.Single(applied.DifferencesFrom(Owned().With(Second, AgentSharingRole.Consumer))), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesFrom_ReportsAccessNobodyAskedFor()
    {
        var applied = Owned().With(Second, AgentSharingRole.Consumer);

        Assert.Contains("was not asked for", Assert.Single(applied.DifferencesFrom(Owned())), StringComparison.Ordinal);
    }

    /// <summary>
    /// A freshly deployed agent reads back no targets at all and writes back the owner, so a grant
    /// built from that read omits an owner it was never told about.
    /// </summary>
    [Fact]
    public void DifferencesFrom_AcceptsAnOwnerTheReadNeverReported()
    {
        var intended = new AgentSharingState { Targets = [AgentShareTarget.For(Second, AgentSharingRole.Consumer)] };
        var applied = new AgentSharingState { Targets = [AgentShareTarget.For(Second, AgentSharingRole.Consumer), AgentShareTarget.For(First, AgentSharingRole.Owner)] };

        Assert.Empty(applied.DifferencesFrom(intended));
    }

    [Fact]
    public void DifferencesFrom_AcceptsAnOwnerAppearingAlongsideAnOrgWideChangeAlone() =>
        Assert.Empty((new AgentSharingState { ShareWithOrg = true, Targets = [AgentShareTarget.For(First, AgentSharingRole.Owner)] })
            .DifferencesFrom(new AgentSharingState { ShareWithOrg = true }));

    [Fact]
    public void DifferencesFrom_StillReportsAnOwnerHoldingOtherAccessNobodyAskedFor()
    {
        var applied = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Owner", "Consumer"] }] };

        Assert.Contains("was not asked for", Assert.Single(applied.DifferencesFrom(new AgentSharingState())), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesFrom_StillReportsAnOwnerTheServiceDropped()
    {
        var applied = new AgentSharingState { Targets = [AgentShareTarget.For(Second, AgentSharingRole.Consumer)] };

        Assert.Contains(First, Assert.Single(applied.DifferencesFrom(Owned().With(Second, AgentSharingRole.Consumer))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "Owner" }, true)]
    [InlineData(new[] { "Owner", "Consumer" }, false)]
    [InlineData(new[] { "Consumer" }, false)]
    public void IsOwnerOnly_IsTrueOnlyForAPrincipalHoldingNothingBesidesOwnership(string[] sharingTypes, bool expected) =>
        Assert.Equal(expected, new AgentShareTarget { ObjectId = First, SharingTypes = sharingTypes }.IsOwnerOnly);

    [Fact]
    public void IsOwnerOnly_IsFalseForAPrincipalHoldingNothing() =>
        Assert.False(new AgentShareTarget { ObjectId = First, SharingTypes = [] }.IsOwnerOnly);

    [Fact]
    public void DifferencesFrom_ReportsOrgWideAccessTheServiceChanged() =>
        Assert.Contains("org-wide", Assert.Single((Owned() with { ShareWithOrg = true }).DifferencesFrom(Owned())), StringComparison.Ordinal);

    [Fact]
    public void DifferencesFrom_AcceptsTheSameAccessListedInAnotherOrder()
    {
        var intended = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer", "Analyst"] }] };
        var applied = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Analyst", "Consumer"] }] };

        Assert.Empty(applied.DifferencesFrom(intended));
    }

    [Fact]
    public void Without_LeavesTheStateAloneWhenThePrincipalHasNoAccess() => Assert.Single(Owned().Without(Second).Targets);

    [Fact]
    public void With_PreservesOrgWideAccess() => Assert.True((Owned() with { ShareWithOrg = true }).With(Second, AgentSharingRole.Consumer).ShareWithOrg);

    [Fact]
    public void With_PreservesTheServiceOwnedSettings()
    {
        var state = (Owned() with { StoreDiscoverable = true, EnableActivitySharing = true }).With(Second, AgentSharingRole.Consumer);

        Assert.True(state.StoreDiscoverable);
        Assert.True(state.EnableActivitySharing);
    }

    [Fact]
    public void RoleOf_ReportsNoRoleForAnAccessLevelThisBuildDoesNotKnow() =>
        Assert.Null(new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["SomethingNew"] }] }.RoleOf(First));

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("")]
    public void TargetFor_DoesNotReadANumericOrEmptySharingTypeAsARole(string sharingType) =>
        Assert.Null(new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = [sharingType] }] }.RoleOf(First));

    [Fact]
    public void DifferencesFrom_ReportsAgentStoreDiscoverabilityTheServiceChanged() =>
        Assert.Contains("agent-store discoverability", Assert.Single((Owned() with { StoreDiscoverable = true }).DifferencesFrom(Owned())), StringComparison.Ordinal);

    [Fact]
    public void DifferencesFrom_ReportsActivitySharingTheServiceChanged() =>
        Assert.Contains("activity sharing", Assert.Single((Owned() with { EnableActivitySharing = true }).DifferencesFrom(Owned())), StringComparison.Ordinal);

    [Fact]
    public void DifferencesFrom_ReportsAnObjectIdTypeTheServiceRewrote()
    {
        var intended = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer"], ObjectIdType = "Object" }] };
        var applied = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer"], ObjectIdType = "Something" }] };

        Assert.Contains("recorded as 'Something'", Assert.Single(applied.DifferencesFrom(intended)), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesFrom_ReportsAClientIdTheServiceDropped()
    {
        var intended = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer"], ClientId = "app-1" }] };
        var applied = new AgentSharingState { Targets = [new AgentShareTarget { ObjectId = First, SharingTypes = ["Consumer"] }] };

        Assert.Contains("no application", Assert.Single(applied.DifferencesFrom(intended)), StringComparison.Ordinal);
    }

    [Fact]
    public void DifferencesFrom_ReportsAPrincipalTheServiceListedTwice()
    {
        var applied = new AgentSharingState { Targets = [AgentShareTarget.For(First, AgentSharingRole.Consumer), AgentShareTarget.For(First, AgentSharingRole.Owner)] };
        var intended = new AgentSharingState { Targets = [AgentShareTarget.For(First, AgentSharingRole.Consumer)] };

        Assert.Contains("listed 2 time(s)", Assert.Single(applied.DifferencesFrom(intended)), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateObjectIds_NamesAPrincipalListedMoreThanOnce() =>
        Assert.Equal(
            [First],
            new AgentSharingState { Targets = [AgentShareTarget.For(First, AgentSharingRole.Consumer), AgentShareTarget.For(First.ToUpperInvariant(), AgentSharingRole.Owner)] }.DuplicateObjectIds);

    [Fact]
    public void DuplicateObjectIds_IsEmptyWhenEveryPrincipalIsListedOnce() => Assert.Empty(Owned().With(Second, AgentSharingRole.Consumer).DuplicateObjectIds);
}
