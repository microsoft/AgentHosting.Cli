using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ArmScopedTokenProviderTests
{
    [Theory]
    [InlineData("https://management.azure.com")]
    [InlineData("https://management.azure.com/")]
    [InlineData("HTTPS://MANAGEMENT.AZURE.COM")]
    public async Task AzureResourceManagerResource_IsAcquiredFromTheConsentClient(string resource)
    {
        var armConsent = new RecordingArmConsentTokenProvider();
        var silent = new RecordingSilentTokenProvider();
        var provider = new ArmScopedTokenProvider(armConsent, silent);

        var result = await provider.AcquireTokenSilentAsync(resource, "tenant-1", CancellationToken.None);

        Assert.Equal("arm-consent-token", result.AccessToken);
        Assert.Equal("tenant-1", armConsent.TargetTenantId);
        Assert.Null(silent.Resource);
    }

    [Theory]
    [InlineData("https://api.powerplatform.com")]
    [InlineData("https://graph.microsoft.com")]
    [InlineData("https://contoso.crm.dynamics.com")]
    public async Task EveryOtherResource_FallsThroughToTheSilentProvider(string resource)
    {
        var armConsent = new RecordingArmConsentTokenProvider();
        var silent = new RecordingSilentTokenProvider();
        var provider = new ArmScopedTokenProvider(armConsent, silent);

        var result = await provider.AcquireTokenSilentAsync(resource, "tenant-2", CancellationToken.None);

        Assert.Equal("silent-token", result.AccessToken);
        Assert.Equal(resource, silent.Resource);
        Assert.Equal("tenant-2", silent.TargetTenantId);
        Assert.False(armConsent.Called);
    }

    [Fact]
    public async Task ScopeBasedAcquisition_AlwaysUsesTheSilentProvider()
    {
        var armConsent = new RecordingArmConsentTokenProvider();
        var silent = new RecordingSilentTokenProvider();
        var provider = new ArmScopedTokenProvider(armConsent, silent);

        var result = await provider.AcquireTokenSilentAsync(
            new[] { IdentityProvisioningConfig.ArmResource + "/.default" },
            "tenant-3",
            CancellationToken.None);

        Assert.Equal("silent-token", result.AccessToken);
        Assert.False(armConsent.Called);
    }

    [Fact]
    public void NullDependencies_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ArmScopedTokenProvider(null!, new RecordingSilentTokenProvider()));
        Assert.Throws<ArgumentNullException>(() => new ArmScopedTokenProvider(new RecordingArmConsentTokenProvider(), null!));
    }

    private sealed class RecordingArmConsentTokenProvider : IArmConsentTokenProvider
    {
        public bool Called { get; private set; }

        public string? TargetTenantId { get; private set; }

        public Task<TokenAcquisitionResult> AcquireArmTokenAsync(string? targetTenantId, CancellationToken cancellationToken)
        {
            Called = true;
            TargetTenantId = targetTenantId;

            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = "arm-consent-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        }
    }

    private sealed class RecordingSilentTokenProvider : ITokenProvider
    {
        public string? Resource { get; private set; }

        public IReadOnlyList<string>? Scopes { get; private set; }

        public string? TargetTenantId { get; private set; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(string resource, string? targetTenantId, CancellationToken cancellationToken)
        {
            Resource = resource;
            TargetTenantId = targetTenantId;

            return Result();
        }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken)
        {
            Scopes = scopes;
            TargetTenantId = targetTenantId;

            return Result();
        }

        private static Task<TokenAcquisitionResult> Result() => Task.FromResult(new TokenAcquisitionResult
        {
            AccessToken = "silent-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
    }
}
