using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Unit tests for PAC's retained corporate-account protection. Pure enforcement with fake accounts —
/// no token acquisition.
/// </summary>
public sealed class MicrosoftCorporateAccountPolicyTests
{
    private const string MsitTenant = MicrosoftCorporateAccountPolicy.MicrosoftTenantId;

    private static AuthAccount Account(string username, string? tenantId) => new()
    {
        Username = username,
        HomeAccountId = "home-id",
        TenantId = tenantId,
    };

    [Fact]
    public void CorporateAccount_IsMsitTenantPlusMicrosoftDomain()
    {
        Assert.True(MicrosoftCorporateAccountPolicy.IsMicrosoftCorporateAccount(Account("alice@microsoft.com", MsitTenant)));
    }

    [Theory]
    [InlineData("alice@microsoft.com", "11111111-1111-1111-1111-111111111111")] // right domain, wrong tenant
    [InlineData("alice@contoso.com", MsitTenant)]                               // right tenant, wrong domain
    [InlineData("alice@contoso.com", "11111111-1111-1111-1111-111111111111")]   // neither
    public void NonCorporateAccount_IsNotFlagged(string username, string tenantId)
    {
        Assert.False(MicrosoftCorporateAccountPolicy.IsMicrosoftCorporateAccount(Account(username, tenantId)));
    }

    [Fact]
    public void BrowserInteractive_WithCorporateAccount_IsBlocked()
    {
        var ex = Assert.Throws<CorporateAccountRequiresBrokerException>(() =>
            MicrosoftCorporateAccountPolicy.EnforceAfterLogin(
                AuthLoginFlow.BrowserInteractive,
                Account("alice@microsoft.com", MsitTenant)));

        Assert.Equal(ExitCodes.Failure, ex.ExitCode);
        Assert.NotNull(ex.Remediation);
    }

    [Fact]
    public void BrowserInteractive_WithNonCorporateAccount_IsAllowed()
    {
        // Does not throw.
        MicrosoftCorporateAccountPolicy.EnforceAfterLogin(
            AuthLoginFlow.BrowserInteractive,
            Account("alice@contoso.com", MsitTenant));
    }

    [Theory]
    [InlineData(AuthLoginFlow.Broker)]
    [InlineData(AuthLoginFlow.DeviceCode)]
    public void BrokerAndDeviceCode_AreExempt_EvenForCorporateAccount(AuthLoginFlow flow)
    {
        // The broker is the sanctioned path for Microsoft employees; neither broker nor device-code is blocked.
        MicrosoftCorporateAccountPolicy.EnforceAfterLogin(flow, Account("alice@microsoft.com", MsitTenant));
    }
}
