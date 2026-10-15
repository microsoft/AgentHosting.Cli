using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentOAuthPlanTests
{
    private const string DeploymentId = "11111111-1111-1111-1111-111111111111";

    private static DeploymentOAuthCreateRequest Create(
        string? connection = "aad-conn",
        string? provider = "Aadv2",
        string? clientId = "client-1",
        string? clientSecret = "secret-1",
        params string[] parameters) =>
        new()
        {
            DeploymentId = DeploymentId,
            ConnectionName = connection!,
            ServiceProviderName = provider!,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Parameters = parameters,
        };

    [Fact]
    public void ValidateCreate_MissingConnection_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(connection: "  ")));
        Assert.Contains("--connection", ex.Message);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a")]
    [InlineData("-connection")]
    [InlineData("_connection")]
    [InlineData("connection/name")]
    [InlineData("connection@name")]
    public void ValidateCreate_InvalidResourceName_Throws(string connectionName)
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(connection: connectionName)));

        Assert.Contains("--connection", ex.Message);
        Assert.Contains("2-64 characters", ex.Message);
    }

    [Fact]
    public void ValidateCreate_ConnectionNameLongerThan64Characters_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(connection: new string('a', 65))));

        Assert.Contains("--connection", ex.Message);
        Assert.Contains("2-64 characters", ex.Message);
    }

    [Theory]
    [InlineData("a1")]
    [InlineData("OAuth connection_1.test-")]
    public void ValidateCreate_ValidResourceName_IsAccepted(string connectionName)
    {
        var plan = DeploymentOAuthPlan.ValidateCreate(Create(connection: connectionName));

        Assert.Equal(connectionName, plan.Request.ConnectionName);
    }

    [Fact]
    public void ValidateCreate_MissingServiceProvider_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(provider: null)));
        Assert.Contains("--provider-name", ex.Message);
    }

    [Fact]
    public void ValidateCreate_UnsupportedServiceProvider_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(provider: "new-cloud-provider")));
        Assert.Contains("--provider-name", ex.Message);
    }

    [Fact]
    public void ValidateCreate_NewCatalogueProvider_IsSupported()
    {
        var plan = DeploymentOAuthPlan.ValidateCreate(Create(provider: "GitHub"));

        Assert.Equal("github", plan.Request.ServiceProviderName);
    }

    [Fact]
    public void ValidateCreate_BlankCommonOAuthParameter_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(clientId: "   ")));
        Assert.Contains("--client-id", ex.Message);
    }

    [Fact]
    public void ValidateCreate_InvalidParameter_Throws()
    {
        const string credential = "SYNTHETIC_OAUTH_CREDENTIAL";
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(parameters: [$"={credential}"])));

        Assert.Contains("position 1", ex.Message);
        Assert.Contains("Key=Value", ex.Message);
        Assert.DoesNotContain(credential, ex.Message);
    }

    [Fact]
    public void ValidateCreate_InvalidDeploymentId_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create() with { DeploymentId = "not-a-guid" }));
        Assert.Contains("--deployment-id", ex.Message);
    }

    [Fact]
    public void ValidateCreate_InvalidAgentIdAndName_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create() with
            {
                DeploymentId = null,
                AgentId = "not-a-guid",
                DeploymentName = "bad name",
            }));
        Assert.Contains("--agent-id", ex.Message);
    }

    [Fact]
    public void ValidateCreate_DuplicateParameter_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateCreate(Create(
                parameters: ["TenantId=one", "tenantid=two"])));
        Assert.Contains("duplicate key at position 2", ex.Message);
        Assert.DoesNotContain("TenantId", ex.Message);
    }

    [Fact]
    public void ValidateCreate_NormalizesStableFields()
    {
        var normalized = DeploymentOAuthPlan.ValidateCreate(
            Create(connection: " aad-conn ", provider: "aadv2", parameters: [" TenantId=common "]));

        Assert.Equal("aad-conn", normalized.Request.ConnectionName);
        Assert.Equal("Aadv2", normalized.Request.ServiceProviderName);
        Assert.Equal("client-1", normalized.Request.ClientId);
        Assert.Equal("common", normalized.ParsedParameters["TenantId"]);
    }

    [Fact]
    public void Validate_MissingConnection_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.Validate(
                new DeploymentOAuthShowRequest { DeploymentId = DeploymentId, ConnectionName = null! }));
        Assert.Contains("--connection", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_NoFields_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateUpdate(
                new DeploymentOAuthUpdateRequest
                {
                    DeploymentId = DeploymentId,
                    ConnectionName = "aad-conn",
                }));
        Assert.Contains("At least one", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_AllowsEmptyValuesForClearing()
    {
        var request = DeploymentOAuthPlan.ValidateUpdate(
            new DeploymentOAuthUpdateRequest
            {
                DeploymentId = DeploymentId,
                ConnectionName = "aad-conn",
                Scopes = string.Empty,
                Parameters = ["TokenExchangeUrl="],
            });

        Assert.Equal(string.Empty, request.Request.Scopes);
        Assert.Equal(string.Empty, request.ParsedParameters["TokenExchangeUrl"]);
    }

    [Fact]
    public void ValidateProviderList_InvalidSubscription_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.ValidateProviderList(
                new DeploymentOAuthProviderListRequest { SubscriptionId = "not-a-guid" }));
        Assert.Contains("--subscription", ex.Message);
    }
}
