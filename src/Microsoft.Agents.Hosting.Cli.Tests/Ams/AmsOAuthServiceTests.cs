using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsOAuthServiceTests
{
    private const string PeriodRefusal = "--connection must not end with a period for an AMS deployment, because AMS addresses the connection in the request path, where a final period can be lost.";

    [Fact]
    public async Task Create_SendsTheValidatedContentThroughTheHomeControlPlane()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput();

        var result = await Service(oauth, standardInput).CreateConnectionAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = " graph login ", ServiceProviderName = " aadv2 ", ClientId = " client-1 ", ClientSecret = "secret-1", Scopes = " openid profile ", Parameters = [" TenantId=common "] }), default);

        var sent = Assert.IsType<AmsOAuthCreateTransportRequest>(Assert.Single(oauth.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal(("graph login", "aadv2"), (sent.ConnectionName, sent.ServiceProviderName));
        Assert.Equal(("client-1", "secret-1", "openid profile"), (sent.Settings.ClientId, sent.Settings.ClientSecret, sent.Settings.Scopes));
        Assert.Equal("common", Assert.Single(sent.Settings.Parameters).Value);
        Assert.Equal(DeploymentOperationStatus.Created, result.Status);
        Assert.Equal(("Connection", "graph"), (result.TargetName, result.TargetInstanceName));
        Assert.Same(oauth.Connection, result.Payload);
        Assert.Equal(0, standardInput.Reads);
    }

    [Fact]
    public async Task Create_LeavesTheProviderRulesToAmsAndReportsAnExistingConnection()
    {
        var oauth = new RecordingOAuthTransport { IsCreated = false };

        var result = await Service(oauth).CreateConnectionAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = "graph", ServiceProviderName = "NotInTheCliAllowlist" }), default);

        var sent = Assert.IsType<AmsOAuthCreateTransportRequest>(Assert.Single(oauth.Requests));
        Assert.Equal("NotInTheCliAllowlist", sent.ServiceProviderName);
        Assert.Equal((null, null, null), (sent.Settings.ClientId, sent.Settings.ClientSecret, sent.Settings.Scopes));
        Assert.Empty(sent.Settings.Parameters);
        Assert.Equal(DeploymentOperationStatus.Existed, result.Status);
    }

    [Fact]
    public async Task Create_SendsThePipedSecretExactly()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput(" piped secret \n");

        await Service(oauth, standardInput).CreateConnectionAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = "graph", ServiceProviderName = "Aadv2", ClientSecretFromStandardInput = true }), default);

        Assert.Equal(1, standardInput.Reads);
        Assert.Equal(" piped secret ", Assert.IsType<AmsOAuthCreateTransportRequest>(Assert.Single(oauth.Requests)).Settings.ClientSecret);
    }

    [Fact]
    public async Task Update_SendsOnlyTheChangedSettingsWithTheClearedValues()
    {
        var oauth = new RecordingOAuthTransport();

        var result = await Service(oauth).UpdateConnectionAsync(Request(new DeploymentOAuthUpdateRequest { ConnectionName = " graph ", Scopes = string.Empty, Parameters = ["TokenExchangeUrl="] }), default);

        var sent = Assert.IsType<AmsOAuthUpdateTransportRequest>(Assert.Single(oauth.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("graph", sent.ConnectionName);
        Assert.Equal((null, null, string.Empty), (sent.Settings.ClientId, sent.Settings.ClientSecret, sent.Settings.Scopes));
        Assert.Equal(string.Empty, sent.Settings.Parameters["TokenExchangeUrl"]);
        Assert.Equal(DeploymentOperationStatus.Updated, result.Status);
        Assert.Same(oauth.Connection, result.Payload);
    }

    [Fact]
    public async Task Update_SendsThePipedSecret()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput("rotated-secret\r\n");

        await Service(oauth, standardInput).UpdateConnectionAsync(Request(new DeploymentOAuthUpdateRequest { ConnectionName = "graph", ClientSecretFromStandardInput = true }), default);

        Assert.Equal(1, standardInput.Reads);
        Assert.Equal("rotated-secret", Assert.IsType<AmsOAuthUpdateTransportRequest>(Assert.Single(oauth.Requests)).Settings.ClientSecret);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("show")]
    public async Task ConnectionOperations_RefuseANameEndingInAPeriodBeforeAnyCallOrRead(string operation)
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput("piped-secret\n");

        var exception = await Assert.ThrowsAsync<UsageException>(() => Invoke(Service(oauth, standardInput), operation, "graph. "));

        Assert.Equal(PeriodRefusal, exception.Message);
        Assert.Empty(oauth.Requests);
        Assert.Equal(0, standardInput.Reads);
    }

    [Fact]
    public async Task ContentRules_MatchLocalModeAndRefuseBeforeAnyCallOrRead()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput("piped-secret\n");
        var content = new DeploymentOAuthCreateRequest { DeploymentId = Deployment, ConnectionName = "graph", ServiceProviderName = "Aadv2", Parameters = ["ClientId=client-1"], ClientSecretFromStandardInput = true };

        var local = Assert.Throws<UsageException>(() => DeploymentOAuthPlan.ValidateCreate(content));
        var ams = await Assert.ThrowsAsync<UsageException>(() => Service(oauth, standardInput).CreateConnectionAsync(Request(content), default));

        Assert.Equal(local.Message, ams.Message);
        Assert.Empty(oauth.Requests);
        Assert.Equal(0, standardInput.Reads);
    }

    [Fact]
    public async Task ContentRules_IgnoreTheLocalDeploymentAddressThatTheCommandAlsoBinds()
    {
        var oauth = new RecordingOAuthTransport();
        var content = new DeploymentOAuthShowRequest { ConnectionName = "graph", AgentId = Agent, DeploymentId = Deployment };

        Assert.Throws<UsageException>(() => DeploymentOAuthPlan.Validate(content));
        var result = await Service(oauth).GetConnectionAsync(Request(content), default);

        var sent = Assert.IsType<AmsOAuthConnectionTransportRequest>(Assert.Single(oauth.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("graph", sent.ConnectionName);
        Assert.Equal(DeploymentOperationStatus.Retrieved, result.Status);
        Assert.Same(oauth.Connection, result.Payload);
    }

    [Fact]
    public async Task Delete_DeletesTheNamedConnection()
    {
        var oauth = new RecordingOAuthTransport();

        var result = await Service(oauth).DeleteConnectionAsync(Request(new DeploymentOAuthDeleteRequest { ConnectionName = " graph login " }), default);

        var sent = Assert.IsType<AmsOAuthConnectionTransportRequest>(Assert.Single(oauth.Requests));
        AssertDeployment(sent.Deployment);
        Assert.Equal("graph login", sent.ConnectionName);
        Assert.Equal(DeploymentOperationStatus.Deleted, result.Status);
        Assert.Null(result.Payload);
        Assert.Equal("graph login", result.TargetInstanceName);
    }

    [Fact]
    public async Task List_ReturnsTheConnectionsAmsAnsweredWithoutAnArmResponse()
    {
        var oauth = new RecordingOAuthTransport();

        var result = await Service(oauth).ListConnectionsAsync(Request(new DeploymentOAuthListRequest()), default);

        AssertDeployment(Assert.IsType<AmsDeploymentReadTransportRequest>(Assert.Single(oauth.Requests)));
        Assert.Equal(DeploymentOperationStatus.Listed, result.Status);
        Assert.Equal("Connections", result.TargetName);
        Assert.Equal([oauth.Connection], result.Payload!);
        Assert.Null(result.ArmResponse);
    }

    [Fact]
    public async Task ListProviders_ReturnsEveryProviderByNameOrTheNamedOne()
    {
        var oauth = new RecordingOAuthTransport();
        var service = Service(oauth);

        var all = await service.ListProvidersAsync(Request(new DeploymentOAuthProviderListRequest()), default);
        var named = await service.ListProvidersAsync(Request(new DeploymentOAuthProviderListRequest { ServiceProviderName = " AADV2 " }), default);

        Assert.Equal(["Aadv2", "github"], all.Select(provider => provider.Name));
        Assert.Equal("Aadv2", Assert.Single(named).Name);
        Assert.Equal(2, oauth.Requests.Count);
        Assert.All(oauth.Requests, request => AssertDeployment(Assert.IsType<AmsDeploymentReadTransportRequest>(request)));
    }

    [Fact]
    public async Task ListProviders_RefusesAnUnknownProviderAfterTheReadAndABlankOneBeforeIt()
    {
        var oauth = new RecordingOAuthTransport();
        var service = Service(oauth);

        var unknown = await Assert.ThrowsAsync<DeploymentException>(() => service.ListProvidersAsync(Request(new DeploymentOAuthProviderListRequest { ServiceProviderName = "line" }), default));
        var blank = await Assert.ThrowsAsync<UsageException>(() => service.ListProvidersAsync(Request(new DeploymentOAuthProviderListRequest { ServiceProviderName = " " }), default));

        Assert.Equal(DeploymentException.OAuthServiceProviderNotFound("line").Message, unknown.Message);
        Assert.Equal("--provider-name cannot be empty. Provide a value or omit the option.", blank.Message);
        Assert.Single(oauth.Requests);
    }

    [Theory]
    [InlineData(null, Deployment, HomeEnvironment, "--agent-id or --agent-name is required for `msagent deployment oauth create`.")]
    [InlineData(Agent, "not-a-guid", HomeEnvironment, "--deployment-id must be a non-empty GUID.")]
    [InlineData(Agent, Deployment, " ", "--environment-id requires a non-empty environment id.")]
    public async Task Address_InvalidIdsAreRefusedBeforeAnyCallOrRead(string? agentId, string deploymentId, string environmentId, string message)
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput("piped-secret\n");

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(oauth, standardInput).CreateConnectionAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = "graph", ServiceProviderName = "Aadv2", ClientSecretFromStandardInput = true }) with { AgentId = agentId, DeploymentId = deploymentId, EnvironmentId = environmentId }, default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(oauth.Requests);
        Assert.Equal(0, standardInput.Reads);
    }

    [Fact]
    public async Task Address_ByNames_ResolvesThroughTheListsAndRefusesARecordThatWasNeverDeployedBeforeReadingTheSecret()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput("piped-secret\n");
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var notDeployed = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)] };
        var byNames = Request(new DeploymentOAuthCreateRequest { ConnectionName = "graph", ServiceProviderName = "Aadv2", ClientSecretFromStandardInput = true }) with { AgentId = null, AgentName = "Contoso Agent", DeploymentId = null, DeploymentName = "dev-proof" };

        await Service(oauth, standardInput, Resolver(agents, new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] })).CreateConnectionAsync(byNames, default);
        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(oauth, standardInput, Resolver(agents, notDeployed)).CreateConnectionAsync(byNames, default));

        Assert.Single(oauth.Requests);
        Assert.Equal(1, standardInput.Reads);
        Assert.Equal("deployment-not-deployed", exception.ErrorKind);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        var oauth = new RecordingOAuthTransport();
        var standardInput = new FakeStandardInput();

        Assert.Throws<ArgumentNullException>(() => new AmsOAuthService(null!, oauth, standardInput));
        Assert.Throws<ArgumentNullException>(() => new AmsOAuthService(Resolver(), null!, standardInput));
        Assert.Throws<ArgumentNullException>(() => new AmsOAuthService(Resolver(), oauth, null!));
    }

    [Fact]
    public async Task Handlers_DispatchEveryRouteToItsOperation()
    {
        var oauth = new RecordingOAuthTransport();
        var service = Service(oauth);

        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>, DeploymentResult<DeploymentOAuthConnection>>)service).HandleAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = "graph", ServiceProviderName = "Aadv2" }), default);
        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>, DeploymentResult<DeploymentOAuthConnection>>)service).HandleAsync(Request(new DeploymentOAuthUpdateRequest { ConnectionName = "graph", ClientId = "client-2" }), default);
        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>, DeploymentResult<DeploymentOAuthConnection>>)service).HandleAsync(Request(new DeploymentOAuthDeleteRequest { ConnectionName = "graph" }), default);
        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>, DeploymentResult<DeploymentOAuthConnection>>)service).HandleAsync(Request(new DeploymentOAuthShowRequest { ConnectionName = "graph" }), default);
        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>, DeploymentResult<DeploymentOAuthConnection[]>>)service).HandleAsync(Request(new DeploymentOAuthListRequest()), default);
        await ((ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>)service).HandleAsync(Request(new DeploymentOAuthProviderListRequest()), default);

        Assert.Equal(["create", "update", "delete", "get", "list", "providers"], oauth.Operations);
    }

    [Fact]
    public void Composition_ServesEveryAmsOAuthRouteFromTheOneOAuthService()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions { ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5", ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        services.AddSingleton(new AmsEndpointProfile());
        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());
        services.AddSingleton<IClock>(new FixedClock(EnvironmentFixtures.SelectedAt));
        services.AddSingleton<IAmsOAuthTransport>(new RecordingOAuthTransport());
        services.AddSingleton<IAmsAgentReadTransport>(new PendingAmsAgentReadTransport());
        services.AddSingleton<IAmsDeploymentTransport>(new PendingAmsDeploymentTransport());
        services.AddSingleton<IStandardInput>(new FakeStandardInput());
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AmsOAuthService>();
        Type[] routes =
        [
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>, DeploymentResult<DeploymentOAuthConnection>>),
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>, DeploymentResult<DeploymentOAuthConnection>>),
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>, DeploymentResult<DeploymentOAuthConnection>>),
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>, DeploymentResult<DeploymentOAuthConnection>>),
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>, DeploymentResult<DeploymentOAuthConnection[]>>),
            typeof(ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>),
        ];

        Assert.All(routes, route => Assert.Same(service, provider.GetRequiredService(route)));
    }

    private static Task<DeploymentResult<DeploymentOAuthConnection>> Invoke(AmsOAuthService service, string operation, string connectionName) => operation switch
    {
        "create" => service.CreateConnectionAsync(Request(new DeploymentOAuthCreateRequest { ConnectionName = connectionName, ServiceProviderName = "Aadv2", ClientSecretFromStandardInput = true }), default),
        "update" => service.UpdateConnectionAsync(Request(new DeploymentOAuthUpdateRequest { ConnectionName = connectionName, ClientSecretFromStandardInput = true }), default),
        "delete" => service.DeleteConnectionAsync(Request(new DeploymentOAuthDeleteRequest { ConnectionName = connectionName }), default),
        "show" => service.GetConnectionAsync(Request(new DeploymentOAuthShowRequest { ConnectionName = connectionName }), default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static AgentDeploymentAmsOAuthRequest<TOAuthRequest> Request<TOAuthRequest>(TOAuthRequest content)
        where TOAuthRequest : class => new() { AgentId = Agent, DeploymentId = Deployment, EnvironmentId = HomeEnvironment, OAuth = content };

    private static AmsOAuthService Service(IAmsOAuthTransport oauth, IStandardInput? standardInput = null, AmsAddressResolver? addresses = null) => new(addresses ?? Resolver(), oauth, standardInput ?? new FakeStandardInput());

    private sealed class RecordingOAuthTransport : IAmsOAuthTransport
    {
        public List<object> Requests { get; } = [];
        public List<string> Operations { get; } = [];
        public DeploymentOAuthConnection Connection { get; } = new() { ResourceId = "/bots/ams/connections/graph", ConnectionName = "graph", ServiceProviderId = "provider-aadv2", ServiceProviderDisplayName = "Azure Active Directory v2" };
        public bool IsCreated { get; init; } = true;

        public Task<IReadOnlyList<OAuthProviderDefinition>> ListProvidersAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => Record<IReadOnlyList<OAuthProviderDefinition>>("providers", request, [new() { Id = "provider-github", Name = "github", DisplayName = "GitHub" }, new() { Id = "provider-aadv2", Name = "Aadv2", DisplayName = "Azure Active Directory v2" }]);

        public Task<IReadOnlyList<DeploymentOAuthConnection>> ListAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => Record<IReadOnlyList<DeploymentOAuthConnection>>("list", request, [Connection]);

        public Task<DeploymentOAuthConnection> GetAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken) => Record("get", request, Connection);

        public Task<AmsOAuthCreateResult> CreateAsync(AmsOAuthCreateTransportRequest request, CancellationToken cancellationToken) => Record("create", request, new AmsOAuthCreateResult(Connection, IsCreated));

        public Task<DeploymentOAuthConnection> UpdateAsync(AmsOAuthUpdateTransportRequest request, CancellationToken cancellationToken) => Record("update", request, Connection);

        public Task DeleteAsync(AmsOAuthConnectionTransportRequest request, CancellationToken cancellationToken) => Record("delete", request, true);

        private Task<T> Record<T>(string operation, object request, T result)
        {
            Operations.Add(operation);
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
