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

    [Fact]
    public void ContentValidators_NeedNoDeploymentAddress_WhileTheLocalValidatorsDo()
    {
        var create = Create(connection: " aad-conn ") with { DeploymentId = null };
        var update = new DeploymentOAuthUpdateRequest { ConnectionName = " aad-conn ", Scopes = "openid" };
        var show = new DeploymentOAuthShowRequest { ConnectionName = " aad-conn " };

        Assert.Equal("aad-conn", DeploymentOAuthPlan.ValidateCreateContent(create).Request.ConnectionName);
        Assert.Equal("aad-conn", DeploymentOAuthPlan.ValidateUpdateContent(update).Request.ConnectionName);
        Assert.Equal("aad-conn", DeploymentOAuthPlan.ValidateContent(show).ConnectionName);
        Assert.Contains("--deployment-id", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreate(create)).Message);
        Assert.Contains("--deployment-id", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateUpdate(update)).Message);
        Assert.Contains("--deployment-id", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.Validate(show)).Message);
    }

    [Fact]
    public void ValidateCreateContent_LeavesTheProviderToAmsButStillRequiresOne()
    {
        var plan = DeploymentOAuthPlan.ValidateCreateContent(Create(provider: " NotInTheCliAllowlist "));

        Assert.Equal("NotInTheCliAllowlist", plan.Request.ServiceProviderName);
        Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreate(Create(provider: "NotInTheCliAllowlist")));
        Assert.Contains("--provider-name", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreateContent(Create(provider: " "))).Message);
    }

    [Theory]
    [InlineData("ClientId=client-2", "--client-id")]
    [InlineData("clientsecret=secret-2", "--client-secret")]
    [InlineData("Scopes=openid", "--scopes")]
    public void ContentValidators_RefuseCommonFieldsInParameters(string assignment, string option)
    {
        Assert.Contains(option, Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreateContent(Create(parameters: [assignment]))).Message);
        Assert.Contains(option, Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateUpdateContent(new DeploymentOAuthUpdateRequest { ConnectionName = "aad-conn", Parameters = [assignment] })).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ContentValidators_RefuseABlankClientSecret(string clientSecret)
    {
        Assert.Contains("--client-secret cannot be empty", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreateContent(Create(clientSecret: clientSecret))).Message);
        Assert.Contains("--client-secret cannot be empty", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn", ClientSecret = clientSecret })).Message);
    }

    [Fact]
    public void ContentValidators_RefuseBothSecretSources()
    {
        Assert.Equal("--client-secret-stdin cannot be combined with --client-secret.", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreate(Create() with { ClientSecretFromStandardInput = true })).Message);
        Assert.Equal("--client-secret-stdin cannot be combined with --client-secret.", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateUpdateContent(new DeploymentOAuthUpdateRequest { ConnectionName = "aad-conn", ClientSecret = "secret-2", ClientSecretFromStandardInput = true })).Message);
    }

    [Fact]
    public void ValidateUpdate_SecretFromStandardInputIsAField()
    {
        var plan = DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn", ClientSecretFromStandardInput = true });

        Assert.True(plan.Request.ClientSecretFromStandardInput);
        Assert.Null(plan.Request.ClientSecret);
        Assert.Contains("--client-secret-stdin", Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn" })).Message);
    }

    [Fact]
    public async Task ReadClientSecretAsync_WithoutTheOption_ReadsNothing()
    {
        var standardInput = new FakeStandardInput("piped-secret\n");
        var plan = DeploymentOAuthPlan.ValidateCreate(Create());

        Assert.Same(plan, await DeploymentOAuthPlan.ReadClientSecretAsync(plan, standardInput, CancellationToken.None));
        Assert.Equal(0, standardInput.Reads);
    }

    public static TheoryData<string, string> PipedSecrets => new()
    {
        { "piped-secret\n", "piped-secret" },
        { "piped-secret\r\n", "piped-secret" },
        { "piped-secret", "piped-secret" },
        { " piped\tsecret \n", " piped\tsecret " },
    };

    [Theory]
    [MemberData(nameof(PipedSecrets))]
    public async Task ReadClientSecretAsync_RemovesOnlyTheFinalLineBreak(string piped, string expected)
    {
        var standardInput = new FakeStandardInput(piped);
        var plan = await DeploymentOAuthPlan.ReadClientSecretAsync(DeploymentOAuthPlan.ValidateCreate(Create(clientSecret: null) with { ClientSecretFromStandardInput = true }), standardInput, CancellationToken.None);

        Assert.IsType<DeploymentOAuthCreateRequest>(plan.Request);
        Assert.Equal(expected, plan.Request.ClientSecret);
        Assert.Equal("Aadv2", plan.Request.ServiceProviderName);
        Assert.Equal(16384, standardInput.MaximumBytes);
    }

    [Theory]
    [InlineData("", "read no secret")]
    [InlineData("\n", "read no secret")]
    [InlineData(" \t\r\n", "read no secret")]
    [InlineData("first\nsecond", "reads one line")]
    [InlineData("piped-secret\n\n", "reads one line")]
    [InlineData("first\r\nsecond\r\n", "reads one line")]
    [InlineData("piped-secret\r", "reads one line")]
    public async Task ReadClientSecretAsync_RefusesAnEmptyOrMultilineSecretWithoutShowingIt(string piped, string message)
    {
        var plan = DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn", ClientSecretFromStandardInput = true });

        var ex = await Assert.ThrowsAsync<UsageException>(() => DeploymentOAuthPlan.ReadClientSecretAsync(plan, new FakeStandardInput(piped), CancellationToken.None));

        Assert.Contains(message, ex.Message);
        Assert.DoesNotContain("first", ex.Message);
        Assert.DoesNotContain("piped-secret", ex.Message);
    }

    [Fact]
    public async Task ReadClientSecretAsync_RefusesATerminalWithoutReadingIt()
    {
        var standardInput = new FakeStandardInput("piped-secret\n", isRedirected: false);
        var plan = DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest { DeploymentId = DeploymentId, ConnectionName = "aad-conn", ClientSecretFromStandardInput = true });

        var ex = await Assert.ThrowsAsync<UsageException>(() => DeploymentOAuthPlan.ReadClientSecretAsync(plan, standardInput, CancellationToken.None));

        Assert.Contains("redirected standard input", ex.Message);
        Assert.Equal(0, standardInput.Reads);
    }
}
