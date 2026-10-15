using System.Diagnostics.Tracing;
using System.Security.Claims;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthenticationResultTenantObserverTests
{
    private static readonly Guid TenantId =
        new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void GenuineSdkResult_ObservesPairedTenantLocalUserPrincipal()
    {
        var observer = new CapturingObserver();
        Guid principalObjectId =
            new("22222222-2222-2222-2222-222222222222");

        AuthAccount account = AuthAccountMapper.FromResult(
            Result(
                TenantId.ToString("D"),
                UserPrincipal(TenantId, principalObjectId)),
            observer,
            "33333333-3333-3333-3333-333333333333");

        AuthenticatedTenantContext context = Assert.Single(observer.Contexts);
        Assert.Equal(TenantId, context.TenantId);
        Assert.Equal(principalObjectId, context.PrincipalObjectId);
        Assert.Equal("home-object.home-tenant", context.AccountKey);
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333",
            context.ClientId);
        Assert.Equal("home-object.home-tenant", account.HomeAccountId);
        Assert.Equal(TenantId, account.AuthenticatedTenantId);
        Assert.Equal(principalObjectId, account.PrincipalObjectId);
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333",
            account.AuthenticatedClientId);
    }

    [Fact]
    public void DisabledObserver_DoesNotReadTenantOrClaims()
    {
        var observer = new CapturingObserver { IsObservationEnabled = false };

        AuthenticationResultTenantObserver.Observe(
            Result("not-a-guid", new ThrowingClaimsPrincipal()),
            observer);

        Assert.Empty(observer.Contexts);
    }

    [Fact]
    public void ClaimReadFailureKeepsTenantOnlyContext()
    {
        var observer = new CapturingObserver();

        AuthenticationResultTenantObserver.Observe(
            Result(
                TenantId.ToString("D"),
                new ThrowingClaimsPrincipal()),
            observer);

        AuthenticatedTenantContext context = Assert.Single(observer.Contexts);
        Assert.Equal(TenantId, context.TenantId);
        Assert.Null(context.PrincipalObjectId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UnknownTenant_DoesNotObserve(string tenantId)
    {
        var observer = new CapturingObserver();

        AuthenticationResultTenantObserver.Observe(Result(tenantId), observer);

        Assert.Empty(observer.Contexts);
    }

    [Fact]
    public void AppIdentityDoesNotQualifyAsUserButKeepsTenant()
    {
        var observer = new CapturingObserver();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", "not-a-guid"),
            new Claim("idtyp", "app"),
        ],
        "synthetic"));

        AuthenticationResultTenantObserver.Observe(
            Result(TenantId.ToString("D"), principal),
            observer);

        AuthenticatedTenantContext context = Assert.Single(observer.Contexts);
        Assert.Equal(TenantId, context.TenantId);
        Assert.Null(context.PrincipalObjectId);
    }

    [Theory]
    [InlineData("missing-oid")]
    [InlineData("duplicate-oid")]
    [InlineData("missing-tid")]
    [InlineData("duplicate-tid")]
    [InlineData("mismatched-tid")]
    [InlineData("invalid-oid")]
    [InlineData("empty-oid")]
    [InlineData("duplicate-idtyp")]
    public void InvalidOrAmbiguousClaimsKeepTenantOnly(string claimCase)
    {
        ClaimsPrincipal principal = PrincipalForCase(claimCase);
        var observer = new CapturingObserver();

        AuthenticationResultTenantObserver.Observe(
            Result(TenantId.ToString("D"), principal),
            observer);

        AuthenticatedTenantContext context = Assert.Single(observer.Contexts);
        Assert.Equal(TenantId, context.TenantId);
        Assert.Null(context.PrincipalObjectId);
    }

    [Fact]
    public void HomeAccountIdIsNeverReinterpretedAsPrincipalObjectId()
    {
        var observer = new CapturingObserver();

        AuthenticationResultTenantObserver.Observe(
            Result(TenantId.ToString("D"), new ClaimsPrincipal()),
            observer);

        AuthenticatedTenantContext context = Assert.Single(observer.Contexts);
        Assert.Equal("home-object.home-tenant", context.AccountKey);
        Assert.Null(context.PrincipalObjectId);
    }

    [Fact]
    public void MissingAccount_PreservesProductValidationAndDoesNotObserve()
    {
        var observer = new CapturingObserver();

        Assert.Throws<ArgumentNullException>(() =>
            AuthAccountMapper.FromResult(
                CreateResult(
                    TenantId.ToString("D"),
                    new ClaimsPrincipal(),
                    account: null),
                observer));

        Assert.Empty(observer.Contexts);
    }

    [Fact]
    public async Task ObserverFailure_DoesNotChangeNormalAuthenticationMapping()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.AuthenticationObserverCallback);

        AuthAccount account = AuthAccountMapper.FromResult(
            Result(TenantId.ToString("D")),
            new ThrowingObserver());

        Assert.Equal("synthetic@example.invalid", account.Username);
        Assert.Equal("home-object.home-tenant", account.HomeAccountId);
        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.AuthenticationObserverCallback);
    }

    [Fact]
    public async Task ObservationAvailabilityFailure_DoesNotChangeNormalAuthenticationMapping()
    {
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.AuthenticationObserverAvailability);

        AuthAccount account = AuthAccountMapper.FromResult(
            Result(TenantId.ToString("D")),
            new ThrowingAvailabilityObserver());

        Assert.Equal("synthetic@example.invalid", account.Username);
        Assert.Equal("home-object.home-tenant", account.HomeAccountId);
        await AssertCategoryAsync(
            listener,
            OptionalMeasurementFailureCategory.AuthenticationObserverAvailability);
    }

    private static AuthenticationResult Result(
        string tenantId,
        ClaimsPrincipal? principal = null) =>
        CreateResult(tenantId, principal ?? new ClaimsPrincipal(), Account());

    private static AuthenticationResult CreateResult(
        string tenantId,
        ClaimsPrincipal principal,
        IAccount? account) =>
        new(
            accessToken: "synthetic-access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: "22222222-2222-2222-2222-222222222222",
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(2),
            tenantId,
            account,
            idToken: "synthetic-id-token",
            scopes: ["synthetic.scope"],
            correlationId: Guid.NewGuid(),
            tokenType: "Bearer",
            authenticationResultMetadata: null!,
            claimsPrincipal: principal,
            spaAuthCode: null,
            additionalResponseParameters: null!);

    private static SyntheticAccount Account() => new();

    private static ClaimsPrincipal UserPrincipal(
        Guid tenantId,
        Guid principalObjectId) =>
        new(new ClaimsIdentity(
        [
            new Claim("tid", tenantId.ToString("D")),
            new Claim("oid", principalObjectId.ToString("D")),
            new Claim("idtyp", "user"),
        ],
        "synthetic"));

    private static ClaimsPrincipal PrincipalForCase(string claimCase)
    {
        Guid principalObjectId =
            new("22222222-2222-2222-2222-222222222222");
        Guid otherTenant =
            new("33333333-3333-3333-3333-333333333333");
        List<Claim> claims =
        [
            new("tid", TenantId.ToString("D")),
            new("oid", principalObjectId.ToString("D")),
            new("idtyp", "user"),
        ];
        switch (claimCase)
        {
            case "missing-oid":
                claims.RemoveAll(claim => claim.Type == "oid");
                break;
            case "duplicate-oid":
                claims.Add(new("oid", principalObjectId.ToString("D")));
                break;
            case "missing-tid":
                claims.RemoveAll(claim => claim.Type == "tid");
                break;
            case "duplicate-tid":
                claims.Add(new("tid", TenantId.ToString("D")));
                break;
            case "mismatched-tid":
                claims.RemoveAll(claim => claim.Type == "tid");
                claims.Add(new("tid", otherTenant.ToString("D")));
                break;
            case "invalid-oid":
                claims.RemoveAll(claim => claim.Type == "oid");
                claims.Add(new("oid", "not-a-guid"));
                break;
            case "empty-oid":
                claims.RemoveAll(claim => claim.Type == "oid");
                claims.Add(new("oid", Guid.Empty.ToString("D")));
                break;
            case "duplicate-idtyp":
                claims.Add(new("idtyp", "user"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(claimCase));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "synthetic"));
    }

    private static async Task AssertCategoryAsync(
        DiagnosticEventListener listener,
        OptionalMeasurementFailureCategory expected)
    {
        EventWrittenEventArgs diagnostic = await listener.NextAsync();
        Assert.Equal(
            (int)expected,
            Assert.IsType<int>(Assert.Single(diagnostic.Payload!)));
    }

    private sealed class SyntheticAccount : IAccount
    {
        public string Username => "synthetic@example.invalid";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId { get; } =
            new("home-object.home-tenant", "home-object", "home-tenant");
    }

    private sealed class CapturingObserver : IAuthenticationResultObserver
    {
        public bool IsObservationEnabled { get; init; } = true;

        public List<AuthenticatedTenantContext> Contexts { get; } = [];

        public void Observe(AuthenticatedTenantContext context) =>
            Contexts.Add(context);
    }

    private sealed class ThrowingObserver : IAuthenticationResultObserver
    {
        public void Observe(AuthenticatedTenantContext context) =>
            throw new InvalidOperationException("synthetic observer failure");
    }

    private sealed class ThrowingAvailabilityObserver : IAuthenticationResultObserver
    {
        public bool IsObservationEnabled =>
            throw new InvalidOperationException("synthetic availability failure");

        public void Observe(AuthenticatedTenantContext context) =>
            throw new InvalidOperationException("must not observe");
    }

    private sealed class ThrowingClaimsPrincipal : ClaimsPrincipal
    {
        public override IEnumerable<Claim> Claims =>
            throw new InvalidOperationException("claims must not be read");

        public override IEnumerable<Claim> FindAll(string type) =>
            throw new InvalidOperationException("claims must not be searched");
    }
}
