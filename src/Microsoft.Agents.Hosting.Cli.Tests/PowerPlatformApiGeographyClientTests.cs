using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PowerPlatformApiDiscoveryTests
{
    [Theory]
    [InlineData(CloudInstance.UsGov, "api.gov.powerplatform.microsoft.us")]
    [InlineData(CloudInstance.UsGovHigh, "api.high.powerplatform.microsoft.us")]
    [InlineData(CloudInstance.UsGovDod, "api.appsplatform.us")]
    [InlineData(CloudInstance.UsNat, "api.powerplatform.eaglex.ic.gov")]
    [InlineData(CloudInstance.UsSec, "api.powerplatform.microsoft.scloud")]
    public void SovereignProductionRoutesUseOneCharacterShard(CloudInstance cloud, string host)
    {
        var target = PowerPlatformApiTargetConfig.Public with
        {
            Cloud = cloud, GlobalEndpoint = new Uri("https://" + host),
        };
        Assert.Equal($"abcdef0123456789abcdef01234567a.b.environment.{host}",
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, "ABCDEF01-2345-6789-ABCD-EF01234567ab").Host);
        Assert.Equal($"defaultabcdef0123456789abcdef01234567a.b.environment.{host}",
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, "Default-ABCDEF01-2345-6789-ABCD-EF01234567ab").Host);
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod)]
    [InlineData(PowerPlatformApiRing.Test)]
    public void NonProductionDefaultEnvironmentUsesOneCharacterShard(PowerPlatformApiRing ring)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        Assert.Equal($"defaultabcdef0123456789abcdef01234567a.b.environment.{target.GlobalEndpoint.Host}",
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target,
                "/providers/Microsoft.PowerApps/environments/Default-ABCDEF01-2345-6789-ABCD-EF01234567ab").Host);
    }

    [Fact]
    public void ChinaGlobalEndpointDoesNotImplyAVerifiedEnvironmentRoute()
    {
        var target = PowerPlatformApiTargetConfig.Public with
        {
            Cloud = CloudInstance.China,
            GlobalEndpoint = new Uri("https://api.powerplatform.partner.microsoftonline.cn"),
        };
        Assert.True(PowerPlatformCloudMap.ForTarget(target).IsTrusted(target.GlobalEndpoint));
        var error = Assert.Throws<NotSupportedException>(() =>
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, "11111111-2222-3333-4444-555555555555"));
        Assert.Contains("China", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrustedCoordinatesCannotWidenDiscoveryTrust()
    {
        var target = PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri("https://attacker.example") };
        Assert.Throws<EndpointTrustException>(() => PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, "env-1"));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, "e-n-v-1", "en.v1")]
    [InlineData(PowerPlatformApiRing.Production, "a-b-c", "a.bc")]
    [InlineData(PowerPlatformApiRing.Preprod, "A-B", "a.b")]
    [InlineData(PowerPlatformApiRing.Test, "A-B", "a.b")]
    public void ExistingNonGuidIdentifiersKeepTheirRoutingShape(PowerPlatformApiRing ring, string id, string split)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        Assert.Equal($"{split}.environment.{target.GlobalEndpoint.Host}",
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, id).Host);
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, 2)]
    [InlineData(PowerPlatformApiRing.Test, 1)]
    public void EnvironmentIdentifierCannotCreateAnOversizedDnsLabel(PowerPlatformApiRing ring, int suffixLength)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        var valid = new string('a', 63 + suffixLength);
        Assert.True(PowerPlatformCloudMap.ForTarget(target).IsTrusted(
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, valid)));
        Assert.Throws<ArgumentException>(() => PowerPlatformApiDiscovery.GetEnvironmentEndpoint(target, valid + "a"));
    }

    [Theory]
    [InlineData(
        "11111111-2222-3333-4444-555555555555",
        "111111112222333344445555555555.55.environment.api.powerplatform.com")]
    [InlineData(
        "Default-11111111-2222-3333-4444-555555555555",
        "default111111112222333344445555555555.55.environment.api.powerplatform.com")]
    public void ProductionEnvironmentEndpointMatchesPlatformDiscovery(
        string environmentId,
        string expectedHost)
    {
        Uri endpoint = PowerPlatformApiDiscovery.GetEnvironmentEndpoint(
            PowerPlatformApiTargetConfig.Public,
            environmentId);

        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(Uri.UriSchemeHttps, endpoint.Scheme);
        Assert.True(endpoint.IsDefaultPort);
        Assert.True(
            PowerPlatformCloudMap.ForTarget(PowerPlatformApiTargetConfig.Public)
                .IsTrusted(endpoint));
    }

    [Fact]
    public void TestEnvironmentEndpointUsesConfiguredRingAndSuffixLength()
    {
        Uri endpoint = PowerPlatformApiDiscovery.GetEnvironmentEndpoint(
            PowerPlatformApiTargetConfig.Test,
            "11111111-2222-3333-4444-555555555555");

        Assert.Equal(
            "1111111122223333444455555555555.5.environment.api.test.powerplatform.com",
            endpoint.Host);
        Assert.True(
            PowerPlatformCloudMap.ForTarget(PowerPlatformApiTargetConfig.Test)
                .IsTrusted(endpoint));
        Assert.False(
            PowerPlatformCloudMap.ForTarget(PowerPlatformApiTargetConfig.Public)
                .IsTrusted(endpoint));
    }

    [Theory]
    [InlineData("")]
    [InlineData("environment.example.com")]
    [InlineData("environment/one")]
    [InlineData("a")]
    [InlineData(null)]
    [InlineData("--")]
    [InlineData("ab")]
    [InlineData(" env-1")]
    [InlineData("env_1")]
    [InlineData("env-1?query")]
    public void EnvironmentEndpointRejectsIdentifiersThatCannotFormATrustedHost(
        string? environmentId) =>
        Assert.Throws<ArgumentException>(() =>
            PowerPlatformApiDiscovery.GetEnvironmentEndpoint(
                PowerPlatformApiTargetConfig.Public,
                environmentId!));
}

public sealed class PowerPlatformApiEnvironmentGeographyClientTests
{
    private const string ClusterResponse = """
        {
          "clusterNumber": "2",
          "geoName": "unitedstates",
          "environment": "Default-11111111-2222-3333-4444-555555555555",
          "clusterType": "Production",
          "clusterCategory": "prod",
          "clusterName": "prod-us-2",
          "geoLongName": "unitedstates"
        }
        """;

    [Fact]
    public async Task ReadsAuthoritativeGeoLongNameFromDefaultEnvironmentHost()
    {
        var handler = new StubHttpMessageHandler(
            _ => Json(HttpStatusCode.OK, ClusterResponse));
        var token = SelectedToken();
        var client = Create(handler, token);
        var target = new ResolvedEnvironmentTarget(
            "Default-11111111-2222-3333-4444-555555555555",
            EnvironmentFixtures.ContosoTenant);

        string? geography =
            await client.GetGeographyAsync(target, CancellationToken.None);

        Assert.Equal("unitedstates", geography);
        Assert.Equal(
            [PowerPlatformApiTargetConfig.Public.Audience],
            token.RequestedResources);
        Assert.Equal(
            [EnvironmentFixtures.ContosoTenant],
            token.RequestedTenants);
        StubHttpMessageHandler.CapturedRequest request =
            Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "default111111112222333344445555555555.55.environment.api.powerplatform.com",
            request.Uri.Host);
        Assert.Equal("/gateway/cluster", request.Uri.AbsolutePath);
        Assert.Equal("?api-version=1", request.Uri.Query);
        Assert.StartsWith(
            "Bearer ",
            request.Authorization,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSelectedAccountDoesNotCallTheService()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("HTTP must not run."));
        var token = new StubSelectedAccountTokenProvider();
        var client = Create(handler, token);

        string? geography = await client.GetGeographyAsync(
            new ResolvedEnvironmentTarget(
                "11111111-2222-3333-4444-555555555555",
                EnvironmentFixtures.ContosoTenant),
            CancellationToken.None);

        Assert.Null(geography);
        Assert.Single(token.RequestedResources);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WrongTenantTokenIsRejectedBeforeCallingTheService()
    {
        var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("HTTP must not run."));
        var token = new StubSelectedAccountTokenProvider
        {
            Result = new TokenAcquisitionResult
            {
                AccessToken = FakeJwt.Create(
                    tenantId: EnvironmentFixtures.FabrikamTenant),
                TenantId = EnvironmentFixtures.FabrikamTenant,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            },
        };
        var client = Create(handler, token);

        await Assert.ThrowsAsync<TokenClaimException>(() =>
            client.GetGeographyAsync(
                new ResolvedEnvironmentTarget(
                    "11111111-2222-3333-4444-555555555555",
                    EnvironmentFixtures.ContosoTenant),
                CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"geoLongName":null}""")]
    [InlineData("""{"geoLongName":""}""")]
    [InlineData("""{"geoName":"us"}""")]
    [InlineData("not-json")]
    public async Task MalformedOrMissingGeographyIsNotFabricated(string body)
    {
        var handler = new StubHttpMessageHandler(
            _ => Json(HttpStatusCode.OK, body));
        var client = Create(handler, SelectedToken());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetGeographyAsync(
                new ResolvedEnvironmentTarget(
                    "11111111-2222-3333-4444-555555555555",
                    EnvironmentFixtures.ContosoTenant),
                CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessResponseIsADataFreeFailure(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(
            _ => Json(status, """{"secret":"must-not-surface"}"""));
        var client = Create(handler, SelectedToken());

        HttpRequestException failure =
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetGeographyAsync(
                    new ResolvedEnvironmentTarget(
                        "11111111-2222-3333-4444-555555555555",
                        EnvironmentFixtures.ContosoTenant),
                    CancellationToken.None));

        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain(
            "secret",
            failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    private static PowerPlatformApiEnvironmentGeographyClient Create(
        StubHttpMessageHandler handler,
        StubSelectedAccountTokenProvider tokenProvider) =>
        new(
            new HttpClient(handler),
            tokenProvider,
            PowerPlatformApiTargetConfig.Public,
            new PpapiTokenClaimsValidator());

    private static StubSelectedAccountTokenProvider SelectedToken() => new()
    {
        Result = new TokenAcquisitionResult
        {
            AccessToken = FakeJwt.Create(),
            TenantId = EnvironmentFixtures.ContosoTenant,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        },
    };

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string body) =>
        new(status)
        {
            Content = new StringContent(
                body,
                System.Text.Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubSelectedAccountTokenProvider :
        ISelectedAccountTokenProvider
    {
        internal TokenAcquisitionResult? Result { get; init; }

        internal List<string> RequestedResources { get; } = [];

        internal List<string> RequestedTenants { get; } = [];

        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(
            string resource,
            string targetTenantId,
            CancellationToken cancellationToken)
        {
            RequestedResources.Add(resource);
            RequestedTenants.Add(targetTenantId);
            return Task.FromResult(Result);
        }
    }
}
