using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentOAuthParametersTests
{
    [Theory]
    [InlineData("clientSecret")]
    [InlineData("Password")]
    [InlineData("ApiKey")]
    [InlineData("PrimaryKey0")]
    [InlineData("Key1")]
    [InlineData("Key2")]
    [InlineData("Key10")]
    [InlineData("accessToken")]
    [InlineData("refreshToken20")]
    [InlineData("credential")]
    [InlineData("serviceCredential3")]
    public void IsSecret_ClassifiesConservativeSecretNames(string name)
    {
        Assert.True(DeploymentOAuthParameters.IsSecret(name));
    }

    [Theory]
    [InlineData("cmekKeyVaultUrl")]
    [InlineData("tokenEndpoint")]
    [InlineData("credentialType")]
    public void IsSecret_PreservesNamesWithoutSecretSuffixes(string name)
    {
        Assert.False(DeploymentOAuthParameters.IsSecret(name));
    }

    [Fact]
    public void IsProviderSecret_UsesDeclaredSecureStringType()
    {
        Assert.True(DeploymentOAuthParameters.IsProviderSecret("securestring"));
        Assert.False(DeploymentOAuthParameters.IsProviderSecret("string"));
    }

    [Fact]
    public void SanitizeConnection_WithoutProviderMasksSecretsAndRetainsUnknownParameters()
    {
        using var document = JsonDocument.Parse("""{"secret":"value"}""");
        var connection = new DeploymentOAuthConnection
        {
            ResourceId = "connection-id",
            ConnectionName = "connection",
            ServiceProviderId = "provider",
            ServiceProviderDisplayName = "Provider",
            ClientId = "client-id",
            Scopes = ["scope-a", "scope-b"],
            Parameters = new Dictionary<string, string?>
            {
                ["ClientSecret"] = "secret",
                ["UnknownValue"] = "unknown",
            },
            RawArmResponse = document.RootElement.Clone(),
        };

        var result = DeploymentOAuthParameters.Sanitize(
            connection,
            provider: null,
            includeArmResponse: false);

        Assert.Equal("client-id", result.ClientId);
        Assert.Equal(["scope-a", "scope-b"], result.Scopes);
        Assert.Equal("***", result.Parameters["ClientSecret"]);
        Assert.Equal("unknown", result.Parameters["UnknownValue"]);
        Assert.DoesNotContain("ClientId", result.Parameters.Keys);
        Assert.DoesNotContain("Scopes", result.Parameters.Keys);
        Assert.Null(result.RawArmResponse);
    }

    [Fact]
    public void SanitizeConnection_WithProviderMasksDeclaredSecretsAndRemovesUnknownParameters()
    {
        using var document = JsonDocument.Parse("""{"secret":"value"}""");
        var connection = new DeploymentOAuthConnection
        {
            ResourceId = "connection-id",
            ConnectionName = "connection",
            ServiceProviderId = "provider",
            ServiceProviderDisplayName = "Provider",
            ClientId = "client-id",
            Scopes = ["scope-a", "scope-b"],
            Parameters = new Dictionary<string, string?>
            {
                ["ProviderPrivateValue"] = "private",
                ["ApiKey"] = "provider-declared-non-secret",
                ["TenantId"] = "tenant-id",
                ["UnknownValue"] = "unknown",
            },
            RawArmResponse = document.RootElement.Clone(),
        };
        var provider = new OAuthProviderDefinition
        {
            Id = "provider",
            Name = "provider",
            DisplayName = "Provider",
            Parameters =
            [
                new()
                {
                    Name = "ClientId",
                    DisplayName = "Client id",
                    Type = "string",
                },
                new()
                {
                    Name = "Scopes",
                    DisplayName = "Scopes",
                    Type = "string",
                },
                new()
                {
                    Name = "ProviderPrivateValue",
                    DisplayName = "Private value",
                    Type = "securestring",
                },
                new()
                {
                    Name = "ApiKey",
                    DisplayName = "API key",
                    Type = "string",
                },
                new()
                {
                    Name = "TenantId",
                    DisplayName = "Tenant id",
                    Type = "string",
                },
            ],
        };

        var result = DeploymentOAuthParameters.Sanitize(
            connection,
            provider,
            includeArmResponse: false);

        Assert.Equal("client-id", result.ClientId);
        Assert.Equal(["scope-a", "scope-b"], result.Scopes);
        Assert.Equal(BotServiceResponseRedactor.Mask, result.Parameters["ProviderPrivateValue"]);
        Assert.Equal("provider-declared-non-secret", result.Parameters["ApiKey"]);
        Assert.Equal("tenant-id", result.Parameters["TenantId"]);
        Assert.DoesNotContain("ClientId", result.Parameters.Keys);
        Assert.DoesNotContain("Scopes", result.Parameters.Keys);
        Assert.DoesNotContain("UnknownValue", result.Parameters.Keys);
        Assert.Null(result.RawArmResponse);
    }

    [Fact]
    public void SanitizeConnection_PreservesExplicitNullSecrets()
    {
        var connection = new DeploymentOAuthConnection
        {
            ResourceId = "connection-id",
            ConnectionName = "connection",
            ServiceProviderId = "provider",
            ServiceProviderDisplayName = "Provider",
            Parameters = new Dictionary<string, string?>
            {
                ["ClientSecret"] = null,
                ["ProviderPrivateValue"] = null,
            },
        };
        var provider = new OAuthProviderDefinition
        {
            Id = "provider",
            Name = "provider",
            DisplayName = "Provider",
            Parameters =
            [
                new()
                {
                    Name = "ClientSecret",
                    DisplayName = "Client secret",
                    Type = "securestring",
                },
                new()
                {
                    Name = "ProviderPrivateValue",
                    DisplayName = "Private value",
                    Type = "securestring",
                },
            ],
        };

        var withProvider = DeploymentOAuthParameters.Sanitize(
            connection,
            provider,
            includeArmResponse: false);
        var withoutProvider = DeploymentOAuthParameters.Sanitize(
            connection,
            provider: null,
            includeArmResponse: false);

        Assert.Null(withProvider.Parameters["ClientSecret"]);
        Assert.Null(withProvider.Parameters["ProviderPrivateValue"]);
        Assert.Null(withoutProvider.Parameters["ClientSecret"]);
        Assert.Null(withoutProvider.Parameters["ProviderPrivateValue"]);
    }
}
