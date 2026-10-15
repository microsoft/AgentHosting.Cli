using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Builds unsigned, structurally-real JWTs for claim-validation tests. These are fixtures, not
/// credentials: the signature segment is a constant, nothing is issued by an authority, and none of
/// them is accepted by any service. No live token is ever needed to test the claim contract.
/// </summary>
internal static class FakeJwt
{
    public static string Create(
        string? audience = "https://api.powerplatform.com/",
        string? applicationId = PowerPlatformApiPermissions.AgentHostCliApplicationId,
        string? tenantId = EnvironmentFixtures.ContosoTenant,
        string? scopes = PowerPlatformApiPermissions.EnvironmentsRead,
        string applicationClaimName = "appid",
        string? objectId = null)
    {
        var payload = new Dictionary<string, string>();
        if (audience is not null)
        {
            payload["aud"] = audience;
        }

        if (applicationId is not null)
        {
            payload[applicationClaimName] = applicationId;
        }

        if (tenantId is not null)
        {
            payload["tid"] = tenantId;
        }

        if (objectId is not null)
        {
            payload["oid"] = objectId;
        }

        if (scopes is not null)
        {
            payload["scp"] = scopes;
        }

        return $"{Segment("""{"alg":"none","typ":"JWT"}""")}.{Segment(JsonSerializer.Serialize(payload))}.fake-signature";
    }

    public static string CreateEncrypted() =>
        $"{Segment("""{"alg":"RSA-OAEP","enc":"A256GCM"}""")}.encrypted-key.initialization-vector.ciphertext.authentication-tag";

    private static string Segment(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class EncryptedTokenTests
{
    [Fact]
    public void AnEncryptedTokenIsRejectedAsUnreadable()
    {
        var error = Assert.Throws<TokenClaimException>(() => PpapiTokenClaimsReader.Read(FakeJwt.CreateEncrypted()));

        Assert.Equal(TokenClaimFailure.Malformed, error.Failure);
        Assert.Contains("encrypted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEncryptedTokenRejectionNamesNoTokenMaterial()
    {
        var token = FakeJwt.CreateEncrypted();

        var error = Assert.Throws<TokenClaimException>(() => PpapiTokenClaimsReader.Read(token));

        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ciphertext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedTokenIsStillRead() =>
        Assert.Equal(EnvironmentFixtures.ContosoTenant, PpapiTokenClaimsReader.Read(FakeJwt.Create()).TenantId);

    [Theory]
    [InlineData("one.two")]
    [InlineData("one.two.three.four")]
    public void ATokenWithAnUnsupportedSegmentCountIsRejected(string token)
    {
        var error = Assert.Throws<TokenClaimException>(() => PpapiTokenClaimsReader.Read(token));

        Assert.Equal(TokenClaimFailure.Malformed, error.Failure);
        Assert.DoesNotContain("encrypted", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Exact Power Platform API scope construction. The CLI keeps PAC's <c>//.default</c> form so its
/// behavior starts identical to PAC and any deviation is a visible, deliberate adaptation.
/// </summary>
public sealed class PpapiScopeBuilderTests{
    private static readonly string[] DefaultScopeSet = ["https://api.powerplatform.com//.default"];

    private static readonly string[] ExplicitScopeSet =
    [
        "https://api.powerplatform.com/PowerApps.Environments.Read",
        "https://api.powerplatform.com/PowerApps.Apps.Read",
        "https://api.powerplatform.com/PowerApps.Apps.Create",
        "https://api.powerplatform.com/PowerApps.Apps.Update",
    ];
    private readonly PpapiScopeBuilder _builder = new();

    [Theory]
    [InlineData("https://api.powerplatform.com/", "https://api.powerplatform.com//.default")]
    [InlineData("https://api.powerplatform.com", "https://api.powerplatform.com//.default")]
    [InlineData("https://api.powerplatform.com/some/path", "https://api.powerplatform.com//.default")]
    public void DefaultScope_UsesPacDoubleSlashForm(string audience, string expected) =>
        Assert.Equal(expected, _builder.DefaultScope(audience));

    [Fact]
    public void DefaultScopes_IsTheSingleScopeSetHandedToMsal() =>
        Assert.Equal(
            DefaultScopeSet,
            _builder.DefaultScopes("https://api.powerplatform.com/").ToArray());

    [Fact]
    public void ExplicitScopes_NameTheExistingPowerAppsPermissions_InOrder()
    {
        var scopes = _builder.ExplicitScopes(
            "https://api.powerplatform.com/",
            PowerPlatformApiPermissions.EnvironmentsRead,
            PowerPlatformApiPermissions.AppsRead,
            PowerPlatformApiPermissions.AppsCreate,
            PowerPlatformApiPermissions.AppsUpdate);

        Assert.Equal(
            ExplicitScopeSet,
            scopes.ToArray());
    }

    [Fact]
    public void ExplicitScopes_RequireAtLeastOnePermission() =>
        Assert.Throws<ArgumentException>(() => _builder.ExplicitScopes("https://api.powerplatform.com/"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void ExplicitScopes_RejectANonAbsoluteAudience(string audience) =>
        Assert.Throws<ArgumentException>(() =>
            _builder.ExplicitScopes(audience, PowerPlatformApiPermissions.EnvironmentsRead));

    [Fact]
    public void RequestedPermissions_ContainNoInventedAgentHostingScope()
    {
        var permissions = new[]
        {
            PowerPlatformApiPermissions.EnvironmentsRead,
            PowerPlatformApiPermissions.AppsRead,
            PowerPlatformApiPermissions.AppsCreate,
            PowerPlatformApiPermissions.AppsUpdate,
        };

        Assert.All(permissions, p => Assert.StartsWith("PowerApps.", p, StringComparison.Ordinal));

        // The permissions the plan explicitly refuses to request.
        Assert.DoesNotContain("PowerApps.Environments.GenerateResourceStorage", permissions);
        Assert.DoesNotContain(permissions, p => p.StartsWith("GitRepositories.", StringComparison.Ordinal));
        Assert.DoesNotContain(permissions, p => p.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The Power Platform API token-claim contract. Every case runs against a fabricated, unsigned JWT —
/// no live credential, no consent prompt, no browser.
/// </summary>
public sealed class PpapiTokenClaimsTests
{
    private static readonly string[] ReadScopes = ["PowerApps.Environments.Read", "PowerApps.Apps.Read"];
    private readonly PpapiTokenClaimsValidator _validator = new();

    private void Validate(string token, string permission = PowerPlatformApiPermissions.EnvironmentsRead) =>
        _validator.Validate(
            token,
            expectedAudience: "https://api.powerplatform.com/",
            expectedTenantId: EnvironmentFixtures.ContosoTenant,
            requiredPermission: permission);

    [Fact]
    public void Reader_ExtractsTheNonSecretClaimSet()
    {
        var claims = PpapiTokenClaimsReader.Read(FakeJwt.Create(
            scopes: "PowerApps.Environments.Read PowerApps.Apps.Read",
            objectId: "7a2b1c3d-4e5f-6071-8293-a4b5c6d7e8f9"));

        Assert.Equal("https://api.powerplatform.com/", claims.Audience);
        Assert.Equal(PowerPlatformApiPermissions.AgentHostCliApplicationId, claims.ApplicationId);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, claims.TenantId);
        Assert.Equal("7a2b1c3d-4e5f-6071-8293-a4b5c6d7e8f9", claims.ObjectId);
        Assert.Equal(
            ReadScopes,
            claims.Scopes.ToArray());
    }

    [Fact]
    public void Reader_ReportsNoObjectIdWhenTheTokenCarriesNone() =>
        Assert.Null(PpapiTokenClaimsReader.Read(FakeJwt.Create()).ObjectId);

    [Fact]
    public void Reader_AcceptsTheV2AzpClaimWhenAppidIsAbsent()
    {
        var claims = PpapiTokenClaimsReader.Read(FakeJwt.Create(applicationClaimName: "azp"));

        Assert.Equal(PowerPlatformApiPermissions.AgentHostCliApplicationId, claims.ApplicationId);
    }

    [Fact]
    public void ValidToken_IsAccepted() => Validate(FakeJwt.Create());

    [Fact]
    public void AuthenticationComposition_ValidatesTheConfiguredClientApplication()
    {
        const string configuredClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5";
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ClientId = configuredClientId,
            ConfigDirectory = Path.GetTempPath(),
        });

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<PpapiTokenClaimsValidator>();

        validator.Validate(
            FakeJwt.Create(applicationId: configuredClientId),
            expectedAudience: "https://api.powerplatform.com/",
            expectedTenantId: EnvironmentFixtures.ContosoTenant,
            requiredPermission: PowerPlatformApiPermissions.EnvironmentsRead);

        var error = Assert.Throws<TokenClaimException>(() => validator.Validate(
            FakeJwt.Create(),
            expectedAudience: "https://api.powerplatform.com/",
            expectedTenantId: EnvironmentFixtures.ContosoTenant,
            requiredPermission: PowerPlatformApiPermissions.EnvironmentsRead));
        Assert.Equal(TokenClaimFailure.WrongApplication, error.Failure);
    }

    [Fact]
    public void TrailingSlashDifferenceInAudience_IsTolerated() =>
        Validate(FakeJwt.Create(audience: "https://api.powerplatform.com"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("bad-base64!.bad-base64!.sig")]
    public void MalformedToken_IsRejected_WithoutEchoingTheToken(string token)
    {
        var error = Assert.Throws<TokenClaimException>(() => Validate(token));

        Assert.Equal(TokenClaimFailure.Malformed, error.Failure);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        if (!string.IsNullOrEmpty(token))
        {
            Assert.DoesNotContain(token, error.Message);
        }
    }

    [Fact]
    public void WrongAudience_IsAWiringErrorTheCallerCanCorrect()
    {
        var error = Assert.Throws<TokenClaimException>(() =>
            Validate(FakeJwt.Create(audience: "https://management.azure.com/")));

        Assert.Equal(TokenClaimFailure.WrongAudience, error.Failure);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
        Assert.Equal("wrong-audience", error.ErrorKind);
    }

    [Fact]
    public void MissingAudienceClaim_IsRejected()
    {
        var error = Assert.Throws<TokenClaimException>(() => Validate(FakeJwt.Create(audience: null)));

        Assert.Equal(TokenClaimFailure.WrongAudience, error.Failure);
    }

    [Fact]
    public void WrongApplication_IsRejected()
    {
        var error = Assert.Throws<TokenClaimException>(() =>
            Validate(FakeJwt.Create(applicationId: "99999999-9999-9999-9999-999999999999")));

        Assert.Equal(TokenClaimFailure.WrongApplication, error.Failure);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
    }

    [Fact]
    public void MissingApplicationClaim_IsRejected()
    {
        var error = Assert.Throws<TokenClaimException>(() => Validate(FakeJwt.Create(applicationId: null)));

        Assert.Equal(TokenClaimFailure.WrongApplication, error.Failure);
    }

    [Fact]
    public void WrongTenant_IsRejected_AndNamesBothTenants()
    {
        var error = Assert.Throws<TokenClaimException>(() =>
            Validate(FakeJwt.Create(tenantId: EnvironmentFixtures.FabrikamTenant)));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
        Assert.Contains(EnvironmentFixtures.FabrikamTenant, error.Message);
        Assert.Contains(EnvironmentFixtures.ContosoTenant, error.Message);
    }

    [Fact]
    public void PlatformValidTokenWithoutTheOperationPermission_IsAPermissionDenial()
    {
        // The token is fine for the platform; it simply lacks the delegated permission this operation
        // needs. That must not be reported as a sign-in problem.
        var error = Assert.Throws<TokenClaimException>(() =>
            Validate(FakeJwt.Create(scopes: "PowerApps.Apps.Read"), PowerPlatformApiPermissions.AppsUpdate));

        Assert.Equal(TokenClaimFailure.MissingPermission, error.Failure);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        Assert.Equal("permission-denied", error.ErrorKind);
        Assert.Contains(PowerPlatformApiPermissions.AppsUpdate, error.Message);
    }

    [Fact]
    public void MissingScpClaimEntirely_IsAPermissionDenial()
    {
        var error = Assert.Throws<TokenClaimException>(() => Validate(FakeJwt.Create(scopes: null)));

        Assert.Equal(TokenClaimFailure.MissingPermission, error.Failure);
    }

    [Fact]
    public void BlankExpectedTenant_IsARejection_NotASkippedCheck()
    {
        var error = Assert.Throws<TokenClaimException>(() => _validator.Validate(
            FakeJwt.Create(),
            expectedAudience: "https://api.powerplatform.com/",
            expectedTenantId: "",
            requiredPermission: PowerPlatformApiPermissions.EnvironmentsRead));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }

    [Fact]
    public void MissingTidClaim_IsARejection()
    {
        var error = Assert.Throws<TokenClaimException>(() => Validate(FakeJwt.Create(tenantId: null)));

        Assert.Equal(TokenClaimFailure.WrongTenant, error.Failure);
    }

    [Fact]
    public void ValidateWithoutTenantBinding_ChecksAudienceApplicationAndPermission_ButNotTenant()
    {
        // The inventory call runs before any tenant is known, so it uses the explicitly named method
        // rather than passing a blank tenant and relying on the check silently disabling itself.
        _validator.ValidateWithoutTenantBinding(
            FakeJwt.Create(tenantId: EnvironmentFixtures.FabrikamTenant),
            "https://api.powerplatform.com/",
            PowerPlatformApiPermissions.EnvironmentsRead);

        Assert.Throws<TokenClaimException>(() => _validator.ValidateWithoutTenantBinding(
            FakeJwt.Create(audience: "https://management.azure.com/"),
            "https://api.powerplatform.com/",
            PowerPlatformApiPermissions.EnvironmentsRead));

        Assert.Throws<TokenClaimException>(() => _validator.ValidateWithoutTenantBinding(
            FakeJwt.Create(scopes: PowerPlatformApiPermissions.AppsRead),
            "https://api.powerplatform.com/",
            PowerPlatformApiPermissions.EnvironmentsRead));
    }

    [Fact]
    public void ClaimFailures_NeverCarryTokenMaterial()
    {
        var token = FakeJwt.Create(audience: "https://management.azure.com/");
        var payloadSegment = token.Split('.')[1];

        var error = Assert.Throws<TokenClaimException>(() => Validate(token));

        Assert.DoesNotContain(token, error.Message);
        Assert.DoesNotContain(payloadSegment, error.Message);
        Assert.DoesNotContain("fake-signature", error.Message);
    }
}
