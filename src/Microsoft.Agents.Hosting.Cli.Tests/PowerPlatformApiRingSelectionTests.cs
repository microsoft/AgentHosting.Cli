using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PowerPlatformApiRingSelectionTests
{
    public static TheoryData<PowerPlatformApiRing> EveryRing
    {
        get
        {
            var data = new TheoryData<PowerPlatformApiRing>();

            foreach (var ring in Enum.GetValues<PowerPlatformApiRing>())
            {
                data.Add(ring);
            }

            return data;
        }
    }

    [Theory]
    [InlineData("prod", PowerPlatformApiRing.Production)]
    [InlineData("production", PowerPlatformApiRing.Production)]
    [InlineData("preprod", PowerPlatformApiRing.Preprod)]
    [InlineData("test", PowerPlatformApiRing.Test)]
    [InlineData("  TEST  ", PowerPlatformApiRing.Test)]
    [InlineData("PreProd", PowerPlatformApiRing.Preprod)]
    public void AnAcceptedTokenResolvesItsRing(string token, PowerPlatformApiRing expected)
    {
        Assert.True(PowerPlatformApiRingSelection.TryResolve(token, out var target, out var error));
        Assert.Equal(expected, target.Ring);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTokenResolvesProduction(string? token)
    {
        Assert.True(PowerPlatformApiRingSelection.TryResolve(token, out var target, out _));
        Assert.Equal(PowerPlatformApiRing.Production, target.Ring);
        Assert.Same(PowerPlatformApiTargetConfig.Public, target);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("ppe")]
    [InlineData("prod ring")]
    [InlineData("https://api.attacker.example")]
    public void AnUnknownTokenIsRefusedAndNamesTheAcceptedValues(string token)
    {
        Assert.False(PowerPlatformApiRingSelection.TryResolve(token, out var target, out var error));
        Assert.NotNull(error);
        Assert.Contains(PowerPlatformApiRingSelection.AcceptedTokenList, error, StringComparison.Ordinal);
        Assert.Same(PowerPlatformApiRingSelection.Default, target);
    }

    [Fact]
    public void TheAcceptedTokenListIsTheAcceptedTokens()
    {
        Assert.Equal(
            string.Join(", ", PowerPlatformApiRingSelection.AcceptedTokens),
            PowerPlatformApiRingSelection.AcceptedTokenList);
        Assert.Equal(["prod", "preprod", "test"], PowerPlatformApiRingSelection.AcceptedTokens);
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void EveryRingRoundTripsThroughItsCanonicalToken(PowerPlatformApiRing ring)
    {
        var token = PowerPlatformApiRingSelection.CanonicalToken(ring);

        Assert.Contains(token, PowerPlatformApiRingSelection.AcceptedTokens);
        Assert.True(PowerPlatformApiRingSelection.TryResolve(token, out var target, out _));
        Assert.Equal(ring, target.Ring);
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void EveryRingHasATargetWhoseRingMatches(PowerPlatformApiRing ring)
    {
        Assert.Equal(ring, PowerPlatformApiRingSelection.TargetFor(ring).Ring);
    }

    [Fact]
    public void EveryAcceptedTokenResolves()
    {
        foreach (var token in PowerPlatformApiRingSelection.AcceptedTokens)
        {
            Assert.True(PowerPlatformApiRingSelection.TryResolve(token, out _, out _), token);
        }
    }

    [Fact]
    public void ProductionKeepsUnscopedStateNames()
    {
        Assert.Equal(string.Empty, PowerPlatformApiRingSelection.StateSuffix(PowerPlatformApiRing.Production));
        Assert.Equal(
            "environment-selection-csharp.json",
            PowerPlatformApiRingSelection.RingScopedName(
                "environment-selection-csharp.json",
                PowerPlatformApiRing.Production));
        Assert.Equal(
            "connections",
            PowerPlatformApiRingSelection.RingScopedName("connections", PowerPlatformApiRing.Production));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod, "-preprod")]
    [InlineData(PowerPlatformApiRing.Test, "-test")]
    public void EachNonProductionRingScopesItsStateNames(PowerPlatformApiRing ring, string suffix)
    {
        Assert.Equal(suffix, PowerPlatformApiRingSelection.StateSuffix(ring));
        Assert.Equal(
            $"environment-selection-csharp{suffix}.json",
            PowerPlatformApiRingSelection.RingScopedName("environment-selection-csharp.json", ring));
        Assert.Equal(
            $"connections{suffix}",
            PowerPlatformApiRingSelection.RingScopedName("connections", ring));
    }

    [Theory]
    [InlineData("environment-selection-csharp.json")]
    [InlineData("connections")]
    public void NoTwoRingsShareAStateName(string name)
    {
        var names = Enum.GetValues<PowerPlatformApiRing>()
            .Select(ring => PowerPlatformApiRingSelection.RingScopedName(name, ring))
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyStateNameIsRefused(string name)
    {
        Assert.Throws<ArgumentException>(
            () => PowerPlatformApiRingSelection.RingScopedName(name, PowerPlatformApiRing.Test));
    }

    [Fact]
    public void AnUndefinedRingIsRefused()
    {
        var undefined = (PowerPlatformApiRing)(-1);

        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformApiRingSelection.CanonicalToken(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformApiRingSelection.StateSuffix(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformApiRingSelection.TargetFor(undefined));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PowerPlatformApiRingSelection.RingScopedName("connections", undefined));
    }

}

public sealed class PowerPlatformApiRingTargetTests
{
    public static TheoryData<PowerPlatformApiRing> EveryRing => PowerPlatformApiRingSelectionTests.EveryRing;

    private static PowerPlatformApiTargetConfig Target(PowerPlatformApiRing ring) =>
        PowerPlatformApiRingSelection.TargetFor(ring);

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, "https://api.powerplatform.com", "https://api.powerplatform.com/")]
    [InlineData(PowerPlatformApiRing.Preprod, "https://api.preprod.powerplatform.com", "https://api.preprod.powerplatform.com")]
    [InlineData(PowerPlatformApiRing.Test, "https://api.test.powerplatform.com", "https://api.test.powerplatform.com")]
    public void EveryRingPinsItsEndpointAndAudience(PowerPlatformApiRing ring, string endpoint, string audience)
    {
        var target = Target(ring);

        Assert.Equal(new Uri(endpoint), target.GlobalEndpoint);
        Assert.Equal(audience, target.Audience);
        Assert.Equal(CloudInstance.Public, target.Cloud);
        Assert.Equal("2022-03-01-preview", target.EnvironmentsApiVersion);
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void EveryRingTrustsItsOwnGlobalEndpoint(PowerPlatformApiRing ring)
    {
        var target = Target(ring);

        Assert.True(PowerPlatformCloudMap.ForTarget(target).IsTrusted(target.GlobalEndpoint));
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void NoRingTrustsAnotherRingsEndpoint(PowerPlatformApiRing ring)
    {
        var map = PowerPlatformCloudMap.ForTarget(Target(ring));

        foreach (var other in Enum.GetValues<PowerPlatformApiRing>().Where(candidate => candidate != ring))
        {
            Assert.False(map.IsTrusted(Target(other).GlobalEndpoint), $"{ring} trusted {other}.");
        }
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void EveryRingTrustsItsEnvironmentSubdomainsOnly(PowerPlatformApiRing ring)
    {
        var target = Target(ring);
        var map = PowerPlatformCloudMap.ForTarget(target);
        var host = target.GlobalEndpoint.Host;

        Assert.True(map.IsTrusted(new Uri($"https://abc.environment.{host}")));
        Assert.False(map.IsTrusted(new Uri($"https://not{host}")));
        Assert.False(map.IsTrusted(new Uri($"https://{host}.attacker.example")));
        Assert.False(map.IsTrusted(new Uri($"http://{host}")));
        Assert.False(map.IsTrusted(new Uri($"https://{host}:8443")));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Preprod)]
    [InlineData(PowerPlatformApiRing.Test)]
    public void ANonProductionRingIsRefusedOnASovereignCloud(PowerPlatformApiRing ring)
    {
        var target = PowerPlatformApiTargetConfig.Public with { Ring = ring, Cloud = CloudInstance.UsGov };

        Assert.Throws<ArgumentOutOfRangeException>(() => PowerPlatformCloudMap.ForTarget(target));
    }

    [Theory]
    [MemberData(nameof(EveryRing))]
    public void EveryDeclaredRingIsAccepted(PowerPlatformApiRing ring)
    {
        Assert.True(PowerPlatformApiRingSelection.IsDeclared(ring));
        Assert.Equal(ring, PowerPlatformApiRingSelection.Require(ring));
    }

    [Fact]
    public void AnUndeclaredRingIsRefusedRatherThanPersisted()
    {
        Assert.False(PowerPlatformApiRingSelection.IsDeclared((PowerPlatformApiRing)987));

        var failure = Assert.Throws<UsageException>(() => PowerPlatformApiRingSelection.Require((PowerPlatformApiRing)987));

        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
    }
}
