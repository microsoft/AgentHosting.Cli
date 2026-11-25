using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

internal static class AmsBotServiceFixtures
{
    public const string Agent = "44444444-4444-4444-4444-444444444444";
    public const string OtherAgent = "45454545-4545-4545-4545-454545454545";
    public const string HomeEnvironment = "22222222-2222-2222-2222-222222222222";
    public const string TargetEnvironment = "33333333-3333-3333-3333-333333333333";
    public const string UnavailableEnvironment = "99999999-9999-9999-9999-999999999999";
    public const string Deployment = "77777777-7777-7777-7777-777777777777";
    public const string OtherDeployment = "78787878-7878-7878-7878-787878787878";
    public const string Entra = "88888888-8888-8888-8888-888888888888";
    public const string Revision = "66666666-6666-6666-6666-666666666666";
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string AgentETag = "W/\"agent-1\"";
    public const string DeploymentETag = "W/\"deployment-1\"";
    public const string RuntimeEndpoint = "https://runtime.example/api/messages";
    public const string TunnelEndpoint = "https://contoso-3978.usw2.devtunnels.ms/api/messages";

    public static readonly AmsServiceTarget HomeControlPlane = new() { Endpoint = new Uri("https://api.powerplatform.com/"), Audience = EnvironmentFixtures.Audience, RouteForm = AmsRouteForm.TenantHost, TenantId = EnvironmentFixtures.ContosoTenant, EnvironmentId = HomeEnvironment };

    public static readonly AmsServiceTarget HomeDataPlane = new() { Endpoint = new Uri("https://env-1.api.powerplatform.com/"), Audience = EnvironmentFixtures.Audience, RouteForm = AmsRouteForm.EnvironmentHost, TenantId = EnvironmentFixtures.ContosoTenant, EnvironmentId = HomeEnvironment };

    public static EnvironmentSelectionService Environments()
    {
        var environments = EnvironmentFixtures.Service(new FakeEnvironmentClient(EnvironmentFixtures.Environment(HomeEnvironment), EnvironmentFixtures.Environment(TargetEnvironment, "Contoso Target", endpoint: "https://target.api.powerplatform.com/")), new InMemoryEnvironmentSelectionStore(), InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));
        environments.SelectAsync(HomeEnvironment, default).GetAwaiter().GetResult();
        return environments;
    }

    public static AmsAddressResolver Resolver(IAmsAgentReadTransport agents, IAmsDeploymentTransport deployments, AmsEndpointProfile? endpoints = null) => new(AmsTargetFixtures.Targets(endpoints), Environments(), agents, deployments);

    public static AmsAddressResolver Resolver() => Resolver(new PendingAmsAgentReadTransport(), new PendingAmsDeploymentTransport());

    public static void AssertDeployment(AmsDeploymentReadTransportRequest deployment)
    {
        AmsTargetFixtures.AssertTarget(HomeControlPlane, deployment.ServiceTarget);
        Assert.Equal(Agent, deployment.AgentId);
        Assert.Equal(Deployment, deployment.DeploymentId);
    }

    public static AmsAgentReadResult CodeAgent(string agentId = Agent, string? displayName = "Contoso Agent", string agentType = "CodeAgent") => new()
    {
        AgentId = agentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = HomeEnvironment,
        AgentType = agentType,
        DisplayName = displayName,
        Description = "Answers questions.",
        ETag = AgentETag,
        State = "Active",
        RepositoryId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        RepositoryBindingStatus = "Bound",
        Language = "DotNet",
        LanguageVersion = "10",
    };

    public static AmsDeploymentResult DeploymentRecord(string deploymentId = Deployment, string deploymentName = "dev-proof", string state = AmsDeploymentResult.DeployedState, string deploymentType = "Dev") => new()
    {
        DeploymentId = deploymentId,
        AgentId = Agent,
        TenantId = EnvironmentFixtures.ContosoTenant,
        HomeEnvironmentId = HomeEnvironment,
        TargetEnvironmentId = TargetEnvironment,
        ETag = DeploymentETag,
        State = state,
        PackageRevisionId = state == AmsDeploymentResult.NotDeployedState ? null : Revision,
        PackageSha256 = state == AmsDeploymentResult.NotDeployedState ? null : new string('a', 64),
        PackageSizeInBytes = state == AmsDeploymentResult.NotDeployedState ? null : 1024,
        CommitId = state == AmsDeploymentResult.NotDeployedState ? null : Commit,
        AgentType = "CodeAgent",
        DeploymentName = deploymentName,
        DeploymentType = deploymentType,
        EntraId = Entra,
        EntraIdType = "AppReg",
        Language = "DotNet",
        LanguageVersion = "10",
        RuntimeEndpoint = state == AmsDeploymentResult.NotDeployedState ? null : RuntimeEndpoint,
        BotService = state == AmsDeploymentResult.NotDeployedState ? null : Bot(),
    };

    public static AmsDeploymentResult DevEndpointRecord(string? runtimeEndpoint, string requestedEndpoint, string state = AmsDeploymentResult.DeployedState) => DeploymentRecord(state: state) with
    {
        PackageRevisionId = null,
        PackageSha256 = null,
        PackageSizeInBytes = null,
        CommitId = null,
        IsCustomEndpoint = true,
        RuntimeEndpoint = runtimeEndpoint,
        RequestedEndpoint = requestedEndpoint,
    };

    public static AgentDeploymentBotService Bot(string name = "ams-bot-1") => new() { ResourceId = $"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/{name}", Name = name, MsaAppId = Entra, MsaAppType = "SingleTenant", MsaAppTenantId = EnvironmentFixtures.ContosoTenant, Endpoint = RuntimeEndpoint };
}

internal sealed class FakeAmsAgentTransport : IAmsAgentReadTransport, IAmsAgentTransport
{
    public List<AmsAgentReadResult> Agents { get; init; } = [];
    public List<AmsAgentPackage> Packages { get; init; } = [];
    public List<string> Calls { get; } = [];
    public List<object> Requests { get; } = [];
    public Func<AmsAgentUpdateTransportRequest, AmsAgentReadResult>? OnUpdate { get; init; }
    public Func<AmsAgentCascadeDeleteTransportRequest, AmsAgentCascadeDeleteResult>? OnCascadeDelete { get; init; }

    public Task<AmsAgentReadResult> GetAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken) => Answer("get", request, () => Agents.Single(agent => string.Equals(agent.AgentId, request.AgentId, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<AmsAgentReadResult>> ListAsync(AmsServiceTarget serviceTarget, CancellationToken cancellationToken) => Answer<IReadOnlyList<AmsAgentReadResult>>("list", serviceTarget, () => Agents);

    public Task<AmsAgentReadResult> UpdateAsync(AmsAgentUpdateTransportRequest request, CancellationToken cancellationToken) => Answer("update", request, () => (OnUpdate ?? (input => Agents.Single(agent => agent.AgentId == input.AgentId) with { DisplayName = input.DisplayName ?? Agents.Single(agent => agent.AgentId == input.AgentId).DisplayName, Description = input.Description, ETag = "W/\"agent-2\"" }))(request));

    public Task<AmsAgentCascadeDeleteResult> CascadeDeleteAsync(AmsAgentCascadeDeleteTransportRequest request, CancellationToken cancellationToken) => Answer("cascadeDelete", request, () => (OnCascadeDelete ?? (input => new AmsAgentCascadeDeleteResult { AgentId = input.AgentId, Deployments = [], KeptBots = [] }))(request));

    public Task<IReadOnlyList<AmsAgentPackage>> ListPackagesAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken) => Answer<IReadOnlyList<AmsAgentPackage>>("packages", request, () => Packages);

    private Task<T> Answer<T>(string call, object request, Func<T> answer)
    {
        Calls.Add(call);
        Requests.Add(request);
        return Task.FromResult(answer());
    }
}

internal sealed class FakeAmsDeploymentTransport : IAmsDeploymentTransport
{
    public List<AmsDeploymentResult> Deployments { get; init; } = [];
    public List<AgentAmsKeptBot> KeptBots { get; init; } = [];
    public Exception? ListFailure { get; init; }
    public List<string> Calls { get; } = [];
    public List<object> Requests { get; } = [];
    public Func<AmsDeploymentCreateTransportRequest, AmsDeploymentResult>? OnCreate { get; init; }
    public Func<AmsDeploymentUpdateTransportRequest, AmsDeploymentResult>? OnUpdate { get; init; }
    public Func<AmsDeploymentDeployTransportRequest, AmsDeploymentResult>? OnDeploy { get; init; }
    public Action<AmsDeploymentDeleteTransportRequest>? OnDelete { get; init; }

    public Task<AmsDeploymentResult> ReadAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken) => Answer("read", request, () => Find(request.DeploymentId));

    public Task<AmsDeploymentListResult> ListAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken) => Answer("list", request, () => ListFailure is null ? new AmsDeploymentListResult { Deployments = Deployments, KeptBots = KeptBots } : throw ListFailure);

    public Task<AmsDeploymentResult> CreateAsync(AmsDeploymentCreateTransportRequest request, CancellationToken cancellationToken) => Answer("create", request, () => (OnCreate ?? throw new InvalidOperationException("Deployment creation was not expected in this test."))(request));

    public Task<AmsDeploymentResult> UpdateAsync(AmsDeploymentUpdateTransportRequest request, CancellationToken cancellationToken) => Answer("update", request, () => (OnUpdate ?? (input => Find(input.DeploymentId) with { DeploymentName = input.DeploymentName ?? Find(input.DeploymentId).DeploymentName, RequestedEndpoint = input.RequestedEndpoint ?? Find(input.DeploymentId).RequestedEndpoint, ETag = "W/\"deployment-2\"" }))(request));

    public Task<AmsDeploymentResult> DeployAsync(AmsDeploymentDeployTransportRequest request, CancellationToken cancellationToken) => Answer("deploy", request, () => (OnDeploy ?? (input => AmsBotServiceFixtures.DeploymentRecord(input.DeploymentId, Find(input.DeploymentId).DeploymentName) with { CommitId = input.CommitId ?? AmsBotServiceFixtures.Commit }))(request));

    public Task DeleteAsync(AmsDeploymentDeleteTransportRequest request, CancellationToken cancellationToken) => Answer("delete", request, () =>
    {
        OnDelete?.Invoke(request);
        return true;
    });

    private AmsDeploymentResult Find(string deploymentId) => Deployments.Single(deployment => string.Equals(deployment.DeploymentId, deploymentId, StringComparison.OrdinalIgnoreCase));

    private Task<T> Answer<T>(string call, object request, Func<T> answer)
    {
        Calls.Add(call);
        Requests.Add(request);
        return Task.FromResult(answer());
    }
}
