using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileResourceConsumerTests
{
    private const string Application = "a19c2c4d-ef49-47ec-92b1-f761f33b3f16";
    private const string GuestOid = "498d3c17-4f76-44a7-94b1-68cc7a50b729";
    private static AuthProfile Profile(AuthPrincipalType mode = AuthPrincipalType.User) => new()
    {
        ProfileId = "a", Revision = 17, AccountKey = "different-home-oid.home-tenant",
        Username = "guest@contoso.com", TenantId = EnvironmentFixtures.ContosoTenant,
        ClientId = mode == AuthPrincipalType.User ? PowerPlatformApiPermissions.AgentHostCliApplicationId : Application,
        PrincipalType = mode,
        CredentialMethod = mode == AuthPrincipalType.User ? AuthCredentialMethod.Browser : AuthCredentialMethod.ClientSecret,
    };

    private static string Jwt(AuthProfile profile, string? oid = GuestOid, string? scope = PowerPlatformApiPermissions.AppsRead,
        string? app = null, string? tenant = null, string? audience = null, string? mode = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            aud = audience ?? PowerPlatformApiTargetConfig.Public.Audience,
            appid = app ?? profile.ClientId, tid = tenant ?? profile.TenantId, oid, scp = scope, idtyp = mode,
        });
        return "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
    }

    [Theory]
    [InlineData(AuthPrincipalType.User, "https://api.powerplatform.com//.default")]
    [InlineData(AuthPrincipalType.ServicePrincipal, "https://api.powerplatform.com/.default")]
    public async Task ResourceBoundary_ChoosesModeDefaultWhenScopesAreNotSupplied(AuthPrincipalType mode, string expected)
    {
        var profile = Profile(mode);
        var credentials = new Credentials(Jwt(profile, scope: mode == AuthPrincipalType.User ? PowerPlatformApiPermissions.AppsRead : null));
        var tokens = new ProfileResourceTokenProvider(credentials);
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);

        await tokens.AcquireAsync(context, context.Target.Audience, profile.TenantId!, [PowerPlatformApiPermissions.AppsRead], default);
        Assert.Equal([expected], credentials.Scopes);
    }

    [Theory]
    [InlineData(AuthPrincipalType.User, AuthCredentialMethod.Browser, false)]
    [InlineData(AuthPrincipalType.User, AuthCredentialMethod.Browser, true)]
    [InlineData(AuthPrincipalType.ServicePrincipal, AuthCredentialMethod.ClientSecret, false)]
    [InlineData(AuthPrincipalType.ServicePrincipal, AuthCredentialMethod.ClientSecret, true)]
    [InlineData(AuthPrincipalType.User, AuthCredentialMethod.DefaultAzureCredential, false)]
    [InlineData(AuthPrincipalType.User, AuthCredentialMethod.DefaultAzureCredential, true)]
    [InlineData(AuthPrincipalType.ServicePrincipal, AuthCredentialMethod.DefaultAzureCredential, false)]
    [InlineData(AuthPrincipalType.ServicePrincipal, AuthCredentialMethod.DefaultAzureCredential, true)]
    public async Task ResourceBoundaryPreservesExplicitScopesForEveryCredentialMode(
        AuthPrincipalType mode, AuthCredentialMethod method, bool throughBoundProvider)
    {
        var profile = Profile(mode) with
        {
            CredentialMethod = method,
            ClientId = method == AuthCredentialMethod.DefaultAzureCredential ? Application : Profile(mode).ClientId,
        };
        var credentials = new Credentials(Jwt(profile, scope: mode == AuthPrincipalType.User ? PowerPlatformApiPermissions.AppsRead : null));
        var tokens = new ProfileResourceTokenProvider(credentials);
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);
        var named = new PpapiScopeBuilder().ExplicitScopes(context.Target.Audience, PowerPlatformApiPermissions.AppsRead);

        if (throughBoundProvider)
            await new ProfileBoundTokenProvider(tokens, context).AcquireTokenSilentAsync(named, profile.TenantId, default);
        else
            await tokens.AcquireAsync(context, context.Target.Audience, profile.TenantId!, [PowerPlatformApiPermissions.AppsRead], default, named);

        Assert.Equal(named, credentials.Scopes);
    }

    [Theory]
    [InlineData(null, null, null, "app", null)]
    [InlineData("other-app", null, null, "app", TokenClaimFailure.WrongApplication)]
    [InlineData(null, "other-tenant", null, "app", TokenClaimFailure.WrongTenant)]
    [InlineData(null, null, "https://wrong.example", "app", TokenClaimFailure.WrongAudience)]
    [InlineData(null, null, null, "user", TokenClaimFailure.WrongPrincipalMode)]
    public async Task AppOnly_NoDelegatedScopeStillRequiresBoundApplicationTenantAudienceAndMode(
        string? app, string? tenant, string? audience, string mode, TokenClaimFailure? expected)
    {
        var profile = Profile(AuthPrincipalType.ServicePrincipal);
        var tokens = new ProfileResourceTokenProvider(new Credentials(Jwt(profile, scope: null, app: app, tenant: tenant, audience: audience, mode: mode)));
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);
        if (expected is { } failure)
        {
            var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
                tokens.AcquireAsync(context, context.Target.Audience, profile.TenantId!, [PowerPlatformApiPermissions.AppsCreate], default));
            Assert.Equal(failure, error.Failure);
        }
        else
        {
            await tokens.AcquireAsync(context, context.Target.Audience, profile.TenantId!, [PowerPlatformApiPermissions.AppsCreate], default);
        }
    }

    [Theory]
    [InlineData("[\"app\"]")]
    [InlineData("1")]
    [InlineData("{}")]
    public void ClaimReaderDoesNotTreatMalformedPrincipalModeAsAbsent(string identityType)
    {
        var payload = "{\"idtyp\":" + identityType + "}";
        var token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

        var error = Assert.Throws<TokenClaimException>(() => PpapiTokenClaimsReader.Read(token));

        Assert.Equal(TokenClaimFailure.Malformed, error.Failure);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Principal_UsesGuestResourceOidForCapturedDacUserWithoutHomeAccountId()
    {
        var a = Profile() with { CredentialMethod = AuthCredentialMethod.DefaultAzureCredential, HomeAccountId = null, ClientId = Application };
        var profiles = new InMemoryAuthProfileStore();
        await profiles.UpsertAsync(a, default);
        var credentials = new Credentials(Jwt(a)) { DuringAcquisition = () => profiles.UpsertAsync(Profile() with { ProfileId = "b" }, default) };
        var resolver = new ManagedAgentPrincipalResolver(profiles, resourceTokens: new ProfileResourceTokenProvider(credentials));
        var environment = new CopilotEnvironmentContext
        {
            EnvironmentId = "env-a", TenantId = a.TenantId!, DataverseOrganization = new Uri("https://contoso.crm.dynamics.com"),
            Context = new(a, PowerPlatformApiTargetConfig.Public),
        };

        var bound = await resolver.BindUserAsync(environment, default);
        Assert.Equal(GuestOid, (await resolver.ResolveAsync(bound, default))?.ObjectId);
        Assert.Equal(a.Username, await resolver.ResolveUsernameAsync(bound, default));
        Assert.Same(a, credentials.Profile);
        Assert.Equal(a.TenantId, credentials.Tenant);
        Assert.Equal(["https://api.powerplatform.com/.default"], credentials.Scopes);
        Assert.Equal(1, credentials.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("placeholder")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ProductPrincipal_RequiresUsableValidatedOid(string? oid)
    {
        var profile = Profile();
        var resolver = new ManagedAgentPrincipalResolver(new InMemoryAuthProfileStore(),
            resourceTokens: new ProfileResourceTokenProvider(new Credentials(Jwt(profile, oid: oid))));
        var error = await Assert.ThrowsAsync<TokenClaimException>(() => resolver.BindUserAsync(new CopilotEnvironmentContext
        {
            EnvironmentId = "env-a", TenantId = profile.TenantId!, DataverseOrganization = new Uri("https://contoso.crm.dynamics.com"),
            Context = new(profile, PowerPlatformApiTargetConfig.Public),
        }, default));
        Assert.Equal(TokenClaimFailure.MissingObjectId, error.Failure);
    }

    [Fact]
    public async Task ProductPrincipal_DoesNotFallBackToUnvalidatedHomeIdentity()
    {
        var resolver = new ManagedAgentPrincipalResolver(
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => resolver.ResolveAsync(default));

        AssertAuthenticationContextFailure(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductPrincipal_MissingContextIsAnInternalFailureBeforeAcquisition(bool useResourceProvider)
    {
        var profile = Profile();
        var credentials = new Credentials(Jwt(profile));
        var resolver = new ManagedAgentPrincipalResolver(new InMemoryAuthProfileStore(),
            tokens: useResourceProvider ? null : new NoLegacyTokenProvider(),
            resourceTokens: useResourceProvider ? new ProfileResourceTokenProvider(credentials) : null);

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => resolver.BindUserAsync(new CopilotEnvironmentContext
        {
            EnvironmentId = "env-a", TenantId = profile.TenantId!, DataverseOrganization = new Uri("https://contoso.crm.dynamics.com"),
        }, default));

        AssertAuthenticationContextFailure(error);
        Assert.Equal(0, credentials.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductPrincipal_RejectsMismatchedValidatedResourceBinding(bool wrongTenant)
    {
        var profile = Profile();
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);
        var returnedContext = wrongTenant ? context : new(profile with { ProfileId = "b" }, context.Target);
        var returnedTenant = wrongTenant ? "22222222-2222-2222-2222-222222222222" : profile.TenantId!;
        var token = await new ProfileResourceTokenProvider(new Credentials(Jwt(profile, tenant: returnedTenant)))
            .AcquireAsync(returnedContext, context.Target.Audience, returnedTenant, [PowerPlatformApiPermissions.AppsRead], default);
        var resolver = new ManagedAgentPrincipalResolver(new InMemoryAuthProfileStore(),
            resourceTokens: new CapturedResourceTokenProvider(token));

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => resolver.BindUserAsync(new CopilotEnvironmentContext
        {
            EnvironmentId = "env-a", TenantId = profile.TenantId!, DataverseOrganization = new Uri("https://contoso.crm.dynamics.com"),
            Context = context,
        }, default));

        AssertAuthenticationContextFailure(error);
    }

    [Fact]
    public async Task ProductPrincipal_RejectsMismatchedLegacyProviderBeforeAcquisition()
    {
        var profile = Profile();
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);
        var credentials = new Credentials(Jwt(profile));
        var resolver = new ManagedAgentPrincipalResolver(new InMemoryAuthProfileStore(),
            tokens: new ProfileBoundTokenProvider(new ProfileResourceTokenProvider(credentials),
                new(profile with { ProfileId = "b" }, context.Target)));

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => resolver.BindUserAsync(new CopilotEnvironmentContext
        {
            EnvironmentId = "env-a", TenantId = profile.TenantId!, DataverseOrganization = new Uri("https://contoso.crm.dynamics.com"),
            Context = context,
        }, default));

        AssertAuthenticationContextFailure(error);
        Assert.Equal(0, credentials.Calls);
    }

    [Fact]
    public async Task CopilotProjection_UsesCapturedSovereignTargetInsteadOfComposedPublicMap()
    {
        var profile = new AuthProfile
        {
            AccountKey = "home", Username = "operator", TenantId = EnvironmentFixtures.ContosoTenant,
            Cloud = CloudInstance.UsGov, ProfileId = "profile-a", Revision = 17,
        };
        var store = new InMemoryAuthProfileStore();
        await store.UpsertAsync(profile, default);
        var service = new EnvironmentSelectionService(new InventoryFactory(),
            new InMemoryEnvironmentSelectionStore(), store, new FixedClock(DateTimeOffset.UnixEpoch));
        var resolver = new CopilotEnvironmentResolver(service, PowerPlatformCloudMap.ForCloud(CloudInstance.Public));

        var inventory = await resolver.ListAsync(default);

        var reachable = Assert.Single(inventory.Reachable);
        Assert.Empty(inventory.Unreachable);
        Assert.NotNull(reachable.Context);
        Assert.Equal(profile.ProfileId, reachable.Context.Profile.ProfileId);
        Assert.Equal(profile.Cloud, reachable.Context.Target.Cloud);
        Assert.Equal(profile.TenantId, reachable.TenantId);
        var json = JsonSerializer.Serialize(reachable);
        Assert.DoesNotContain("Context", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountKey", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AuthPrincipalType.User)]
    [InlineData(AuthPrincipalType.ServicePrincipal)]
    public async Task ConcreteInventoryFactoryUsesAnExplicitInactiveProfileAndMode(AuthPrincipalType mode)
    {
        var a = Profile(mode) with { Ring = PowerPlatformApiRing.Preprod };
        var context = new EnvironmentResolutionContext(a, PowerPlatformApiTargetConfig.Preprod);
        var profiles = new InMemoryAuthProfileStore();
        await profiles.UpsertAsync(Profile() with { ProfileId = "b" }, default);
        var credentials = new Credentials(Jwt(a, audience: context.Target.Audience,
            scope: mode == AuthPrincipalType.User ? PowerPlatformApiPermissions.EnvironmentsRead : null));
        using var handler = new RecordingHttp("{\"value\":[{\"name\":\"env-a\",\"properties\":{\"instanceUrl\":\"https://contoso.crm.dynamics.com\"}}]}");
        using var http = new HttpClient(handler);
        var factory = new ProfilePowerPlatformEnvironmentClientFactory(http, new ProfileResourceTokenProvider(credentials));
        var client = factory.CreateClient(context);

        var row = Assert.Single(await client.ListEnvironmentsAsync(default));

        Assert.Same(a, credentials.Profile);
        Assert.Same(context, row.Context);
        Assert.Equal(a.TenantId, row.TenantId);
        Assert.Equal(context.Target.Audience, row.ApiAudience);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(context.Target.GlobalEndpoint.Host, handler.LastUri?.Host);
        Assert.Equal(Assert.Single(credentials.Scopes), Assert.IsType<PowerPlatformApiEnvironmentClient>(client).InventoryScope);
    }

    [Theory]
    [InlineData("other-app", null, "user", PowerPlatformApiPermissions.EnvironmentsRead, TokenClaimFailure.WrongApplication)]
    [InlineData(null, "other-tenant", "user", PowerPlatformApiPermissions.EnvironmentsRead, TokenClaimFailure.WrongTenant)]
    [InlineData(null, null, "app", PowerPlatformApiPermissions.EnvironmentsRead, TokenClaimFailure.WrongPrincipalMode)]
    [InlineData(null, null, "user", PowerPlatformApiPermissions.AppsRead, TokenClaimFailure.MissingPermission)]
    public async Task InvalidInventoryTokenIsRejectedBeforeHttp(string? app, string? tenant, string mode, string scope,
        TokenClaimFailure failure)
    {
        var profile = Profile();
        using var handler = new RecordingHttp("{\"value\":[]}");
        using var http = new HttpClient(handler);
        var client = new ProfilePowerPlatformEnvironmentClientFactory(http,
            new ProfileResourceTokenProvider(new Credentials(Jwt(profile, app: app, tenant: tenant, mode: mode, scope: scope))))
            .CreateClient(new(profile, PowerPlatformApiTargetConfig.Public));

        var error = await Assert.ThrowsAsync<TokenClaimException>(() => client.ListEnvironmentsAsync(default));

        Assert.Equal(failure, error.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GeographyUsesTheCapturedResourceTargetNotTheLegacyComposedTarget()
    {
        var profile = Profile(AuthPrincipalType.ServicePrincipal) with { Ring = PowerPlatformApiRing.Test };
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Test);
        var credentials = new Credentials(Jwt(profile, audience: context.Target.Audience, scope: null, mode: "app"));
        using var handler = new RecordingHttp("{\"geoLongName\":\"Test region\"}");
        using var http = new HttpClient(handler);
        var client = new PowerPlatformApiEnvironmentGeographyClient(http, new NoGlobalTokenProvider(),
            PowerPlatformApiTargetConfig.Public, new PpapiTokenClaimsValidator(),
            new ProfileResourceTokenProvider(credentials), context);

        Assert.Equal("Test region", await client.GetGeographyAsync(new("env-a", profile.TenantId!), default));
        Assert.Equal(context.Target.Audience + "/.default", Assert.Single(credentials.Scopes));
        Assert.EndsWith(".api.test.powerplatform.com", handler.LastUri!.Host, StringComparison.Ordinal);
        var boundClient = new PowerPlatformApiEnvironmentGeographyClient(http, new ProfileResourceTokenProvider(credentials), context);
        Assert.Equal("Test region", await boundClient.GetGeographyAsync(new("env-a", profile.TenantId!), default));
    }

    private sealed class NoGlobalTokenProvider : ISelectedAccountTokenProvider
    {
        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(string resource, string targetTenantId, CancellationToken ct) =>
            throw new InvalidOperationException("Global profile lookup is forbidden.");
    }

    [Theory]
    [InlineData(AuthPrincipalType.ServicePrincipal, null)]
    [InlineData(AuthPrincipalType.ServicePrincipal, "different-client")]
    [InlineData(AuthPrincipalType.User, null)]
    public async Task AmsCreateUsesTheCapturedModeAndChecksClaimsBeforeMutation(AuthPrincipalType mode, string? wrongClient)
    {
        var a = Profile(mode);
        var profiles = new InMemoryAuthProfileStore();
        await profiles.UpsertAsync(a, default);
        var credentials = new Credentials(Jwt(a, app: wrongClient,
            scope: mode == AuthPrincipalType.User ? PowerPlatformApiPermissions.AppsCreate : null))
        {
            DuringAcquisition = () => profiles.UpsertAsync(Profile() with { ProfileId = "b" }, default),
        };
        var context = new EnvironmentResolutionContext(a, PowerPlatformApiTargetConfig.Public);
        using var handler = new RecordingHttp("{\"id\":\"agent\",\"tenantId\":\"tenant\",\"environmentId\":\"env\",\"displayName\":\"A\",\"repositoryId\":\"repo\"}");
        using var http = new HttpClient(handler);
        var adapter = new AmsAgentCreationAdapter(http, new NoLegacyTokenProvider(),
            profileTokens: new ProfileResourceTokenProvider(credentials));
        var request = new AmsAgentCreationTransportRequest
        {
            Context = context, Endpoint = context.Target.GlobalEndpoint, Audience = context.Target.Audience,
            TenantId = a.TenantId!, EnvironmentId = "env-a", RouteMode = AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            DisplayName = "A", IdempotencyKey = "operation-a", AgentType = 0, Language = 1, LanguageVersion = "10", Origin = "test",
        };
        if (wrongClient is not null)
        {
            var error = await Assert.ThrowsAsync<TokenClaimException>(() => adapter.CreateAsync(request, default));
            Assert.Equal(TokenClaimFailure.WrongApplication, error.Failure);
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            Assert.Equal("agent", (await adapter.CreateAsync(request, default)).AgentId);
            Assert.Equal(1, handler.Calls);
        }
        Assert.Same(a, credentials.Profile);
        Assert.Equal(a.TenantId, credentials.Tenant);
        Assert.Equal(mode == AuthPrincipalType.User ? "https://api.powerplatform.com//.default" : "https://api.powerplatform.com/.default",
            Assert.Single(credentials.Scopes));
    }

    private sealed class NoLegacyTokenProvider : ITokenProvider
    {
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? tenant, CancellationToken ct) =>
            throw new InvalidOperationException("Global profile lookup is forbidden.");
        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? tenant, CancellationToken ct) =>
            throw new InvalidOperationException("Global profile lookup is forbidden.");
    }

    [Fact]
    public async Task AmsCreateMissingContextIsAnInternalFailureBeforeAcquisitionOrHttp()
    {
        var profile = Profile();
        var context = new EnvironmentResolutionContext(profile, PowerPlatformApiTargetConfig.Public);
        var credentials = new Credentials(Jwt(profile, scope: PowerPlatformApiPermissions.AppsCreate));
        using var handler = new RecordingHttp("{}");
        using var http = new HttpClient(handler);
        var adapter = new AmsAgentCreationAdapter(http, new NoLegacyTokenProvider(),
            profileTokens: new ProfileResourceTokenProvider(credentials));
        var request = new AmsAgentCreationTransportRequest
        {
            Endpoint = context.Target.GlobalEndpoint, Audience = context.Target.Audience,
            TenantId = profile.TenantId!, EnvironmentId = "env-a", RouteMode = AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            DisplayName = "A", IdempotencyKey = "operation-a", AgentType = 0, Language = 1, LanguageVersion = "10", Origin = "test",
        };

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => adapter.CreateAsync(request, default));

        AssertAuthenticationContextFailure(error);
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BoundLegacyAdapterRejectsAnotherSnapshotBeforeTokenAcquisition()
    {
        var a = Profile();
        var context = new EnvironmentResolutionContext(a, PowerPlatformApiTargetConfig.Public);
        var credentials = new Credentials(Jwt(a, scope: PowerPlatformApiPermissions.AppsCreate));
        using var handler = new RecordingHttp("{}");
        using var http = new HttpClient(handler);
        var adapter = new AmsAgentCreationAdapter(http,
            new ProfileBoundTokenProvider(new ProfileResourceTokenProvider(credentials), context));
        var request = new AmsAgentCreationTransportRequest
        {
            Context = new(a with { ProfileId = "b" }, context.Target),
            Endpoint = context.Target.GlobalEndpoint, Audience = context.Target.Audience,
            TenantId = a.TenantId!, EnvironmentId = "env-a", RouteMode = AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            DisplayName = "A", IdempotencyKey = "operation-a", AgentType = 0, Language = 1, LanguageVersion = "10", Origin = "test",
        };

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => adapter.CreateAsync(request, default));

        AssertAuthenticationContextFailure(error);
        Assert.Equal(0, credentials.Calls);
        Assert.Equal(0, handler.Calls);
    }

    private static void AssertAuthenticationContextFailure(AgentHostingException error)
    {
        Assert.IsType<AuthenticationContextException>(error);
        Assert.Equal(99005, error.ErrorCode);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        Assert.Equal("authentication-context-invalid", error.ErrorKind);
        Assert.Contains("CLI version", error.Remediation, StringComparison.Ordinal);
        Assert.Contains("token provider", error.Remediation, StringComparison.Ordinal);
        Assert.Contains("Do not include tokens or credentials", error.Remediation, StringComparison.Ordinal);
    }

    private sealed class CapturedResourceTokenProvider(ValidatedResourceToken token) : IProfileResourceTokenProvider
    {
        public Task<ValidatedResourceToken> AcquireAsync(EnvironmentResolutionContext context, string audience,
            string tenantId, IReadOnlyList<string> requiredPermissions, CancellationToken cancellationToken,
            IReadOnlyList<string>? explicitScopes = null) => Task.FromResult(token);
    }

    private sealed class RecordingHttp(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class InventoryFactory : IProfileEnvironmentClientFactory, IEnvironmentClient
    {
        private EnvironmentResolutionContext? _context;
        public IEnvironmentClient CreateClient(EnvironmentResolutionContext context)
        {
            _context = context;
            return this;
        }

        public Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PowerPlatformEnvironment>>(
            [
                new()
                {
                    EnvironmentId = "env-a", DisplayName = "A", TenantId = EnvironmentFixtures.ContosoTenant,
                    ApiEndpoint = _context!.Target.GlobalEndpoint, ApiAudience = _context.Target.Audience,
                    InstanceUrl = "https://contoso.crm.microsoftdynamics.us/",
                },
            ]);
    }

    private sealed class Credentials(string token) : IProfileCredentialProvider
    {
        public AuthProfile? Profile { get; private set; }
        public IReadOnlyList<string> Scopes { get; private set; } = [];
        public string? Tenant { get; private set; }
        public int Calls { get; private set; }
        public Func<Task>? DuringAcquisition { get; init; }
        public async Task<TokenAcquisitionResult> AcquireAsync(AuthProfile profile, IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct)
        {
            Profile = profile;
            Scopes = scopes;
            Tenant = targetTenantId;
            Calls++;
            if (DuringAcquisition is not null) await DuringAcquisition();
            return new() { AccessToken = token, TenantId = targetTenantId };
        }
        public Task<AuthProfile> AuthenticateAsync(AuthCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteCreationAsync(AuthProfile profile, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveOwnedCredentialAsync(AuthProfile profile, CancellationToken ct) => throw new NotSupportedException();
    }
}
