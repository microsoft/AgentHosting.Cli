using System.Net;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class NativeAmsRoutingTests
{
    internal const string ControlPlane =
        "https://parp-agentmanagementservice-devuser.wus-cm100.gateway.test.cm.powerapps.com";
    internal const string DataPlane =
        "https://parp-agentmanagementservicedataplane-devuser.wus-il100.gateway.test.island.powerapps.com";
    private const string Tenant = EnvironmentFixtures.ContosoTenant;
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Revision = "55555555-5555-5555-5555-555555555555";
    private const string Deployment = "66666666-6666-6666-6666-666666666666";
    private const string Entra = "77777777-7777-7777-7777-777777777777";
    private static readonly Uri EnvironmentEndpoint =
        PowerPlatformApiDiscovery.GetEnvironmentEndpoint(PowerPlatformApiTargetConfig.Test, Home);

    [Theory]
    [InlineData(PowerPlatformApiRing.Production)]
    [InlineData(PowerPlatformApiRing.Preprod)]
    [InlineData(PowerPlatformApiRing.Test)]
    public void UnsetOverridesKeepNativeFrontDoorRouting(PowerPlatformApiRing ring)
    {
        var services = new ServiceCollection();
        Program.AddAmsServices(services, PowerPlatformApiRingSelection.TargetFor(ring), _ => null);
        using var provider = services.BuildServiceProvider();

        var target = provider.GetRequiredService<AgentCreationTarget>().Resolve(EnvironmentEndpoint);

        Assert.Equal(EnvironmentEndpoint, target.Endpoint);
        Assert.Equal(AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor, target.RouteMode);
        Assert.Null(provider.GetRequiredService<AmsDevEndpoints>().ControlPlane);
        Assert.Null(provider.GetRequiredService<AmsDevEndpoints>().DataPlane);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PersonalEndpointsAcceptOnlyOriginSyntaxAndCaseInsensitiveMatching(
        bool uppercase,
        bool trailingSlash)
    {
        var controlPlane = ControlPlane.Replace("devuser", "dev-user42", StringComparison.Ordinal);
        var dataPlane = DataPlane.Replace("devuser", "dev-user42", StringComparison.Ordinal);
        if (uppercase)
        {
            controlPlane = controlPlane.ToUpperInvariant();
            dataPlane = dataPlane.ToUpperInvariant();
        }

        if (trailingSlash)
        {
            controlPlane += "/";
            dataPlane += "/";
        }

        var endpoints = AmsDevEndpoints.FromConfiguration(
            PowerPlatformApiRing.Test, controlPlane, dataPlane);

        Assert.Equal(new Uri(controlPlane), endpoints.ControlPlane);
        Assert.Equal(new Uri(dataPlane), endpoints.DataPlane);
        Assert.False(PowerPlatformCloudMap.ForTarget(PowerPlatformApiTargetConfig.Test)
            .IsTrusted(endpoints.DataPlane));
    }

    public static IEnumerable<object[]> InvalidEndpoints()
    {
        foreach (var isControlPlane in new[] { true, false })
        {
            var endpoint = isControlPlane ? ControlPlane : DataPlane;
            foreach (var invalid in new[]
            {
                " ",
                "not-an-endpoint",
                "https://external.example",
                "https://127.0.0.1",
                isControlPlane ? DataPlane : ControlPlane,
                endpoint.Replace("https://", "http://", StringComparison.Ordinal),
                endpoint.Replace("https://", "https://user:secret@", StringComparison.Ordinal),
                endpoint.Replace("-devuser.", ".", StringComparison.Ordinal),
                endpoint.Replace("-devuser.", "--devuser.", StringComparison.Ordinal),
                endpoint.Replace("-devuser.", "-devuser-.", StringComparison.Ordinal),
                endpoint.Replace("-devuser.", "-dev_user.", StringComparison.Ordinal),
                endpoint.Replace("-devuser.", "-" + new string('a', 64) + ".", StringComparison.Ordinal),
                endpoint.Replace(".test.", ".prod.", StringComparison.Ordinal),
                endpoint.Replace(".test.", ".preprod.", StringComparison.Ordinal),
                endpoint.Replace("-cm100.", "-il100.", StringComparison.Ordinal)
                    .Replace(".test.cm.", ".test.island.", StringComparison.Ordinal),
                endpoint.Replace("-il100.", "-cm100.", StringComparison.Ordinal)
                    .Replace(".test.island.", ".test.cm.", StringComparison.Ordinal),
                endpoint + ".external.example",
                endpoint + ".",
                endpoint + ":443",
                endpoint + ":8443",
                endpoint + "/agenthosting",
                endpoint + "/a/..",
                endpoint + "//",
                endpoint + "\\",
                endpoint + "/%2f",
                endpoint + "?",
                endpoint + "?token=secret",
                endpoint + "#",
                endpoint + "#fragment",
                " " + endpoint,
                endpoint + "\n",
            })
            {
                // One plane-substitution transform is unchanged for the other plane.
                if (invalid != endpoint)
                {
                    yield return [isControlPlane, invalid];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidEndpoints))]
    public void NativeCompositionRejectsMalformedSharedCrossPlaneOrExternalEndpoints(
        bool isControlPlane,
        string invalid)
    {
        var services = new ServiceCollection();
        var clientFactoryCalled = false;

        var error = Assert.Throws<ArgumentException>(() => Program.AddAmsServices(
            services,
            PowerPlatformApiTargetConfig.Test,
            setting => setting == "AH_CLI_AMS_ENDPOINT"
                ? isControlPlane ? invalid : ControlPlane
                : isControlPlane ? DataPlane : invalid,
            () =>
            {
                clientFactoryCalled = true;
                throw new InvalidOperationException("No transport may be constructed for invalid configuration.");
            }));

        Assert.Contains(
            isControlPlane ? "AH_CLI_AMS_ENDPOINT" : "AH_CLI_AMSDP_ENDPOINT",
            error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.False(clientFactoryCalled);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, ControlPlane, DataPlane)]
    [InlineData(PowerPlatformApiRing.Preprod, ControlPlane, DataPlane)]
    [InlineData(PowerPlatformApiRing.Test, ControlPlane, null)]
    [InlineData(PowerPlatformApiRing.Test, null, DataPlane)]
    [InlineData(PowerPlatformApiRing.Test, ControlPlane, "")]
    [InlineData(PowerPlatformApiRing.Test, "", DataPlane)]
    [InlineData(PowerPlatformApiRing.Test, ControlPlane,
        "https://parp-agentmanagementservicedataplane-otheruser.wus-il100.gateway.test.island.powerapps.com")]
    [InlineData(PowerPlatformApiRing.Test, ControlPlane,
        "https://parp-agentmanagementservicedataplane-devuser.eus-il100.gateway.test.island.powerapps.com")]
    public void NativeCompositionRejectsNonTestPartialOrMismatchedPairs(
        PowerPlatformApiRing ring,
        string? controlPlane,
        string? dataPlane)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => Program.AddAmsServices(
            services,
            PowerPlatformApiRingSelection.TargetFor(ring),
            setting => setting == "AH_CLI_AMS_ENDPOINT" ? controlPlane : dataPlane));

        Assert.Empty(services);
    }

    public static IEnumerable<object[]> InvalidTokenClaims()
    {
        foreach (var operation in Operations)
        {
            foreach (var failure in new[]
            {
                TokenClaimFailure.WrongAudience,
                TokenClaimFailure.WrongApplication,
                TokenClaimFailure.WrongTenant,
                TokenClaimFailure.MissingPermission,
            })
            {
                yield return [operation, failure];
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidTokenClaims))]
    public async Task DirectRoutesStillRequireNativeAudienceClientTenantAndDelegatedPermissions(
        string operation,
        TokenClaimFailure failure)
    {
        var handler = new CapturingPackageHandler(_ =>
            throw new InvalidOperationException("An invalid token must not reach a transport."));
        using var http = new HttpClient(handler);
        var tokens = new ClaimsTokenProvider(failure);
        using var provider = Compose(http, tokens);

        var error = await Assert.ThrowsAsync<TokenClaimException>(() =>
            InvokeAsync(provider, operation));

        Assert.Equal(failure, error.Failure);
        Assert.Empty(handler.Requests);
        Assert.Equal([PowerPlatformApiTargetConfig.Test.Audience], tokens.Resources);
        Assert.Equal([Tenant], tokens.Tenants);
    }

    public static IEnumerable<object[]> TransportFailures()
    {
        foreach (var operation in Operations)
        {
            yield return [operation, HttpStatusCode.TemporaryRedirect];
            yield return [operation, HttpStatusCode.InternalServerError];
        }
    }

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task DirectRoutesDoNotFollowRedirectsOrRetryFailedMutations(
        string operation,
        HttpStatusCode status)
    {
        var handler = new CapturingPackageHandler(_ => status == HttpStatusCode.TemporaryRedirect
            ? FakeHttpResponse.Redirect(status, "https://external.example/collect")
            : FakeHttpResponse.Json(status, """{"error":{"code":"InternalServerError"}}"""));
        using var http = new HttpClient(handler);
        using var provider = Compose(http, new FakePpapiTokenProvider { Scopes = AllScopes });

        var error = await Assert.ThrowsAnyAsync<AgentHostingException>(() => InvokeAsync(provider, operation));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            operation is "create" or "read-agent" or "upload" ? DataPlane : ControlPlane,
            request.Uri.GetLeftPart(UriPartial.Authority));
        Assert.StartsWith($"/agenthosting/tenants/{Tenant}/environments/{Home}/agents", request.Uri.AbsolutePath);
        Assert.True(request.HasAuthorization);
        if (status == HttpStatusCode.TemporaryRedirect)
        {
            Assert.Equal("redirect-rejected", error.ErrorKind);
            Assert.DoesNotContain("external.example", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PersonalRoutingDoesNotTrustPersonalHostsAdvertisedByEnvironmentDiscovery()
    {
        var body = $$$"""
            {"value":[{"name":"{{{Home}}}","properties":{"tenantId":"{{{Tenant}}}","apiEndpoint":"{{{DataPlane}}}"}}]}
            """;
        var handler = new CapturingPackageHandler(_ => FakeHttpResponse.Json(HttpStatusCode.OK, body));
        using var http = new HttpClient(handler);
        using var provider = Compose(http, new FakePpapiTokenProvider
        {
            Scopes = PowerPlatformApiPermissions.EnvironmentsRead,
        });
        var inventory = new PowerPlatformApiEnvironmentClient(
            http,
            provider.GetRequiredService<ITokenProvider>(),
            PowerPlatformApiTargetConfig.Test,
            claimsValidator: provider.GetRequiredService<PpapiTokenClaimsValidator>());

        await Assert.ThrowsAsync<EndpointTrustException>(() =>
            inventory.ListEnvironmentsAsync(default));

        Assert.Equal("api.test.powerplatform.com", Assert.Single(handler.Requests).Uri.Host);
    }

    private static readonly string[] Operations =
        ["create", "read-agent", "upload", "deploy", "read-deployment", "update"];
    private static readonly string AllScopes = string.Join(' ',
        PowerPlatformApiPermissions.AppsCreate,
        PowerPlatformApiPermissions.AppsRead,
        PowerPlatformApiPermissions.AppsUpdate);

    private static ServiceProvider Compose(HttpClient http, ITokenProvider tokens)
    {
        var services = new ServiceCollection();
        Program.AddAmsServices(
            services,
            PowerPlatformApiTargetConfig.Test,
            setting => setting == "AH_CLI_AMS_ENDPOINT" ? ControlPlane : DataPlane,
            () => http);
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ConfigDirectory = AppContext.BaseDirectory,
            InteractionAllowed = false,
        }, PowerPlatformApiTargetConfig.Test);
        services.AddSingleton(tokens);
        return services.BuildServiceProvider();
    }

    private static async Task InvokeAsync(ServiceProvider provider, string operation)
    {
        var creation = provider.GetRequiredService<AgentCreationTarget>().Resolve(EnvironmentEndpoint);
        var reference = new AmsDeploymentReadTransportRequest
        {
            Endpoint = PowerPlatformApiTargetConfig.Test.GlobalEndpoint,
            Audience = PowerPlatformApiTargetConfig.Test.Audience,
            TenantId = Tenant,
            HomeEnvironmentId = Home,
            AgentId = Agent,
            DeploymentId = Deployment,
        };
        using var package = new MemoryStream(new byte[] { 1, 2, 3 }, writable: false);
        switch (operation)
        {
            case "create":
                await provider.GetRequiredService<IAmsAgentCreationTransport>().CreateAsync(
                    new AmsAgentCreationTransportRequest
                    {
                        Endpoint = creation.Endpoint,
                        RouteMode = creation.RouteMode,
                        Audience = reference.Audience,
                        TenantId = Tenant,
                        EnvironmentId = Home,
                        IdempotencyKey = "native-dev-routing-proof",
                        DisplayName = "routing-proof",
                        AgentType = 0,
                        Language = 1,
                        LanguageVersion = "10",
                        Origin = "AgentHostCLI",
                    }, default);
                break;
            case "read-agent":
                await provider.GetRequiredService<IAmsAgentReadTransport>().GetAsync(
                    new AmsAgentReadTransportRequest
                    {
                        Endpoint = EnvironmentEndpoint,
                        Audience = reference.Audience,
                        TenantId = Tenant,
                        EnvironmentId = Home,
                        AgentId = Agent,
                    }, default);
                break;
            case "upload":
                await provider.GetRequiredService<IAmsPackagePublicationTransport>().PublishAsync(
                    new AmsPackagePublicationTransportRequest
                    {
                        Endpoint = EnvironmentEndpoint,
                        Audience = reference.Audience,
                        TenantId = Tenant,
                        EnvironmentId = Home,
                        AgentId = Agent,
                        PackageStream = package,
                        PackageLength = package.Length,
                    }, default);
                break;
            case "deploy":
                await provider.GetRequiredService<IAmsDeploymentTransport>().CreateAsync(
                    new AmsDeploymentCreateTransportRequest
                    {
                        Endpoint = reference.Endpoint,
                        Audience = reference.Audience,
                        TenantId = Tenant,
                        HomeEnvironmentId = Home,
                        TargetEnvironmentId = Target,
                        AgentId = Agent,
                        PackageRevisionId = Revision,
                        DeploymentName = "routing-proof",
                        DeploymentType = "Dev",
                        EntraId = Entra,
                        EntraIdType = "AppReg",
                        Language = "DotNet",
                        LanguageVersion = "10",
                    }, default);
                break;
            case "read-deployment":
                await provider.GetRequiredService<IAmsDeploymentTransport>().GetAsync(reference, default);
                break;
            case "update":
                await provider.GetRequiredService<IAmsDeploymentTransport>().UpdateAsync(
                    new AmsDeploymentUpdateTransportRequest
                    {
                        Endpoint = reference.Endpoint,
                        Audience = reference.Audience,
                        TenantId = Tenant,
                        HomeEnvironmentId = Home,
                        AgentId = Agent,
                        DeploymentId = Deployment,
                        PackageRevisionId = Revision,
                        ExpectedPackageRevisionId = Target,
                    }, default);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class ClaimsTokenProvider(TokenClaimFailure failure) : ITokenProvider
    {
        public List<string> Resources { get; } = [];
        public List<string?> Tenants { get; } = [];

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Resources.Add(resource);
            Tenants.Add(targetTenantId);
            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = FakeJwt.Create(
                    audience: failure == TokenClaimFailure.WrongAudience ? DataPlane : resource,
                    applicationId: failure == TokenClaimFailure.WrongApplication
                        ? Entra : PowerPlatformApiPermissions.AgentHostCliApplicationId,
                    tenantId: failure == TokenClaimFailure.WrongTenant ? Target : targetTenantId,
                    scopes: failure == TokenClaimFailure.MissingPermission ? null : AllScopes),
                TenantId = targetTenantId,
                ExpiresOn = DateTimeOffset.UnixEpoch.AddYears(60),
            });
        }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            AcquireTokenSilentAsync(string.Join(' ', scopes), targetTenantId, cancellationToken);
    }
}
