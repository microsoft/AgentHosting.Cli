using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AcquiredTokenTenantTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    private static TokenAcquisitionResult Acquisition(string? tenantId) =>
        new() { AccessToken = "token-1", TenantId = tenantId, ExpiresOn = DateTimeOffset.UtcNow.AddHours(1) };

    [Fact]
    public void AMatchingTenantReturnsTheAccessToken() =>
        Assert.Equal("token-1", AcquiredTokenTenant.Require(Acquisition(Tenant), Tenant));

    [Fact]
    public void ATenantDifferingOnlyByCaseOrSpacingIsAccepted() =>
        Assert.Equal("token-1", AcquiredTokenTenant.Require(Acquisition(Tenant.ToUpperInvariant()), $"  {Tenant}  "));

    [Fact]
    public void AForeignTenantIsRejected()
    {
        var error = Assert.Throws<TokenClaimException>(() =>
            AcquiredTokenTenant.Require(Acquisition("99999999-9999-9999-9999-999999999999"), Tenant));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }

    [Fact]
    public void TheRejectionNamesBothTenantsAndNoTokenMaterial()
    {
        var error = Assert.Throws<TokenClaimException>(() =>
            AcquiredTokenTenant.Require(Acquisition("99999999-9999-9999-9999-999999999999"), Tenant));

        Assert.Contains("99999999-9999-9999-9999-999999999999", error.Message, StringComparison.Ordinal);
        Assert.Contains(Tenant, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoExpectedTenantLeavesTheTokenAsAcquired(string? expectedTenantId) =>
        Assert.Equal("token-1", AcquiredTokenTenant.Require(Acquisition(Tenant), expectedTenantId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ALibraryThatReportsNoTenantLeavesTheTokenAsAcquired(string? acquiredTenantId) =>
        Assert.Equal("token-1", AcquiredTokenTenant.Require(Acquisition(acquiredTenantId), Tenant));

    [Fact]
    public void ANullAcquisitionIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => AcquiredTokenTenant.Require(null!, Tenant));
}
