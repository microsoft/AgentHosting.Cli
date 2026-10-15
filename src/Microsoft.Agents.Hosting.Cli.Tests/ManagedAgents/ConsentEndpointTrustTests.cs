using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConsentEndpointTrustTests
{
    [Theory]
    [InlineData("https://global.consent.azure-apim.net/redirect")]
    [InlineData("https://europe-001.azure-apim.net/apim/office365/conn-1/consent")]
    [InlineData("https://global.consent.azure-apihub.net/redirect")]
    [InlineData("https://power-apis-usgov001-public.azure-apihub.us/consent")]
    [InlineData("https://china.azure-apihub.cn/consent")]
    public void TrustedConsentHosts_MayReceiveAToken(string endpoint) =>
        Assert.True(ConsentEndpointTrust.IsTrusted(new Uri(endpoint)));

    [Theory]
    [InlineData("https://attacker.example.com/consent")]
    [InlineData("https://azure-apim.net.attacker.example.com/consent")]
    [InlineData("https://contoso.com/azure-apim.net")]
    public void UnknownHosts_MayNotReceiveAToken(string endpoint) =>
        Assert.False(ConsentEndpointTrust.IsTrusted(new Uri(endpoint)));

    [Fact]
    public void PlainHttp_IsNeverTrusted() =>
        Assert.False(ConsentEndpointTrust.IsTrusted(new Uri("http://global.consent.azure-apim.net/redirect")));

    [Fact]
    public void Require_ReturnsATrustedEndpoint()
    {
        var endpoint = new Uri("https://global.consent.azure-apim.net/redirect");

        Assert.Equal(endpoint, ConsentEndpointTrust.Require(endpoint));
    }

    [Fact]
    public void Require_RefusesAnUntrustedEndpointAndNamesIt()
    {
        var failure = Assert.Throws<SilentConnectionUnavailableException>(
            () => ConsentEndpointTrust.Require(new Uri("https://attacker.example.com/consent")));

        Assert.Contains("attacker.example.com", failure.Message, StringComparison.Ordinal);
    }
}
