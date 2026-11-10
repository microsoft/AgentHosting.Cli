using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsCapabilityTests
{
    private static readonly Dictionary<string, Func<AmsMcsConfig, AmsMcsConfig>> Without = new()
    {
        ["amsMcs.dataPlane.endpoint"] = config => config with { DataPlane = config.DataPlane with { Endpoint = null } },
        ["amsMcs.dataPlane.audience"] = config => config with { DataPlane = config.DataPlane with { Audience = " " } },
        ["amsMcs.onBehalfOf.endpoint"] = config => config with { OnBehalfOf = config.OnBehalfOf with { Endpoint = null } },
        ["amsMcs.onBehalfOf.audience"] = config => config with { OnBehalfOf = config.OnBehalfOf with { Audience = null } },
        ["amsMcs.controlPlane.endpoint"] = config => config with { ControlPlane = config.ControlPlane with { Endpoint = null } },
        ["amsMcs.controlPlane.audience"] = config => config with { ControlPlane = config.ControlPlane with { Audience = null } },
        ["amsMcs.gitHost"] = config => config with { GitHost = null },
        ["amsMcs.devEnvironmentId"] = config => config with { DevEnvironmentId = null },
        ["amsMcs.testEnvironmentId"] = config => config with { TestEnvironmentId = "" },
        ["amsMcs.prodEnvironmentId"] = config => config with { ProdEnvironmentId = null },
        ["amsMcs.organizations.dev"] = config => config with { Organizations = config.Organizations with { Dev = null } },
        ["amsMcs.organizations.test"] = config => config with { Organizations = config.Organizations with { Test = null } },
        ["amsMcs.organizations.prod"] = config => config with { Organizations = config.Organizations with { Prod = null } },
    };

    public static TheoryData<string> ServiceKeys() => [.. Without.Keys.Where(key => !key.Contains("Environment", StringComparison.Ordinal) && !key.Contains("organizations", StringComparison.Ordinal))];

    public static TheoryData<string> RingKeys() => [.. Without.Keys.Where(key => key.Contains("Environment", StringComparison.Ordinal) || key.Contains("organizations", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(ServiceKeys))]
    public void EachMissingServiceValueIsASkipThatNamesItsKey(string key)
    {
        var capabilities = Resolve(Without[key](AmsMcsTestKit.Config()));

        Assert.False(capabilities.Has(Capability.AmsMcs));
        Assert.Equal($"no {key} supplied", capabilities.Reason(Capability.AmsMcs));
        Assert.True(capabilities.Has(Capability.AmsMcsRing));
    }

    [Theory]
    [MemberData(nameof(RingKeys))]
    public void EachMissingRingValueIsASkipThatNamesItsKey(string key)
    {
        var capabilities = Resolve(Without[key](AmsMcsTestKit.Config()));

        Assert.False(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal($"no {key} supplied", capabilities.Reason(Capability.AmsMcsRing));
        Assert.True(capabilities.Has(Capability.AmsMcs));
    }

    [Fact]
    public void AFlagWithoutAmsMcsAgentsMakesAmsMcsUnavailable()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config() with { Features = "EvaluationFeatures" });

        Assert.False(capabilities.Has(Capability.AmsMcs));
        Assert.Equal("amsMcs.features 'EvaluationFeatures' doesn't hold AmsMcsAgents", capabilities.Reason(Capability.AmsMcs));
    }

    [Fact]
    public void ACompleteBlockMakesAmsMcsAndTheRingAvailable()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config());

        Assert.True(capabilities.Has(Capability.AmsMcs));
        Assert.Equal("AmsMcsAgents, data plane https://data-plane.example.invalid", capabilities.Reason(Capability.AmsMcs));
        Assert.True(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal("dev aaaaaaaa…, test bbbbbbbb…, prod cccccccc…", capabilities.Reason(Capability.AmsMcsRing));
    }

    [Fact]
    public void AnIdThatIsNotAGuidMakesTheRingUnavailable()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config() with { TestEnvironmentId = "TestEnvironmentName" });

        Assert.False(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal("amsMcs.testEnvironmentId is not a GUID", capabilities.Reason(Capability.AmsMcsRing));
    }

    [Theory]
    [InlineData("http://dev.example.invalid")]
    [InlineData("dev.example.invalid")]
    public void AnOrganizationThatIsNotAnHttpsUrlMakesTheRingUnavailable(string organization)
    {
        var config = AmsMcsTestKit.Config();
        var capabilities = Resolve(config with { Organizations = config.Organizations with { Dev = organization } });

        Assert.False(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal("amsMcs.organizations.dev is not an https URL", capabilities.Reason(Capability.AmsMcsRing));
    }

    [Fact]
    public void AForbiddenEnvironmentMakesTheRingUnavailable()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config() with { ForbiddenEnvironmentIds = [AmsMcsTestKit.Elsewhere, $" {AmsMcsTestKit.Prod.ToUpperInvariant()} "] });

        Assert.False(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal($"amsMcs.prodEnvironmentId {AmsMcsTestKit.Prod} is in amsMcs.forbiddenEnvironmentIds", capabilities.Reason(Capability.AmsMcsRing));
    }

    [Fact]
    public void AForbiddenListNamingNoneOfTheThreeLeavesTheRingAvailable()
    {
        Assert.True(Resolve(AmsMcsTestKit.Config() with { ForbiddenEnvironmentIds = [AmsMcsTestKit.Elsewhere] }).Has(Capability.AmsMcsRing));
    }

    [Fact]
    public void TwoEqualIdsMakeTheRingUnavailable()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config() with { ProdEnvironmentId = $" {AmsMcsTestKit.Test.ToUpperInvariant()}" });

        Assert.False(capabilities.Has(Capability.AmsMcsRing));
        Assert.Equal("amsMcs.devEnvironmentId, amsMcs.testEnvironmentId and amsMcs.prodEnvironmentId must name three different environments", capabilities.Reason(Capability.AmsMcsRing));
    }

    [Fact]
    public void TheProbedCapabilitiesWaitForTheAmsPack()
    {
        var capabilities = Resolve(AmsMcsTestKit.Config());

        Assert.All(Capabilities.ProbedAmsMcsCapabilities, capability => Assert.Equal(Capabilities.AmsMcsNotProbed, capabilities.Reason(capability)));
        Assert.All(Capabilities.ProbedAmsMcsCapabilities, capability => Assert.False(capabilities.Has(capability)));
    }

    private static Capabilities Resolve(AmsMcsConfig config) => Capabilities.Resolve(new E2EConfig { AmsMcs = config }, Path.GetTempPath(), signedIn: true, "signed in", azureCliPresent: false);
}
