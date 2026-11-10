using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileBoundEnvironmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), ".test-profile-environments-" + Guid.NewGuid().ToString("N"));
    private readonly FileAuthProfileStore _profiles;
    private readonly SelectionStore _selection = new();

    public ProfileBoundEnvironmentTests()
    {
        Directory.CreateDirectory(_directory);
        _profiles = new FileAuthProfileStore(Path.Combine(_directory, "profiles.json"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AuthProfile Profile(string key = "home-a") => new()
    {
        AccountKey = key, Username = key + "@contoso.com", TenantId = "tenant-a",
        ClientId = "client-a", Authority = "https://login.microsoftonline.com/organizations",
    };

    private static ActiveEnvironment Selection(string id = "env-a") => new()
    {
        EnvironmentId = id, DisplayName = id, TenantId = "tenant-a",
        ApiEndpoint = new Uri("https://env.api.powerplatform.com"),
        ApiAudience = PowerPlatformApiTargetConfig.Public.Audience,
    };

    private static PowerPlatformEnvironment Environment(string id = "env-a") => new()
    {
        EnvironmentId = id, DisplayName = id, TenantId = "tenant-a",
        ApiEndpoint = Selection().ApiEndpoint, ApiAudience = Selection().ApiAudience,
    };

    private EnvironmentSelectionService Service(IEnvironmentClient? client = null, IEnvironmentSelectionStore? selection = null) =>
        new(client ?? new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>([Environment(), Environment("env-b")])),
            selection ?? _selection, _profiles, PowerPlatformCloudMap.ForCloud(CloudInstance.Public), new FixedClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task ProfileDefaultResolvesWithoutInventory_AndExplicitTargetWins()
    {
        await _profiles.CreateAsync(Profile() with { DefaultEnvironment = Selection() }, default);
        var client = new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>([Environment("env-b")]));
        var service = Service(client);
        Assert.Equal("env-a", (await service.ResolveForCommandAsync(null, default)).EnvironmentId);
        Assert.Equal(0, client.Calls);
        Assert.Equal("env-b", (await service.ResolveForCommandAsync("env-b", default)).EnvironmentId);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task SameTenantDifferentProfileCannotUseOwnedOverride()
    {
        await _profiles.CreateAsync(Profile(), default);
        var service = Service();
        await service.SelectAsync("env-a", default);
        var b = await _profiles.CreateAsync(Profile("home-b"), default);
        await Assert.ThrowsAsync<EnvironmentNotSelectedException>(() => service.ResolveForCommandAsync(null, default));
        Assert.NotNull(_selection.Active);
        await _profiles.UpdateAsync(b.ProfileId, b.Revision, new(DefaultEnvironment: Selection("env-b")), default);
        Assert.Equal("env-b", (await service.ResolveForCommandAsync(null, default)).EnvironmentId);
        await _profiles.SelectAsync(new(Index: 1), default);
        Assert.Equal("env-a", (await service.ResolveForCommandAsync(null, default)).EnvironmentId);
    }

    [Fact]
    public async Task SelectCommitsProfileDefault_AndRenamePreservesOwnership()
    {
        await _profiles.CreateAsync(Profile() with { Name = "Before" }, default);
        var service = Service();
        await service.SelectAsync("env-a", default);
        Assert.Equal("env-a", (await _profiles.GetActiveProfileAsync(default))?.DefaultEnvironment?.EnvironmentId);
        await _profiles.UpdateAsync(new(Name: "Before"), new(Name: "After"), default);
        Assert.True((await service.GetActiveBindingAsync(default))?.IsBound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectRejectsConcurrentProfileRevisionOrSwitch(bool switchProfile)
    {
        await _profiles.CreateAsync(Profile() with { Name = "Before" }, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(new Inventory(async () =>
        {
            entered.SetResult();
            await resume.Task;
            return [Environment()];
        }));
        var selecting = service.SelectAsync("env-a", default);
        await entered.Task;
        if (switchProfile)
            await _profiles.CreateAsync(Profile("home-b"), default);
        else
            await _profiles.UpdateAsync(new(Name: "Before"), new(Name: "After"), default);
        resume.SetResult();
        await Assert.ThrowsAsync<UsageException>(() => selecting);
        Assert.Null(_selection.Active);
        Assert.All(await _profiles.GetProfilesAsync(default), p => Assert.Null(p.DefaultEnvironment));
    }

    [Fact]
    public async Task LateSecondaryWriteCannotShadowNewerCommittedDefault()
    {
        await _profiles.CreateAsync(Profile(), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _selection.BeforeWrite = async environment =>
        {
            if (environment.EnvironmentId == "env-a")
            {
                entered.SetResult();
                await resume.Task;
            }
        };
        var service = Service();
        var selectingA = service.SelectAsync("env-a", default);
        await entered.Task;
        await service.SelectAsync("env-b", default);
        resume.SetResult();
        await selectingA;
        Assert.Equal("env-a", _selection.Active?.EnvironmentId);
        Assert.Equal("env-b", (await service.ResolveForCommandAsync(null, default)).EnvironmentId);
    }

    [Fact]
    public async Task SecondaryWriteFailureReportsCommittedDefaultAndPreservesErrorContract()
    {
        await _profiles.CreateAsync(Profile(), default);
        var original = new AgentHostingStateException("Cannot persist selection.", "Repair selection storage.");
        _selection.BeforeWrite = _ => throw original;
        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service().SelectAsync("env-a", default));
        Assert.Equal("env-a", (await _profiles.GetActiveProfileAsync(default))?.DefaultEnvironment?.EnvironmentId);
        Assert.Contains("committed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original.ErrorCode, error.ErrorCode);
        Assert.Equal(original.ExitCode, error.ExitCode);
        Assert.Equal(original.ErrorKind, error.ErrorKind);
        Assert.Equal(original.Remediation, error.Remediation);
        Assert.Equal("env-a", (await Service().ResolveForCommandAsync(null, default)).EnvironmentId);
    }

    [Fact]
    public async Task LegacySelectionIsUnbound_ReadOnly_AndSurvivesIdentityChange()
    {
        var path = Path.Combine(_directory, "selection.json");
        const string original = """
            {"schemaVersion":1,"active":{"environmentId":"env-a","displayName":"Legacy","tenantId":"tenant-a",
            "apiEndpoint":"https://env.api.powerplatform.com","apiAudience":"https://api.powerplatform.com/"}}
            """;
        await File.WriteAllTextAsync(path, original);
        var profile = await _profiles.CreateAsync(Profile(), default);
        var service = Service(selection: new FileEnvironmentSelectionStore(path));
        Assert.False((await service.GetActiveBindingAsync(default))?.IsBound);
        await Assert.ThrowsAsync<EnvironmentNotSelectedException>(() => service.ResolveForCommandAsync(null, default));
        Assert.False(await service.ClearSelectionsOnIdentityChangeAsync(profile, Profile("home-b"), default));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InactiveExactIdUpdateIsAtomic_AndCannotRetargetAfterReordering()
    {
        var first = await _profiles.CreateAsync(Profile() with { Name = "First" }, default);
        var active = await _profiles.CreateAsync(Profile("home-b") with { Name = "Active" }, default);
        first = (await _profiles.GetProfilesAsync(default)).Single(p => p.ProfileId == first.ProfileId);
        await Assert.ThrowsAsync<UsageException>(() => UpdateExact(first, new(Name: "Active", DefaultEnvironment: Selection())));
        Assert.Null((await _profiles.GetProfilesAsync(default)).Single(p => p.ProfileId == first.ProfileId).DefaultEnvironment);
        var updated = await UpdateExact(first, new(Name: "Changed", DefaultEnvironment: Selection()));
        Assert.False(updated.IsActive);
        Assert.Equal("Changed", updated.Name);
        Assert.Equal("env-a", updated.DefaultEnvironment?.EnvironmentId);
        Assert.Equal(active.ProfileId, (await _profiles.GetActiveProfileAsync(default))?.ProfileId);
        await Assert.ThrowsAsync<UsageException>(() => UpdateExact(first, new(Name: "Stale", DefaultEnvironment: Selection("env-b"))));
        await _profiles.DeleteAsync(new(Name: "Changed"), default);
        await _profiles.CreateAsync(Profile("home-c") with { Name = "Changed" }, default);
        await Assert.ThrowsAsync<UsageException>(() => UpdateExact(updated, new(Name: "Retargeted")));
        Assert.DoesNotContain(await _profiles.GetProfilesAsync(default), p => p.Name == "Retargeted" || p.Name == "Stale");
    }

    private Task<AuthProfile> UpdateExact(AuthProfile profile, AuthProfileUpdate update)
        => _profiles.UpdateAsync(profile.ProfileId, profile.Revision, update, default);

    [Fact]
    public async Task EqualReplacementRebindsStagedDefaultToCommittedId_IncludingDacIdentity()
    {
        var profile = Profile() with { CredentialMethod = AuthCredentialMethod.DefaultAzureCredential };
        var first = await _profiles.CreateAsync(profile, default);
        var staged = profile with { ProfileId = "transient-stage-id" };
        staged = staged with { DefaultEnvironment = EnvironmentProfileBinding.BindDefault(staged, Selection("env-b")) };
        var committed = await _profiles.CreateAsync(staged, default);
        Assert.Equal(first.ProfileId, committed.ProfileId);
        Assert.Equal(committed.ProfileId, committed.DefaultEnvironment?.Owner?.ProfileId);
        Assert.Null(committed.HomeAccountId);
        Assert.Equal("env-b", (await Service().ResolveForCommandAsync(null, default)).EnvironmentId);
        Assert.Null((await new FileAuthProfileStore(Path.Combine(_directory, "profiles.json")).GetActiveProfileAsync(default))?.HomeAccountId);
    }

    [Fact]
    public async Task CapturedProfileRemainsAWhileActiveSwitchesDuringInventory()
    {
        var a = await _profiles.CreateAsync(Profile(), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new Factory(new Inventory(async () =>
        {
            entered.SetResult();
            await resume.Task;
            return [Environment() with { DisplayName = "Friendly A" }];
        }));
        var service = FactoryService(factory);
        var resolving = service.ResolveForCommandAsync("Friendly", default);
        await entered.Task;
        await _profiles.CreateAsync(Profile("home-b"), default);
        resume.SetResult();
        var resolved = await resolving;
        Assert.Equal(a.ProfileId, Assert.Single(factory.Contexts).Profile.ProfileId);
        Assert.Equal(a, resolved.Context?.Profile);
        Assert.Equal(PowerPlatformApiTargetConfig.Public, resolved.Context?.Target);
    }

    [Theory]
    [InlineData(CloudInstance.UsNat, PowerPlatformApiRing.Production)]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Test)]
    public async Task FactoryUsesStagedProfileAndItsRegisteredTarget_NotActivePublicProfile(CloudInstance cloud, PowerPlatformApiRing ring)
    {
        await _profiles.CreateAsync(Profile(), default);
        var staged = Profile("stage") with { ProfileId = "stage-id", Cloud = cloud, Ring = ring };
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud, ring);
        var factory = new Factory(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
            [Environment() with { DisplayName = "Staged Environment", ApiEndpoint = target.GlobalEndpoint, ApiAudience = target.Audience }])));
        var resolved = await FactoryService(factory).ResolveForProfileAsync(staged, "Staged", default);
        Assert.Same(staged, Assert.Single(factory.Contexts).Profile);
        Assert.Equal(target, resolved.Context?.Target);
        Assert.Equal(PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, "env-a"), resolved.ApiEndpoint);
    }

    [Fact]
    public async Task FactoryValidatesTheExactInactiveProfileWithoutActivatingIt()
    {
        var first = await _profiles.CreateAsync(Profile(), default);
        var active = await _profiles.CreateAsync(Profile("home-b"), default);
        var inactive = (await _profiles.GetProfilesAsync(default)).Single(profile => profile.ProfileId == first.ProfileId);
        var factory = new Factory(new Inventory(() =>
            Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>([Environment()])));

        var resolved = await FactoryService(factory).ResolveForProfileAsync(inactive, "env-a", default);

        Assert.False(inactive.IsActive);
        Assert.Same(inactive, Assert.Single(factory.Contexts).Profile);
        Assert.Same(inactive, resolved.Context?.Profile);
        Assert.Equal(active.ProfileId, (await _profiles.GetActiveProfileAsync(default))?.ProfileId);
    }

    [Theory]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Production)]
    [InlineData(CloudInstance.Public, PowerPlatformApiRing.Test)]
    public async Task FixedClientRejectsBothCloudAndRingChangesBeforeInventory(CloudInstance cloud, PowerPlatformApiRing ring)
    {
        await _profiles.CreateAsync(Profile() with { Cloud = cloud, Ring = ring }, default);
        var client = new Inventory(() => throw new InvalidOperationException("Must not enumerate."));
        await Assert.ThrowsAsync<CloudRetargetedException>(() => Service(client).ListAvailableAsync(default));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task CapturedMetadataAccessorNeverLooksUpActiveOrConstructsClient()
    {
        var captured = await _profiles.CreateAsync(Profile() with { DefaultEnvironment = Selection() }, default);
        await _profiles.CreateAsync(Profile("home-b"), default);
        var factory = new Factory(new Inventory(() => throw new InvalidOperationException("Must not enumerate.")));
        var service = new EnvironmentSelectionService(factory, _selection, new UnreadableProfiles(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        Assert.True((await service.GetActiveBindingAsync(captured, default))?.IsBound);
        Assert.Empty(factory.Contexts);
        Assert.Equal("env-a", (await Service().ResolveForProfileAsync(captured, "env-a", default)).EnvironmentId);
    }

    [Theory]
    [InlineData("env-a", "env-a")]
    [InlineData("https://a.crm.dynamics.com/", "env-a")]
    [InlineData("Production", "env-a")]
    [InlineData("Preview", "env-b")]
    public async Task LookupPrefersExactCoordinatesBeforePartialDisplayNames(string requested, string expected)
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        var inventory = new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
        [
            Environment() with { DisplayName = "Production", InstanceUrl = "https://a.crm.dynamics.com/" },
            Environment("env-b") with { DisplayName = "Production Preview" },
        ]));
        Assert.Equal(expected, (await Service(inventory).ResolveForProfileAsync(profile, requested, default)).EnvironmentId);
    }

    [Fact]
    public async Task AmbiguousPartialOrDuplicateExactNamesNeverPickFirstMatch()
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        foreach (var names in new[] { new[] { "Production A", "Production B" }, new[] { "Production", "Production" } })
        {
            var service = Service(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
                [Environment() with { DisplayName = names[0] }, Environment("env-b") with { DisplayName = names[1] }])));
            await Assert.ThrowsAsync<UsageException>(() => service.ResolveForProfileAsync(profile, "Production", default));
        }
    }

    [Theory]
    [InlineData("http://a.crm.dynamics.com/")]
    [InlineData("https://a.crm.dynamics.com:8443/")]
    [InlineData("https://user@a.crm.dynamics.com/")]
    [InlineData("https://a.crm.dynamics.com/?private=value")]
    [InlineData("https://a.crm.dynamics.com/#private")]
    [InlineData("https://attacker.example/")]
    public async Task LookupRejectsInvalidOrUntrustedDataverseUrlBeforeConstructingClient(string requested)
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        var factory = new Factory(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
            [Environment() with { InstanceUrl = requested }])));
        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => FactoryService(factory).ResolveForProfileAsync(profile, requested, default));
        Assert.Empty(factory.Contexts);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("user@", error.Message);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("endpoint-query")]
    [InlineData("dataverse")]
    [InlineData("studio")]
    public async Task AllInventoryAndDefaultSourcesValidateAdvertisedCoordinates(string invalid)
    {
        var selection = invalid switch
        {
            "audience" => Selection() with { ApiAudience = "https://attacker.example/" },
            "endpoint-query" => Selection() with { ApiEndpoint = new Uri("https://env.api.powerplatform.com/?private=value") },
            "dataverse" => Selection() with { InstanceUrl = "https://attacker.example/" },
            _ => Selection(),
        };
        var profile = await _profiles.CreateAsync(Profile() with { DefaultEnvironment = selection }, default);
        var inventory = Environment() with
        {
            ApiEndpoint = selection.ApiEndpoint, ApiAudience = selection.ApiAudience, InstanceUrl = selection.InstanceUrl,
            AgentManagementUrl = invalid == "studio" ? "https://attacker.example/" : null,
        };
        var service = Service(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>([inventory])));
        await Assert.ThrowsAsync<EndpointTrustException>(() => service.ResolveForProfileAsync(profile, "env-a", default));
        if (invalid != "studio")
            await Assert.ThrowsAsync<EndpointTrustException>(() => service.ResolveForCommandAsync(null, default));
    }

    [Fact]
    public async Task ExactIdWinsOverAConflictingDisplayName_AndDefaultResourceIdsRemainIntact()
    {
        const string defaultId = "Default-11111111-1111-1111-1111-111111111111";
        var profile = await _profiles.CreateAsync(Profile(), default);
        var service = Service(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
        [
            Environment(defaultId) with { UniqueName = "unique-default" },
            Environment("env-b") with { DisplayName = defaultId },
        ])));
        var exact = await service.ResolveForProfileAsync(profile, defaultId, default);
        Assert.Equal(defaultId, exact.EnvironmentId);
        Assert.Equal(defaultId, (await service.ResolveForProfileAsync(profile,
            "/providers/Microsoft.PowerApps/environments/" + defaultId, default)).EnvironmentId);
        Assert.Equal(defaultId, (await service.ResolveForProfileAsync(profile, "unique-default", default)).EnvironmentId);
    }

    [Theory]
    [InlineData(CloudInstance.China, null)]
    [InlineData(CloudInstance.UsNat, "https://gateway.island.powerapps.com/")]
    [InlineData(CloudInstance.UsSec, "https://gateway.island.powerapps.com/")]
    public async Task UnsupportedCloudRoutesAndUnverifiedGatewaysFailTyped(CloudInstance cloud, string? gateway)
    {
        var profile = Profile() with { ProfileId = "staged", Cloud = cloud };
        var target = PowerPlatformApiTargetSelection.TargetFor(cloud);
        var factory = new Factory(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
            [Environment() with { ApiEndpoint = target.GlobalEndpoint, ApiAudience = target.Audience, AgentManagementUrl = gateway }])));
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => FactoryService(factory).ResolveForProfileAsync(profile, "env-a", default));
    }

    [Fact]
    public async Task OwnedSelectionRoundTripsThroughV2_WithoutExposingCapturedContext()
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        var path = Path.Combine(_directory, "selection.json");
        var store = new FileEnvironmentSelectionStore(path);
        var service = Service(selection: store);
        await service.SelectAsync("env-a", default);
        var reloaded = await new FileEnvironmentSelectionStore(path).GetActiveAsync(default);
        Assert.Equal(profile.ProfileId, reloaded?.Owner?.ProfileId);
        Assert.NotEmpty(reloaded!.DefaultGeneration!);
        Assert.Null(reloaded.Context);
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"schemaVersion\": 2", json);
        Assert.DoesNotContain("\"context\"", json);
        Assert.DoesNotContain(profile.Username, json);
        Assert.True((await service.GetActiveBindingAsync(default))?.IsBound);
    }

    [Theory]
    [InlineData("\"owner\":{}")]
    [InlineData("\"owner\":{\"profileId\":\"a\",\"cloud\":\"invalid\",\"ring\":\"Production\",\"tenantId\":\"tenant-a\",\"identityFingerprint\":\"hash\"}")]
    [InlineData("\"owner\":{\"profileId\":\"a\",\"cloud\":\"Public\",\"ring\":\"Production\",\"tenantId\":\"tenant-a\",\"identityFingerprint\":\"hash\"}")]
    public async Task MalformedV2OwnerIsTypedCorruption_NotASilentlyUnboundSelection(string owner)
    {
        var path = Path.Combine(_directory, "selection.json");
        await File.WriteAllTextAsync(path, $$$"""
            {"schemaVersion":2,"active":{"environmentId":"env-a","displayName":"Legacy","tenantId":"tenant-a",
            "apiEndpoint":"https://env.api.powerplatform.com","apiAudience":"https://api.powerplatform.com/",{{{owner}}}
            }}
            """);
        await Assert.ThrowsAsync<AgentHostingStateException>(() => new FileEnvironmentSelectionStore(path).GetActiveAsync(default));
    }

    [Fact]
    public async Task DefaultLookupUsesSingleCapturedProfile_EvenIfStoreSwitchesDuringSelectionRead()
    {
        var a = await _profiles.CreateAsync(Profile() with { DefaultEnvironment = Selection() }, default);
        var switching = new SwitchingSelectionStore(async () => await _profiles.CreateAsync(Profile("home-b"), default));
        var service = Service(new Inventory(() => throw new InvalidOperationException("No inventory for default lookup.")), switching);
        var resolved = await service.ResolveForCommandAsync(null, default);
        Assert.Equal("env-a", resolved.EnvironmentId);
        Assert.Equal(a.ProfileId, resolved.Context?.Profile.ProfileId);
        Assert.NotEqual(a.ProfileId, (await _profiles.GetActiveProfileAsync(default))?.ProfileId);
    }

    [Fact]
    public async Task UrlLookupDoesNotParseUnrelatedUnreachableOrganizationsAsTrustedCoordinates()
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        var service = Service(new Inventory(() => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
        [
            Environment("env-b") with { InstanceUrl = "not-a-url" },
            Environment() with { InstanceUrl = "https://a.crm.dynamics.com/" },
        ])));
        Assert.Equal("env-a", (await service.ResolveForProfileAsync(profile, "https://a.crm.dynamics.com", default)).EnvironmentId);
    }

    [Theory]
    [InlineData("""{"schemaVersion":99,"schemaVersion":2,"active":null}""")]
    [InlineData("""{"schemaVersion":2,"active":null,"active":null}""")]
    public async Task DuplicateKnownSelectionMetadataIsTypedCorruption(string json)
    {
        var path = Path.Combine(_directory, "selection.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<AgentHostingStateException>(() => new FileEnvironmentSelectionStore(path).GetActiveAsync(default));
    }

    [Fact]
    public async Task MalformedPersistedEndpointIsNotEchoedIntoDiagnostics()
    {
        var path = Path.Combine(_directory, "selection.json");
        await File.WriteAllTextAsync(path, """
            {"schemaVersion":1,"active":{"environmentId":"env-a","displayName":"Legacy","tenantId":"tenant-a",
            "apiEndpoint":"private-endpoint-value","apiAudience":"https://api.powerplatform.com/"}}
            """);
        var error = await Assert.ThrowsAsync<AgentHostingStateException>(() => new FileEnvironmentSelectionStore(path).GetActiveAsync(default));
        Assert.DoesNotContain("private-endpoint-value", error.Message);
    }

    [Fact]
    public async Task UrlLookupRequiresInventoryLinkedUrl_NotAUrlShapedDisplayName()
    {
        var profile = await _profiles.CreateAsync(Profile(), default);
        var service = FactoryService(new Factory(new Inventory(
            () => Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
                [Environment("env-a") with { DisplayName = "https://a.crm.dynamics.com", InstanceUrl = "https://b.crm.dynamics.com" }]))));
        await Assert.ThrowsAsync<EnvironmentNotFoundException>(() =>
            service.ResolveForProfileAsync(profile, "https://a.crm.dynamics.com", default));
    }

    [Theory]
    [InlineData(CloudInstance.Public)]
    [InlineData(CloudInstance.UsGov)]
    [InlineData(CloudInstance.China)]
    public async Task UnboundLegacyMetadataRemainsVisibleAcrossProfileCloudsWithoutClientConstruction(CloudInstance storedCloud)
    {
        var target = PowerPlatformApiTargetSelection.TargetFor(storedCloud);
        await _selection.SetActiveAsync(Selection() with { ApiEndpoint = target.GlobalEndpoint, ApiAudience = target.Audience }, default);
        var captured = await _profiles.CreateAsync(Profile() with
        {
            Cloud = CloudInstance.UsGov,
            Authority = "https://login.microsoftonline.us/organizations",
        }, default);
        var factory = new Factory(new Inventory(() => throw new InvalidOperationException("No inventory")));
        var service = FactoryService(factory);

        var binding = await service.GetActiveBindingAsync(captured, default);
        Assert.NotNull(binding);
        Assert.False(binding.IsBound);
        Assert.Equal("env-a", binding.Environment.EnvironmentId);
        Assert.Empty(factory.Contexts);
    }

    [Fact]
    public async Task InvalidInjectedSelectionOwnerIsTypedCorruptionBeforeRegistryLookup()
    {
        var profile = await _profiles.CreateAsync(Profile() with { DefaultEnvironment = Selection() }, default);
        await _selection.SetActiveAsync(profile.DefaultEnvironment! with
        {
            Owner = profile.DefaultEnvironment.Owner! with { Cloud = (CloudInstance)12345 },
        }, default);
        var service = Service();
        await Assert.ThrowsAsync<AgentHostingStateException>(() => service.GetActiveBindingAsync(profile, default));
        await Assert.ThrowsAsync<AgentHostingStateException>(() => service.ResolveForCommandAsync(null, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContainedLegacyProfileDefaultHasOwnershipEvidence_AndStillRequiresTenantBinding(bool compatibleTenant)
    {
        var captured = await _profiles.CreateAsync(Profile(), default);
        captured = captured with
        {
            DefaultEnvironment = Selection() with { TenantId = compatibleTenant ? "tenant-a" : "tenant-b" },
        };
        var service = Service();
        if (compatibleTenant)
            Assert.True((await service.GetActiveBindingAsync(captured, default))?.IsBound);
        else
            await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(() => service.GetActiveBindingAsync(captured, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompletePersistedProfileDefaultFailsTypedBeforeReturningCoordinates(bool missingId)
    {
        await _profiles.CreateAsync(Profile() with
        {
            DefaultEnvironment = missingId ? Selection() with { EnvironmentId = "" } : Selection() with { ApiEndpoint = null! },
        }, default);
        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service().ResolveForCommandAsync(null, default));
    }

    private sealed class SwitchingSelectionStore(Func<Task> switchProfile) : IEnvironmentSelectionStore
    {
        public async Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken) { await switchProfile(); return null; }
        public Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private EnvironmentSelectionService FactoryService(Factory factory) =>
        new(factory, _selection, _profiles, new FixedClock(DateTimeOffset.UnixEpoch));

    private sealed class Factory(IEnvironmentClient client) : IProfileEnvironmentClientFactory
    {
        public List<EnvironmentResolutionContext> Contexts { get; } = [];
        public IEnvironmentClient CreateClient(EnvironmentResolutionContext context)
        {
            Contexts.Add(context);
            return client;
        }
    }

    private sealed class UnreadableProfiles : IAuthProfileStore
    {
        public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Metadata must use the captured profile.");
        public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetActiveAsync(string accountKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Inventory(Func<Task<IReadOnlyList<PowerPlatformEnvironment>>> list) : IEnvironmentClient
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return list();
        }
    }

    private sealed class SelectionStore : IEnvironmentSelectionStore
    {
        public ActiveEnvironment? Active { get; private set; }
        public Func<ActiveEnvironment, Task>? BeforeWrite { get; set; }
        public Task<ActiveEnvironment?> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Active);
        public async Task SetActiveAsync(ActiveEnvironment environment, CancellationToken cancellationToken)
        {
            if (BeforeWrite is not null) await BeforeWrite(environment);
            Active = environment;
        }
        public Task ClearAsync(CancellationToken cancellationToken) { Active = null; return Task.CompletedTask; }
    }
}
