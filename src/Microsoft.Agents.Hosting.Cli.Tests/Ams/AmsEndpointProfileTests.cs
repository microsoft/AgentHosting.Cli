using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsEndpointProfileTests
{
    private const string DataPlaneHost = "https://parp-agentmanagementservicedataplane-nguhoa.wus-il100.gateway.test.island.powerapps.com";
    private const string ControlPlaneHost = "https://parp-agentmanagementservice-nguhoa.wus-cm100.gateway.test.cm.powerapps.com";

    private static readonly PowerPlatformEnvironment Home = EnvironmentFixtures.Environment("env-1");
    private static readonly Uri TrustedControlPlane = new("https://api.powerplatform.com/");

    [Fact]
    public void FromEnvironment_ReadsBothOverridesTrimmed()
    {
        var variables = new Dictionary<string, string?>
        {
            [AmsEndpointProfile.DataPlaneEndpointVariable] = $" {DataPlaneHost} ",
            [AmsEndpointProfile.ControlPlaneEndpointVariable] = ControlPlaneHost,
        };

        var profile = AmsEndpointProfile.FromEnvironment(name => variables.GetValueOrDefault(name));

        Assert.Equal(new AmsEndpointProfile { DataPlaneEndpoint = DataPlaneHost, ControlPlaneEndpoint = ControlPlaneHost }, profile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void FromEnvironment_TreatsUnsetOrBlankAsNoOverride(string? value)
    {
        Assert.Equal(new AmsEndpointProfile(), AmsEndpointProfile.FromEnvironment(_ => value));
    }

    [Fact]
    public void WithoutOverrides_EveryCallUsesThePowerPlatformApi()
    {
        var profile = new AmsEndpointProfile();

        Assert.Equal(Target(Home.ApiEndpoint, AmsRouteForm.EnvironmentHost), profile.ResolveDataPlane(Home));
        Assert.Equal(Target(TrustedControlPlane, AmsRouteForm.TenantHost), profile.ResolveControlPlane(Home, TrustedControlPlane));
    }

    [Fact]
    public void Overrides_UseTheDirectRouteFormWithTheEnvironmentAudience()
    {
        var profile = new AmsEndpointProfile { DataPlaneEndpoint = DataPlaneHost, ControlPlaneEndpoint = $" {ControlPlaneHost}/base " };

        var controlPlane = profile.ResolveControlPlane(Home, TrustedControlPlane);

        Assert.Equal(Target(new Uri(DataPlaneHost), AmsRouteForm.Direct), profile.ResolveDataPlane(Home));
        Assert.Equal(Target(new Uri($"{ControlPlaneHost}/base"), AmsRouteForm.Direct), controlPlane);
        Assert.Equal($"{ControlPlaneHost}/base/agenthosting/tenants/{Home.TenantId}/environments/env-1/agents/agent-1/deployments?api-version=1", AmsRoutes.Deployments(controlPlane, "agent-1").AbsoluteUri);
    }

    [Fact]
    public void EachPlane_ValidatesOnlyItsOwnOverride()
    {
        var profile = new AmsEndpointProfile { DataPlaneEndpoint = "http://plain.example" };

        Assert.Equal(Target(TrustedControlPlane, AmsRouteForm.TenantHost), profile.ResolveControlPlane(Home, TrustedControlPlane));
        Assert.Throws<AgentDeployException>(() => profile.ResolveDataPlane(Home));
    }

    [Theory]
    [InlineData("http://plain.example", "scheme 'http'")]
    [InlineData("https://user:secret@host.example", "user information")]
    [InlineData("relative/path", "absolute uri")]
    [InlineData("https://host.example/?x=1", "query or fragment")]
    [InlineData("https://host.example/#x", "query or fragment")]
    public void UnusableDataPlaneOverride_IsRejectedNamingItsVariableNotItsValue(string endpoint, string reason)
    {
        var exception = Assert.Throws<AgentDeployException>(() => new AmsEndpointProfile { DataPlaneEndpoint = endpoint }.ResolveDataPlane(Home));

        Assert.Equal(ErrorCodes.Deployment.AgentDeployEndpointNotUsable, exception.ErrorCode);
        Assert.Equal("endpoint-untrusted", exception.ErrorKind);
        Assert.StartsWith(AmsEndpointProfile.DataPlaneEndpointVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusableControlPlaneOverride_IsRejectedNamingItsVariable()
    {
        var exception = Assert.Throws<AgentDeployException>(() => new AmsEndpointProfile { ControlPlaneEndpoint = "https://host.example/?x=1" }.ResolveControlPlane(Home, TrustedControlPlane));

        Assert.Equal(ErrorCodes.Deployment.AgentDeployEndpointNotUsable, exception.ErrorCode);
        Assert.StartsWith(AmsEndpointProfile.ControlPlaneEndpointVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedControlPlaneEndpoint_IsTheRingGlobalEndpoint()
    {
        Assert.Equal(PowerPlatformApiTargetConfig.Public.GlobalEndpoint, AmsEndpointProfile.TrustedControlPlaneEndpoint(PowerPlatformApiTargetConfig.Public));
    }

    [Fact]
    public void Targets_ResolveBothPlanesThroughTheProfileAndTheRing()
    {
        var targets = new AmsTargets(new AmsEndpointProfile { DataPlaneEndpoint = DataPlaneHost }, PowerPlatformApiTargetConfig.Public);

        Assert.Equal(Target(new Uri(DataPlaneHost), AmsRouteForm.Direct), targets.DataPlane(Home));
        Assert.Equal(Target(PowerPlatformApiTargetConfig.Public.GlobalEndpoint, AmsRouteForm.TenantHost), targets.ControlPlane(Home));
    }

    [Fact]
    public void Targets_RefuseAnUntrustedRingWhenConstructed()
    {
        Assert.Throws<EndpointTrustException>(() => new AmsTargets(new AmsEndpointProfile(), PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri("https://attacker.example/") }));
    }

    [Fact]
    public void Targets_UseTheCloudOfTheSignInThatResolvedTheEnvironment()
    {
        var context = new EnvironmentResolutionContext(EnvironmentFixtures.Account(), PowerPlatformApiTargetConfig.Test);
        var home = Home with { Context = context };
        var targets = new AmsTargets(new AmsEndpointProfile(), PowerPlatformApiTargetConfig.Public);

        var controlPlane = targets.ControlPlane(home);

        Assert.Equal(Target(PowerPlatformApiTargetConfig.Test.GlobalEndpoint, AmsRouteForm.TenantHost) with { Context = context }, controlPlane);
        Assert.Equal(Target(Home.ApiEndpoint, AmsRouteForm.EnvironmentHost) with { Context = context }, targets.DataPlane(home));
    }

    [Fact]
    public void Targets_KeepTheSignInContextOnAnOverriddenControlPlane()
    {
        var context = new EnvironmentResolutionContext(EnvironmentFixtures.Account(), PowerPlatformApiTargetConfig.Test);
        var targets = new AmsTargets(new AmsEndpointProfile { ControlPlaneEndpoint = ControlPlaneHost }, PowerPlatformApiTargetConfig.Public);

        Assert.Equal(Target(new Uri(ControlPlaneHost), AmsRouteForm.Direct) with { Context = context }, targets.ControlPlane(Home with { Context = context }));
    }

    [Fact]
    public void Targets_RefuseAnUntrustedSignInCloud()
    {
        var context = new EnvironmentResolutionContext(EnvironmentFixtures.Account(), PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri("https://attacker.example/") });
        var targets = new AmsTargets(new AmsEndpointProfile(), PowerPlatformApiTargetConfig.Public);

        Assert.Throws<EndpointTrustException>(() => targets.ControlPlane(Home with { Context = context }));
    }

    private static AmsServiceTarget Target(Uri endpoint, AmsRouteForm routeForm) =>
        new() { Endpoint = endpoint, Audience = Home.ApiAudience, RouteForm = routeForm, TenantId = Home.TenantId, EnvironmentId = Home.EnvironmentId };
}
