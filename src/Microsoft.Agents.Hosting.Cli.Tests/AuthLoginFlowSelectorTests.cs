using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>Unit tests for the pure login-flow selection, decided only by the requested options and the platform.</summary>
public sealed class AuthLoginFlowSelectorTests
{
    [Theory]
    [InlineData("windows", AuthLoginFlow.Broker)]
    [InlineData("macos", AuthLoginFlow.Broker)]
    [InlineData("linux", AuthLoginFlow.Broker)]
    [InlineData("unknown", AuthLoginFlow.DeviceCode)]
    public void DefaultsToTheBroker_OnEveryPlatformMsalHasOneFor(string platform, AuthLoginFlow expected) =>
        Assert.Equal(expected, AuthLoginFlowSelector.Select(new AuthLoginRequest(), FakePlatformInfo.Named(platform)));

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("linux")]
    [InlineData("unknown")]
    public void TheDeviceCodeFlag_SelectsDeviceCode_OnEveryPlatform(string platform) =>
        Assert.Equal(AuthLoginFlow.DeviceCode, AuthLoginFlowSelector.Select(new AuthLoginRequest { DeviceCode = true }, FakePlatformInfo.Named(platform)));

    [Fact]
    public void ARequestAndAPlatformAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => AuthLoginFlowSelector.Select(null!, FakePlatformInfo.Linux()));
        Assert.Throws<ArgumentNullException>(() => AuthLoginFlowSelector.Select(new AuthLoginRequest(), null!));
    }
}
