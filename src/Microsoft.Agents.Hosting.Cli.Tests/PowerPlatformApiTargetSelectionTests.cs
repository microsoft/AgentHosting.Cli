using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PowerPlatformApiTargetSelectionTests
{
    [Theory]
    [InlineData("Public", CloudInstance.Public, PowerPlatformApiRing.Production, "api.powerplatform.com", "login.microsoftonline.com")]
    [InlineData("Preprod", CloudInstance.Public, PowerPlatformApiRing.Preprod, "api.preprod.powerplatform.com", "login.microsoftonline.com")]
    [InlineData("Test", CloudInstance.Public, PowerPlatformApiRing.Test, "api.test.powerplatform.com", "login.microsoftonline.com")]
    [InlineData("UsGov", CloudInstance.UsGov, PowerPlatformApiRing.Production, "api.gov.powerplatform.microsoft.us", "login.microsoftonline.com")]
    [InlineData("UsGovHigh", CloudInstance.UsGovHigh, PowerPlatformApiRing.Production, "api.high.powerplatform.microsoft.us", "login.microsoftonline.us")]
    [InlineData("UsGovDod", CloudInstance.UsGovDod, PowerPlatformApiRing.Production, "api.appsplatform.us", "login.microsoftonline.us")]
    [InlineData("China", CloudInstance.China, PowerPlatformApiRing.Production, "api.powerplatform.partner.microsoftonline.cn", "login.partner.microsoftonline.cn")]
    [InlineData("UsNat", CloudInstance.UsNat, PowerPlatformApiRing.Production, "api.powerplatform.eaglex.ic.gov", "login.microsoftonline.eaglex.ic.gov")]
    [InlineData("UsSec", CloudInstance.UsSec, PowerPlatformApiRing.Production, "api.powerplatform.microsoft.scloud", "login.microsoftonline.microsoft.scloud")]
    public void OperatorTargetResolvesCoherentCoordinatesAndScopes(
        string token, CloudInstance cloud, PowerPlatformApiRing ring, string apiHost, string loginHost)
    {
        Assert.True(PowerPlatformApiTargetSelection.TryResolve(" " + token.ToUpperInvariant() + " ", out var target, out var error));
        Assert.Null(error);
        Assert.Equal(cloud, target.Cloud);
        Assert.Equal(ring, target.Ring);
        Assert.Equal(new Uri("https://" + apiHost), target.GlobalEndpoint);
        Assert.Equal($"https://{apiHost}" + (ring == PowerPlatformApiRing.Production ? "/" : ""), target.Audience);
        Assert.Equal($"https://{apiHost}//.default", new PpapiScopeBuilder().DefaultScope(target.Audience));
        Assert.Equal(new Uri($"https://{loginHost}/organizations"), target.Authority.AuthorityUrl);
        Assert.Equal(cloud, target.Authority.Cloud);
        Assert.Null(target.Authority.TenantId);
        Assert.Equal(token, PowerPlatformApiTargetSelection.CanonicalToken(cloud, ring));
        Assert.Same(target, PowerPlatformApiTargetSelection.TargetFor(cloud, ring));
        Assert.Same(target, PowerPlatformApiTargetSelection.Require(target with { }));
        var map = PowerPlatformCloudMap.ForTarget(target);
        Assert.True(map.IsTrusted(target.GlobalEndpoint));
        Assert.False(map.IsTrusted(new Uri($"https://not{apiHost}")));
        Assert.False(map.IsTrusted(new Uri($"https://{apiHost}.attacker.example")));
        Assert.False(map.IsTrusted(new Uri($"https://user@{apiHost}")));
        Assert.False(map.IsTrusted(new Uri($"https://{apiHost}:8443")));
        foreach (var other in PowerPlatformApiTargetSelection.AcceptedTokens)
        {
            Assert.True(PowerPlatformApiTargetSelection.TryResolve(other, out var otherTarget, out _));
            if (otherTarget != target) Assert.False(map.IsTrusted(otherTarget.GlobalEndpoint), $"{token} trusted {other}");
        }
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production)]
    [InlineData(PowerPlatformApiRing.Preprod)]
    [InlineData(PowerPlatformApiRing.Test)]
    public void ComputedAuthorityDoesNotChangeTargetCoordinateSerialization(PowerPlatformApiRing ring)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(target));
        Assert.False(json.RootElement.TryGetProperty(nameof(target.Authority), out _));
        Assert.Equal(5, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void CanonicalTargetsExcludeLegacyAliasesAndPreserveRingSingletons()
    {
        Assert.Equal(["Public", "Preprod", "Test", "UsGov", "UsGovHigh", "UsGovDod", "China", "UsNat", "UsSec"],
            PowerPlatformApiTargetSelection.AcceptedTokens);
        Assert.Same(PowerPlatformApiTargetConfig.Public, PowerPlatformApiTargetSelection.TargetFor(CloudInstance.Public));
        Assert.Same(PowerPlatformApiTargetConfig.Preprod, PowerPlatformApiTargetSelection.TargetFor(CloudInstance.Public, PowerPlatformApiRing.Preprod));
        Assert.Same(PowerPlatformApiTargetConfig.Test, PowerPlatformApiTargetSelection.TargetFor(CloudInstance.Public, PowerPlatformApiRing.Test));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("prod")]
    [InlineData(" PRODUCTION ")]
    public void LegacyDefaultAndProductionAliasesResolvePublic(string? token)
    {
        Assert.True(PowerPlatformApiTargetSelection.TryResolve(token, out var target, out var error));
        Assert.Same(PowerPlatformApiTargetConfig.Public, target);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ppe")]
    [InlineData("https://api.powerplatform.com")]
    [InlineData("UsNatPreprod")]
    [InlineData("99")]
    public void UnknownOperatorTargetIsNotAccepted(string token)
    {
        Assert.False(PowerPlatformApiTargetSelection.TryResolve(token, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("UsSec", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudInstance.UsGov, PowerPlatformApiRing.Preprod)]
    [InlineData(CloudInstance.UsGovHigh, PowerPlatformApiRing.Test)]
    [InlineData(CloudInstance.UsGovDod, PowerPlatformApiRing.Preprod)]
    [InlineData(CloudInstance.China, PowerPlatformApiRing.Test)]
    [InlineData(CloudInstance.UsNat, PowerPlatformApiRing.Preprod)]
    [InlineData(CloudInstance.UsSec, PowerPlatformApiRing.Test)]
    [InlineData((CloudInstance)99, PowerPlatformApiRing.Production)]
    [InlineData(CloudInstance.Public, (PowerPlatformApiRing)99)]
    public void UndefinedAndIncompatibleTargetsCannotResolve(CloudInstance cloud, PowerPlatformApiRing ring)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformApiTargetSelection.TargetFor(cloud, ring));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformApiTargetSelection.CanonicalToken(cloud, ring));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PowerPlatformCloudMap.ForTarget(PowerPlatformApiTargetConfig.Public with { Cloud = cloud, Ring = ring }));
    }

    [Fact]
    public void SuppliedCoordinatesCannotAlterARegisteredTarget()
    {
        Assert.Throws<ArgumentException>(() => PowerPlatformApiTargetSelection.Require(
            PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri("https://attacker.example") }));
        Assert.Throws<ArgumentException>(() => PowerPlatformApiTargetSelection.Require(
            PowerPlatformApiTargetConfig.Public with { Audience = "https://api.test.powerplatform.com" }));
    }

    [Theory]
    [InlineData("https://user:synthetic@api.powerplatform.com/")]
    [InlineData("https://api.powerplatform.com/#fragment")]
    [InlineData("https://api.powerplatform.com/#")]
    [InlineData("https://api.powerplatform.com/?query=value")]
    [InlineData("https://api.powerplatform.com:8443/")]
    [InlineData("http://api.powerplatform.com/")]
    [InlineData("relative", UriKind.Relative)]
    public void SuppliedEndpointCannotHideUnwantedUriComponents(string endpoint, UriKind kind = UriKind.Absolute)
    {
        Assert.Throws<ArgumentException>(() => PowerPlatformApiTargetSelection.Require(
            PowerPlatformApiTargetConfig.Public with { GlobalEndpoint = new Uri(endpoint, kind) }));
    }
}
