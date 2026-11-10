using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsEndpointProfileTests
{
    private const string DirectHost = "https://parp-agentmanagementservicedataplane-alias.wus-il100.gateway.test.island.powerapps.com";
    private const string GatewayHost = "https://ppapigw.us-il100.gateway.test.island.powerapps.com";
    private const string ControlPlaneHost = "https://parp-agentmanagementservice-alias.wus-cm100.gateway.test.cm.powerapps.com";

    private static readonly PowerPlatformEnvironment Home = EnvironmentFixtures.Environment("env-1");

    [Fact]
    public void FromEnvironment_ReadsEveryOverridePair_AndLeavesUnsetFamiliesOnTheFrontDoor()
    {
        var variables = new Dictionary<string, string?>
        {
            [AmsEndpointProfile.DataPlaneEndpointVariable] = DirectHost,
            [AmsEndpointProfile.DataPlaneAudienceVariable] = "https://api.test.powerplatform.com",
            [AmsEndpointProfile.OnBehalfOfEndpointVariable] = GatewayHost,
            [AmsEndpointProfile.OnBehalfOfAudienceVariable] = "https://api.dev.powerplatform.com",
            [AmsEndpointProfile.ControlPlaneEndpointVariable] = ControlPlaneHost,
        };

        var profile = AmsEndpointProfile.FromEnvironment(name => variables.GetValueOrDefault(name));

        Assert.Equal(new AmsEndpointOverride(AmsEndpointProfile.DataPlaneEndpointVariable, DirectHost, "https://api.test.powerplatform.com"), profile.DataPlane);
        Assert.Equal(new AmsEndpointOverride(AmsEndpointProfile.OnBehalfOfEndpointVariable, GatewayHost, "https://api.dev.powerplatform.com"), profile.OnBehalfOf);
        Assert.Equal(new AmsEndpointOverride(AmsEndpointProfile.ControlPlaneEndpointVariable, ControlPlaneHost, null), profile.ControlPlane);
    }

    [Theory]
    [InlineData(" pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com ", "pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void FromEnvironment_ReadsTheGitHostOfAPersonalDevCluster_Trimmed(string? value, string? gitHost)
    {
        Assert.Equal(gitHost, AmsEndpointProfile.FromEnvironment(name => name == AmsEndpointProfile.GitHostVariable ? value : null).GitHost);
    }

    [Fact]
    public void WithoutOverrides_EveryFamilyUsesThePowerPlatformApi()
    {
        var profile = AmsEndpointProfile.FromEnvironment(_ => null);
        var global = new Uri("https://api.powerplatform.com/");

        var dataPlane = profile.ResolveDataPlane(Home);
        var onBehalfOf = profile.ResolveOnBehalfOf(Home, "msagent agent deploy");
        var controlPlane = profile.ResolveControlPlane(Home, global);

        Assert.Equal((Home.ApiEndpoint, AmsRouteForm.EnvironmentHost, Home.ApiAudience), (dataPlane.Endpoint, dataPlane.RouteForm, dataPlane.Audience));
        Assert.Equal((Home.ApiEndpoint, AmsRouteForm.EnvironmentHost), (onBehalfOf.Endpoint, onBehalfOf.RouteForm));
        Assert.Equal((global, AmsRouteForm.TenantHost, Home.ApiAudience), (controlPlane.Endpoint, controlPlane.RouteForm, controlPlane.Audience));
        Assert.Equal((Home.TenantId, Home.EnvironmentId), (controlPlane.TenantId, controlPlane.EnvironmentId));
    }

    [Fact]
    public void Overrides_UseTheFullRouteFormAndTheirOwnAudiences()
    {
        var profile = new AmsEndpointProfile
        {
            DataPlane = new(AmsEndpointProfile.DataPlaneEndpointVariable, DirectHost, "https://api.test.powerplatform.com"),
            OnBehalfOf = new(AmsEndpointProfile.OnBehalfOfEndpointVariable, $" {GatewayHost} ", "https://api.dev.powerplatform.com"),
            ControlPlane = new(AmsEndpointProfile.ControlPlaneEndpointVariable, ControlPlaneHost, null),
        };

        var dataPlane = profile.ResolveDataPlane(Home);
        var onBehalfOf = profile.ResolveOnBehalfOf(Home, "msagent agent deploy");
        var controlPlane = profile.ResolveControlPlane(Home, new Uri("https://api.powerplatform.com/"));

        Assert.Equal((new Uri(DirectHost), AmsRouteForm.Direct, "https://api.test.powerplatform.com"), (dataPlane.Endpoint, dataPlane.RouteForm, dataPlane.Audience));
        Assert.Equal((new Uri(GatewayHost), AmsRouteForm.Direct, "https://api.dev.powerplatform.com"), (onBehalfOf.Endpoint, onBehalfOf.RouteForm, onBehalfOf.Audience));
        Assert.Equal((new Uri(ControlPlaneHost), AmsRouteForm.Direct, Home.ApiAudience), (controlPlane.Endpoint, controlPlane.RouteForm, controlPlane.Audience));
        Assert.Equal($"{DirectHost}/agenthosting/tenants/{Home.TenantId}/environments/env-1/agents/mcs?api-version=1", AmsRoutes.McsRegister(dataPlane).AbsoluteUri);
    }

    [Fact]
    public void OnBehalfOfOperation_NeverReachesThePlainDataPlaneOverride()
    {
        var profile = new AmsEndpointProfile { DataPlane = new(AmsEndpointProfile.DataPlaneEndpointVariable, DirectHost, null) };

        var exception = Assert.Throws<AmsAgentException>(() => profile.ResolveOnBehalfOf(Home, "msagent agent deploy"));

        Assert.Equal(ErrorCodes.AmsAgent.OnBehalfOfEndpointRequired, exception.ErrorCode);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Contains("msagent agent deploy", exception.Message, StringComparison.Ordinal);
        Assert.Contains(AmsEndpointProfile.DataPlaneEndpointVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(AmsEndpointProfile.OnBehalfOfEndpointVariable, exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://plain.example", "scheme 'http'")]
    [InlineData("https://user:secret@host.example", "user information")]
    [InlineData("relative/path", "absolute uri")]
    [InlineData("https://host.example/?x=1", "query or fragment")]
    [InlineData(null, "is not set")]
    public void UnusableOverride_IsRejectedNamingItsVariable(string? endpoint, string reason)
    {
        var profile = new AmsEndpointProfile { DataPlane = new(AmsEndpointProfile.DataPlaneEndpointVariable, endpoint, "https://api.test.powerplatform.com") };

        var exception = Assert.Throws<AmsAgentException>(() => profile.ResolveDataPlane(Home));

        Assert.Equal(ErrorCodes.AmsAgent.EndpointOverrideNotUsable, exception.ErrorCode);
        Assert.Equal("endpoint-untrusted", exception.ErrorKind);
        Assert.StartsWith(AmsEndpointProfile.DataPlaneEndpointVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/path", "Unparsed is not an absolute uri.|Supply an absolute https endpoint, for example https://<host>.")]
    [InlineData("http://plain.example", "Parsed uses scheme 'http', and a delegated token is only ever sent over https.|Supply an https endpoint.")]
    [InlineData("https://name@host.example", "Parsed carries embedded user information.|Supply an https endpoint with no embedded credentials.")]
    public void RequireHttps_RefusesWithTheCallersFailure_NamingTheSubjectThatFits(string value, string failure)
    {
        Assert.Equal(failure, Assert.Throws<UsageException>(() => AmsEndpointValidation.RequireHttps(value, "Unparsed", "Parsed", (detail, remediation) => new UsageException($"{detail}|{remediation}"))).Message);
    }

    [Fact]
    public void RequireHttps_ReturnsTheTrimmedHttpsEndpoint()
    {
        Assert.Equal(new Uri("https://host.example/base"), AmsEndpointValidation.RequireHttps(" https://host.example/base ", "Unparsed", "Parsed", (detail, _) => new UsageException(detail)));
    }

    [Fact]
    public void TrustedControlPlaneEndpoint_IsTheRingGlobalEndpoint()
    {
        Assert.Equal(PowerPlatformApiTargetConfig.Public.GlobalEndpoint, AmsEndpointProfile.TrustedControlPlaneEndpoint(PowerPlatformApiTargetConfig.Public));
    }
}
