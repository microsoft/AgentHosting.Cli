using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestAuthenticationTests
{
    private const string ClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TargetTenant = "11111111-1111-1111-1111-111111111111";
    private const string ActiveUser = "alice@contoso.com";

    private static readonly AgentHostClientAppConfiguration LiveTestClient = AgentHostClientAppConfiguration.Create(ClientId, FakePlatformInfo.Linux());

    [Fact]
    public async Task AcquireDelegatedToken_UsesSharedSilentProviderAndCopilotInvokeScope()
    {
        var acquisition = new RecordingAcquisition(
            new TokenAcquisitionResult
            {
                AccessToken = "opaque-token",
                TenantId = TargetTenant,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        var silent = new RecordingSilentTokenProvider(acquisition.Result);
        var provider = CreateProvider(acquisition, silent);

        string token = await provider.AcquireDelegatedTokenAsync(
            TargetTenant,
            nonInteractive: false,
            cancellationToken: CancellationToken.None);

        Assert.Equal("opaque-token", token);
        Assert.Equal(
            ["https://api.powerplatform.com/CopilotStudio.Copilots.Invoke"],
            silent.Scopes);
        Assert.Equal(TargetTenant, silent.TargetTenantId);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_RejectsReturnedTokenFromAnotherTenant()
    {
        var acquisition = new RecordingAcquisition(
            new TokenAcquisitionResult
            {
                AccessToken = "must-not-escape",
                TenantId = "22222222-2222-2222-2222-222222222222",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        var silent = new RecordingSilentTokenProvider(acquisition.Result);
        var provider = CreateProvider(acquisition, silent);

        TokenClaimException failure = await Assert.ThrowsAsync<TokenClaimException>(() =>
            provider.AcquireDelegatedTokenAsync(
                TargetTenant,
                nonInteractive: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, failure.Failure);
        Assert.Equal(1, silent.CallCount);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_RejectsMissingReturnedTenant()
    {
        var acquisition = new RecordingAcquisition(
            new TokenAcquisitionResult
            {
                AccessToken = "must-not-escape",
                TenantId = null,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        var silent = new RecordingSilentTokenProvider(acquisition.Result);
        var provider = CreateProvider(acquisition, silent);

        TokenClaimException failure = await Assert.ThrowsAsync<TokenClaimException>(() =>
            provider.AcquireDelegatedTokenAsync(
                TargetTenant,
                nonInteractive: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, failure.Failure);
        Assert.Equal(1, silent.CallCount);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_RequiresResolvedDestinationTenant()
    {
        var acquisition = new RecordingAcquisition(
            new TokenAcquisitionResult
            {
                AccessToken = "opaque-token",
                TenantId = TargetTenant,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        var silent = new RecordingSilentTokenProvider(acquisition.Result);
        var provider = CreateProvider(acquisition, silent);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.AcquireDelegatedTokenAsync(
                string.Empty,
                nonInteractive: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal(0, silent.CallCount);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_NonInteractiveUsesSilentTokenProvider()
    {
        var acquisition = new RecordingAcquisition(
            new TokenAcquisitionResult
            {
                AccessToken = "opaque-token",
                TenantId = TargetTenant,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        var silent = new RecordingSilentTokenProvider(acquisition.Result);
        var provider = CreateProvider(acquisition, silent);

        string token = await provider.AcquireDelegatedTokenAsync(
            TargetTenant,
            nonInteractive: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal("opaque-token", token);
        Assert.Equal(
            ["https://api.powerplatform.com/CopilotStudio.Copilots.Invoke"],
            silent.Scopes);
        Assert.Equal(TargetTenant, silent.TargetTenantId);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_UsesConsentAcquisitionWhenSilentTokenRequiresInteraction()
    {
        var acquisition = new RecordingAcquisition(new TokenAcquisitionResult
        {
            AccessToken = "consented-token",
            TenantId = TargetTenant,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
        var silent = new RecordingSilentTokenProvider(
            new InteractionRequiredException("Consent is required."));
        var provider = CreateProvider(acquisition, silent);

        string token = await provider.AcquireDelegatedTokenAsync(
            TargetTenant,
            nonInteractive: false,
            cancellationToken: CancellationToken.None);

        Assert.Equal("consented-token", token);
        Assert.Equal(1, silent.CallCount);
        Assert.Equal(1, acquisition.CallCount);
        Assert.False(acquisition.NonInteractive);
        Assert.Equal(ClientId, acquisition.ClientId.ToString("D"));
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/nativeclient", acquisition.RedirectUri);
        Assert.Equal(
            ["https://api.powerplatform.com/CopilotStudio.Copilots.Invoke"],
            acquisition.Scopes);
        Assert.Equal(TargetTenant, acquisition.TargetTenantId);
    }

    [Fact]
    public async Task AcquireDelegatedToken_NonInteractiveDoesNotFallBackToConsentUi()
    {
        var acquisition = new RecordingAcquisition(new TokenAcquisitionResult
        {
            AccessToken = "must-not-be-used",
            TenantId = TargetTenant,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
        var silent = new RecordingSilentTokenProvider(
            new InteractionRequiredException("Consent is required."));
        var provider = CreateProvider(acquisition, silent);

        await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            provider.AcquireDelegatedTokenAsync(
                TargetTenant,
                nonInteractive: true,
                cancellationToken: CancellationToken.None));

        Assert.Equal(1, silent.CallCount);
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_UsesConsentAcquisition_WhenTheBrokerCannotSignInSilently()
    {
        var acquisition = new RecordingAcquisition(new TokenAcquisitionResult
        {
            AccessToken = "consented-token",
            TenantId = TargetTenant,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
        var silent = new RecordingSilentTokenProvider(new BrokerUnavailableException("wam_runtime_init_failed"));
        var provider = CreateProvider(acquisition, silent);

        string token = await provider.AcquireDelegatedTokenAsync(TargetTenant, nonInteractive: false, CancellationToken.None);

        Assert.Equal("consented-token", token);
        Assert.Equal(1, silent.CallCount);
        Assert.Equal(1, acquisition.CallCount);
    }

    [Fact]
    public async Task AcquireDelegatedToken_NonInteractive_ReportsTheBrokerFailure_WithoutConsentUi()
    {
        var acquisition = new RecordingAcquisition(new TokenAcquisitionResult
        {
            AccessToken = "must-not-be-used",
            TenantId = TargetTenant,
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        });
        var unavailable = new BrokerUnavailableException("wam_runtime_init_failed");
        var provider = CreateProvider(acquisition, new RecordingSilentTokenProvider(unavailable));

        Assert.Same(unavailable, await Assert.ThrowsAsync<BrokerUnavailableException>(() => provider.AcquireDelegatedTokenAsync(TargetTenant, nonInteractive: true, CancellationToken.None)));
        Assert.Equal(0, acquisition.CallCount);
    }

    [Fact]
    public void ConsentSelection_UsesOnlyTheExactActiveAccount()
    {
        string? selected = ConsentAccountSelection.Select(
            ["alternate-account", "active-account"],
            "active-account",
            account => account);

        Assert.Equal("active-account", selected);
        Assert.Null(ConsentAccountSelection.Select(
            ["alternate-account"],
            "active-account",
            account => account));
    }

    [Fact]
    public async Task ConsentAcquisition_ReturnsTheCachedTokenWithoutInteraction_ByTryingSilentlyFirst()
    {
        using var directory = new TempDir();
        var acquisition = await ConsentHoldingTheActiveAccountAsync(directory.Path);

        var result = await acquisition.AcquireAsync(
            LiveTestClient.ClientId,
            LiveTestClient.RedirectUri.OriginalString,
            [MsalLiveTestTokenProvider.CopilotStudioInvokeScope],
            TargetTenant,
            "Consent is required.",
            "Rerun interactively.",
            InteractionRequiredResource.Unspecified,
            CancellationToken.None);

        Assert.Equal(MsalTokenCacheSeed.AccessToken(ActiveUser), result.AccessToken);
    }

    [Fact]
    public async Task AcquireDelegatedToken_GoesStraightToConsent_WithoutRepeatingTheSilentAttemptThatFailed()
    {
        using var directory = new TempDir();
        var silent = new RecordingSilentTokenProvider(new InteractionRequiredException("Consent is required."));
        var provider = new MsalLiveTestTokenProvider(silent, await ConsentHoldingTheActiveAccountAsync(directory.Path), Options(), FakePlatformInfo.Linux());

        var required = await Assert.ThrowsAsync<InteractionRequiredException>(() => provider.AcquireDelegatedTokenAsync(TargetTenant, nonInteractive: false, CancellationToken.None));

        Assert.Equal("Rerun interactively so you can sign in or consent to Copilot Studio, then retry.", required.Remediation);
        Assert.Equal(1, silent.CallCount);
    }

    private static MsalLiveTestTokenProvider CreateProvider(
        RecordingAcquisition acquisition,
        RecordingSilentTokenProvider? silent = null) =>
        new(
            silent ?? new RecordingSilentTokenProvider(acquisition.Result),
            acquisition,
            Options(),
            FakePlatformInfo.Linux());

    private static AgentHostAuthOptions Options() =>
        new()
        {
            ClientId = ClientId,
            ConfigDirectory = "unused",
        };

    private static async Task<MsalConsentTokenAcquisition> ConsentHoldingTheActiveAccountAsync(string configDirectory)
    {
        var clientProvider = FakeClientProviders.Create(FakePlatformInfo.Linux(), configDirectory);
        var client = await clientProvider.GetClientAsync(useBroker: false, tenantId: null, CancellationToken.None, clientIdOverride: LiveTestClient.ClientId, redirectUriOverride: LiveTestClient.RedirectUri.OriginalString);
        MsalTokenCacheSeed.Seed(client, TargetTenant, MsalLiveTestTokenProvider.CopilotStudioInvokeScope, ActiveUser);
        var profiles = InMemoryAuthProfileStore.SignedOut();
        await profiles.UpsertAsync(new AuthProfile { AccountKey = MsalTokenCacheSeed.HomeAccountId(ActiveUser, TargetTenant), Username = ActiveUser, TenantId = TargetTenant, FlowType = "device-code" }, CancellationToken.None);
        return new(clientProvider, profiles, new StaticInteractionPolicy(false), new RecordingMessagePresenter());
    }

    private sealed class RecordingAcquisition(TokenAcquisitionResult result)
        : MsalLiveTestTokenProvider.ILiveTestConsentTokenAcquisition
    {
        private readonly TokenAcquisitionResult _result = result;

        public TokenAcquisitionResult Result => _result;
        public int CallCount { get; private set; }
        public bool NonInteractive { get; private set; }
        public Guid ClientId { get; private set; }
        public string RedirectUri { get; private set; } = string.Empty;
        public IReadOnlyList<string> Scopes { get; private set; } = [];
        public string? TargetTenantId { get; private set; }
        public string InteractionMessage { get; private set; } = string.Empty;
        public string InteractionRemediation { get; private set; } = string.Empty;
        public InteractionRequiredResource InteractionResource { get; private set; }

        public Task<TokenAcquisitionResult> AcquireAsync(
            bool nonInteractive,
            Guid clientId,
            string redirectUri,
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            string interactionMessage,
            string interactionRemediation,
            InteractionRequiredResource interactionResource,
            CancellationToken cancellationToken)
        {
            CallCount++;
            NonInteractive = nonInteractive;
            ClientId = clientId;
            RedirectUri = redirectUri;
            Scopes = scopes;
            TargetTenantId = targetTenantId;
            InteractionMessage = interactionMessage;
            InteractionRemediation = interactionRemediation;
            InteractionResource = interactionResource;
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingSilentTokenProvider(object outcome) : ITokenProvider
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> Scopes { get; private set; } = [];
        public string? TargetTenantId { get; private set; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Scopes = scopes;
            TargetTenantId = targetTenantId;
            return outcome switch
            {
                TokenAcquisitionResult result => Task.FromResult(result),
                Exception exception => Task.FromException<TokenAcquisitionResult>(exception),
                _ => throw new InvalidOperationException("Unexpected silent-token test outcome."),
            };
        }
    }
}
