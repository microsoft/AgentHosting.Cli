using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeployServiceTests
{
    private const string DataPlaneEndpoint = "https://env-1.api.powerplatform.com/";
    private const string ControlPlaneEndpoint = "https://api.powerplatform.com/";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string HomeEnvironment = "22222222-2222-2222-2222-222222222222";
    private const string TargetEnvironment = "33333333-3333-3333-3333-333333333333";
    private const string Repository = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string InitialRevision = "aaaaaaaa-1111-aaaa-1111-aaaaaaaaaaaa";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Entra = "88888888-8888-8888-8888-888888888888";

    [Fact]
    public async Task Deploy_ConsumesExistingRevision_AndCallsOnlyTheDeploymentTransport()
    {
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var service = Service(agents, deployments);
        var deploymentName = new string('a', AgentDeployRequest.DeploymentNameMaxLength);

        var result = await service.DeployAsync(
            Request() with { DeploymentName = deploymentName },
            default);

        Assert.Equal(1, agents.Calls);
        Assert.Equal(DataPlaneEndpoint, agents.LastRequest!.Endpoint.AbsoluteUri);
        Assert.Equal(1, deployments.Calls);
        var sent = Assert.IsType<AmsDeploymentCreateTransportRequest>(deployments.LastRequest);
        Assert.Equal(ControlPlaneEndpoint, sent.Endpoint.AbsoluteUri);
        Assert.Equal(Revision, sent.PackageRevisionId);
        Assert.Equal(TargetEnvironment, sent.TargetEnvironmentId);
        Assert.Equal(deploymentName, sent.DeploymentName);
        Assert.Equal("Dev", sent.DeploymentType);
        Assert.Equal(Entra, sent.EntraId);
        Assert.Equal("AppReg", sent.EntraIdType);
        Assert.Equal("DotNet", sent.Language);
        Assert.Equal("10", sent.LanguageVersion);

        Assert.Equal("deployment-projection-acknowledged", result.Status);
        Assert.Equal(Revision, result.PackageRevisionId);
        Assert.Equal(Deployment, result.DeploymentId);
        Assert.Equal("current-grs", result.PackageSource);
        Assert.True(result.ProjectionAcknowledged);
        Assert.False(result.PublicDeployOperationCreated);
        Assert.False(result.TargetLocalPackageStagingPerformed);
        Assert.False(result.RuntimeReadinessProven);
        Assert.False(result.CrossIslandDemonstrated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Deploy_RequiresExplicitTargetBeforeInventoryOrServiceCalls(string? targetEnvironmentId)
    {
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var inventory = new FakeEnvironmentClient(EnvironmentFixtures.Environment(HomeEnvironment));
        var service = Service(agents, deployments, inventory: inventory);
        var inventoryCalls = inventory.ListCalls;

        var exception = await Assert.ThrowsAsync<UsageException>(() =>
            service.DeployAsync(Request() with { TargetEnvironmentId = targetEnvironmentId! }, default));

        Assert.Contains("--target-environment-id", exception.Message, StringComparison.Ordinal);
        Assert.Equal(inventoryCalls, inventory.ListCalls);
        Assert.Equal(0, agents.Calls);
        Assert.Equal(0, deployments.Calls);
    }

    [Fact]
    public async Task Deploy_RejectsOverlongDeploymentNameBeforeInventoryOrServiceCalls()
    {
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var inventory = new FakeEnvironmentClient(EnvironmentFixtures.Environment(HomeEnvironment));
        var service = Service(agents, deployments, inventory: inventory);
        var inventoryCalls = inventory.ListCalls;

        var exception = await Assert.ThrowsAsync<UsageException>(() =>
            service.DeployAsync(
                Request() with
                {
                    DeploymentName = new string('a', AgentDeployRequest.DeploymentNameMaxLength + 1),
                },
                default));

        Assert.Contains("--deployment-name", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            AgentDeployRequest.DeploymentNameMaxLength.ToString(),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(inventoryCalls, inventory.ListCalls);
        Assert.Equal(0, agents.Calls);
        Assert.Equal(0, deployments.Calls);
    }

    [Fact]
    public async Task Deploy_ExplicitSameHomeTarget_IsResolvedAndPreserved()
    {
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var inventory = new FakeEnvironmentClient(EnvironmentFixtures.Environment(HomeEnvironment));
        var service = Service(agents, deployments, inventory: inventory);
        var inventoryCalls = inventory.ListCalls;

        var result = await service.DeployAsync(
            Request() with { TargetEnvironmentId = HomeEnvironment },
            default);

        Assert.Equal(inventoryCalls + 2, inventory.ListCalls);
        Assert.Equal(1, agents.Calls);
        Assert.Equal(1, deployments.Calls);
        Assert.Equal(HomeEnvironment, deployments.LastRequest!.TargetEnvironmentId);
        Assert.Equal(HomeEnvironment, result.TargetEnvironmentId);
        Assert.False(result.CrossIslandDemonstrated);
    }

    [Fact]
    public void DeployContracts_CarryNoPackageBytesOrUploadReceipt()
    {
        Assert.DoesNotContain(
            typeof(AgentDeployRequest).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Bytes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(AgentDeployResult).GetProperties(),
            property => property.Name.Contains("OperationId", StringComparison.OrdinalIgnoreCase)
                && property.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(AgentDeployService).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IAmsPackagePublicationTransport));
    }

    [Fact]
    public async Task Deploy_WhenTargetTenantDiffers_RejectsBeforeDeploymentMutation()
    {
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var service = Service(
            new RecordingAgentReadTransport(BoundAgent()),
            deployments,
            targetTenantId: "99999999-9999-9999-9999-999999999999");

        var exception = await Assert.ThrowsAsync<EnvironmentTenantMismatchException>(
            () => service.DeployAsync(Request(), default));

        Assert.Contains(TargetEnvironment, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, deployments.Calls);
    }

    [Fact]
    public async Task Deploy_WhenRevisionDoesNotMatch_RejectsTheAcknowledgement()
    {
        var deployments = new RecordingDeploymentTransport(request =>
            CreatedDeployment(request) with
            {
                PackageRevisionId = "99999999-9999-9999-9999-999999999999",
            });
        var service = Service(new RecordingAgentReadTransport(BoundAgent()), deployments);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(
            () => service.DeployAsync(Request(), default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Equal(1, deployments.Calls);
        Assert.DoesNotContain("clientRequestId=", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("serviceRequestId=", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x-ms-request-id")]
    [InlineData("x-ms-correlation-request-id")]
    [InlineData("request-id")]
    [InlineData(null)]
    public async Task Deploy_WhenAdapterReturnsWrongRevision_PreservesCorrelation(string? serviceRequestHeader)
    {
        const string serviceRequestId = "deployment-service-request-id";
        var body =
            $$"""
            {
              "id": "{{Deployment}}",
              "agentId": "{{Agent}}",
              "tenantId": "{{EnvironmentFixtures.ContosoTenant}}",
              "homeEnvironmentId": "{{HomeEnvironment}}",
              "environmentId": "{{TargetEnvironment}}",
              "packageRevisionId": "99999999-9999-9999-9999-999999999999",
              "packageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "packageSizeInBytes": 1024,
              "agentType": 0,
              "deploymentType": 0,
              "deploymentName": "dev-proof",
              "entraAppId": "{{Entra}}",
              "entraAppIdType": 0,
              "language": "DotNet",
              "languageVersion": "10",
              "endpoint": "https://runtime.example/api/messages",
              "isCustomEndpoint": false,
              "isQuarantined": false
            }
            """;
        var response = FakeHttpResponse.Json(HttpStatusCode.Created, body);
        if (serviceRequestHeader is not null)
        {
            response.Headers.TryAddWithoutValidation(serviceRequestHeader, serviceRequestId);
        }

        var handler = new CapturingPackageHandler(_ => response);
        using var httpClient = new HttpClient(handler);
        var adapter = new AmsDeploymentAdapter(httpClient, new FakePpapiTokenProvider());
        var service = Service(new RecordingAgentReadTransport(BoundAgent()), adapter);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            service.DeployAsync(Request(), default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.True(Guid.TryParse(sent.ClientRequestId, out _));
        Assert.Contains($"clientRequestId={sent.ClientRequestId}", exception.Message, StringComparison.Ordinal);
        if (serviceRequestHeader is not null)
        {
            Assert.Contains($"serviceRequestId={serviceRequestId}", exception.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("serviceRequestId=", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Deploy_WhenAgentIsNotBound_RejectsBeforeDeploymentMutation()
    {
        var deployments = new RecordingDeploymentTransport(CreatedDeployment);
        var service = Service(
            new RecordingAgentReadTransport(BoundAgent() with { RepositoryBindingStatus = "Unbound" }),
            deployments);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(
            () => service.DeployAsync(Request(), default));

        Assert.Equal("agent-not-deployable", exception.ErrorKind);
        Assert.Equal(0, deployments.Calls);
    }

    [Fact]
    public async Task Deploy_RequiresAnExistingNonEmptyPackageRevision()
    {
        var service = Service(
            new RecordingAgentReadTransport(BoundAgent()),
            new RecordingDeploymentTransport(CreatedDeployment));

        var exception = await Assert.ThrowsAsync<UsageException>(
            () => service.DeployAsync(Request() with { PackageRevisionId = Guid.Empty.ToString() }, default));

        Assert.Contains("--package-revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_RejectsAnUntrustedControlPlaneEndpoint()
    {
        var target = PowerPlatformApiTargetConfig.Public with
        {
            GlobalEndpoint = new Uri("https://attacker.example/"),
        };

        Assert.Throws<EndpointTrustException>(() => Service(
            new RecordingAgentReadTransport(BoundAgent()),
            new RecordingDeploymentTransport(CreatedDeployment),
            target: target));
    }

    [Fact]
    public async Task Update_ReadsCurrentRevisionThenPatchesOnlyTheExistingDeployment()
    {
        var deployments = UpdateTransport();

        var result = await Service(new RecordingAgentReadTransport(BoundAgent()), deployments)
            .UpdateAsync(UpdateRequest(), default);

        Assert.Equal(1, deployments.Reads);
        Assert.Equal(1, deployments.Updates);
        Assert.Equal(0, deployments.Calls);
        var read = Assert.IsType<AmsDeploymentReadTransportRequest>(deployments.LastReadRequest);
        Assert.Equal(ControlPlaneEndpoint, read.Endpoint.AbsoluteUri);
        Assert.Equal(EnvironmentFixtures.Audience, read.Audience);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, read.TenantId);
        Assert.Equal(HomeEnvironment, read.HomeEnvironmentId);
        Assert.Equal(Agent, read.AgentId);
        Assert.Equal(Deployment, read.DeploymentId);
        var update = Assert.IsType<AmsDeploymentUpdateTransportRequest>(deployments.LastUpdateRequest);
        Assert.Equal(Revision, update.PackageRevisionId);
        Assert.Equal(InitialRevision, update.ExpectedPackageRevisionId);
        Assert.Equal("agent-deployment-updated", result.Status);
        Assert.Equal(TargetEnvironment, result.TargetEnvironmentId);
        Assert.Equal(Entra, result.EntraId);
    }

    [Theory]
    [InlineData("bad", Deployment, Revision)]
    [InlineData(Agent, "bad", Revision)]
    [InlineData(Agent, Deployment, "bad")]
    [InlineData(Agent, "00000000-0000-0000-0000-000000000000", Revision)]
    public async Task Update_InvalidIdentifierSendsNothing(
        string agentId,
        string deploymentId,
        string revision)
    {
        var deployments = UpdateTransport();

        await Assert.ThrowsAsync<UsageException>(() =>
            Service(new RecordingAgentReadTransport(BoundAgent()), deployments).UpdateAsync(
                UpdateRequest() with
                {
                    AgentId = agentId,
                    DeploymentId = deploymentId,
                    PackageRevisionId = revision,
                },
                default));

        Assert.Equal(0, deployments.Reads);
        Assert.Equal(0, deployments.Updates);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("home")]
    [InlineData("agent")]
    [InlineData("deployment")]
    [InlineData("revision")]
    public async Task Update_InvalidReadNeverPatchesAndPreservesCorrelation(string field)
    {
        var deployments = UpdateTransport(read: request =>
        {
            var current = CurrentDeployment(request) with
            {
                ClientRequestId = "read-client-id",
                ServiceRequestId = "read-service-id",
            };
            return field switch
            {
                "tenant" => current with { TenantId = "different" },
                "home" => current with { HomeEnvironmentId = "different" },
                "agent" => current with { AgentId = "different" },
                "deployment" => current with { DeploymentId = "different" },
                "revision" => current with { PackageRevisionId = "not-a-guid" },
                _ => throw new InvalidOperationException(),
            };
        });

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Service(new RecordingAgentReadTransport(BoundAgent()), deployments)
                .UpdateAsync(UpdateRequest(), default));

        Assert.Contains("clientRequestId=read-client-id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=read-service-id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No update request was sent", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, deployments.Reads);
        Assert.Equal(0, deployments.Updates);
    }

    [Fact]
    public async Task Update_CancellationAfterReadPreventsPatch()
    {
        using var cancellation = new CancellationTokenSource();
        var deployments = UpdateTransport(read: request =>
        {
            cancellation.Cancel();
            return CurrentDeployment(request);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(new RecordingAgentReadTransport(BoundAgent()), deployments)
                .UpdateAsync(UpdateRequest(), cancellation.Token));

        Assert.Equal(1, deployments.Reads);
        Assert.Equal(0, deployments.Updates);
    }

    [Fact]
    public async Task Update_ConcurrencyConflictIsNotRefreshedOrRetried()
    {
        var deployments = UpdateTransport(update: _ => throw AgentDeployException.ServiceRejected(
            "Conflict",
            ExitCodes.Failure,
            "agent-deployment-concurrency-conflict",
            "Run the update again."));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Service(new RecordingAgentReadTransport(BoundAgent()), deployments)
                .UpdateAsync(UpdateRequest(), default));

        Assert.Equal("agent-deployment-concurrency-conflict", exception.ErrorKind);
        Assert.Equal(1, deployments.Reads);
        Assert.Equal(1, deployments.Updates);
        Assert.Equal(InitialRevision, deployments.LastUpdateRequest!.ExpectedPackageRevisionId);
    }

    [Fact]
    public async Task Update_EachInvocationReadsFreshStateAndSameRevisionStillPatches()
    {
        var currentRevision = InitialRevision;
        var deployments = UpdateTransport(
            read: request => CurrentDeployment(request) with { PackageRevisionId = currentRevision },
            update: request =>
            {
                Assert.Equal(currentRevision, request.ExpectedPackageRevisionId);
                currentRevision = request.PackageRevisionId;
                return UpdatedDeployment(request);
            });
        var service = Service(new RecordingAgentReadTransport(BoundAgent()), deployments);

        await service.UpdateAsync(UpdateRequest(), default);
        await service.UpdateAsync(UpdateRequest(), default);
        await service.UpdateAsync(
            UpdateRequest() with { PackageRevisionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
            default);

        Assert.Equal(3, deployments.Reads);
        Assert.Equal(3, deployments.Updates);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", currentRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_RawTransportFailureDistinguishesReadFromMutation(bool failRead)
    {
        var deployments = UpdateTransport(
            read: request => failRead
                ? throw new HttpRequestException("private read detail")
                : CurrentDeployment(request),
            update: request => failRead
                ? UpdatedDeployment(request)
                : throw new HttpRequestException("private update detail"));

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() =>
            Service(new RecordingAgentReadTransport(BoundAgent()), deployments)
                .UpdateAsync(UpdateRequest(), default));

        Assert.Equal(
            failRead ? "agent-deployment-read-failed" : "agent-deployment-transport-failed",
            exception.ErrorKind);
        Assert.Equal(failRead, exception.Message.Contains("No update request was sent", StringComparison.Ordinal));
        Assert.Equal(!failRead, exception.Message.Contains("indeterminate", StringComparison.Ordinal));
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
    }

    private static AgentDeployRequest Request() => new()
    {
        AgentId = Agent,
        PackageRevisionId = Revision,
        EnvironmentId = HomeEnvironment,
        TargetEnvironmentId = TargetEnvironment,
        DeploymentName = "dev-proof",
        DeploymentType = "dev",
        EntraId = Entra,
        EntraIdType = "app-reg",
    };

    private static AgentDeploymentPackageUpdateRequest UpdateRequest() => new()
    {
        AgentId = Agent,
        DeploymentId = Deployment,
        PackageRevisionId = Revision,
        EnvironmentId = HomeEnvironment,
    };

    private static RecordingDeploymentTransport UpdateTransport(
        Func<AmsDeploymentReadTransportRequest, AmsDeploymentCurrentResult>? read = null,
        Func<AmsDeploymentUpdateTransportRequest, AmsDeploymentCreateResult>? update = null) =>
        new(_ => throw new InvalidOperationException("Update must not create a deployment."))
        {
            OnRead = read ?? CurrentDeployment,
            OnUpdate = update ?? UpdatedDeployment,
        };

    private static AmsDeploymentCurrentResult CurrentDeployment(
        AmsDeploymentReadTransportRequest request) => new()
        {
            DeploymentId = request.DeploymentId,
            TenantId = request.TenantId,
            HomeEnvironmentId = request.HomeEnvironmentId,
            AgentId = request.AgentId,
            PackageRevisionId = InitialRevision,
        };

    private static AmsDeploymentCreateResult UpdatedDeployment(
        AmsDeploymentUpdateTransportRequest request) => new()
        {
            DeploymentId = request.DeploymentId.ToUpperInvariant(),
            AgentId = request.AgentId,
            TenantId = request.TenantId,
            HomeEnvironmentId = request.HomeEnvironmentId,
            TargetEnvironmentId = TargetEnvironment,
            PackageRevisionId = request.PackageRevisionId,
            PackageSha256 = new string('a', 64),
            PackageSizeInBytes = 1024,
            AgentType = "CodeAgent",
            DeploymentName = "dev-proof",
            DeploymentType = "Dev",
            EntraId = Entra,
            EntraIdType = "AppReg",
            Language = "DotNet",
            LanguageVersion = "10",
            RuntimeEndpoint = "https://runtime.example/api/messages",
            IsCustomEndpoint = false,
            IsQuarantined = false,
        };

    private static AmsAgentReadResult BoundAgent() => new()
    {
        AgentId = Agent,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = HomeEnvironment,
        RepositoryId = Repository,
        RepositoryBindingStatus = "Bound",
        AgentType = "CodeAgent",
        Language = "DotNet",
        LanguageVersion = "10",
    };

    private static AmsDeploymentCreateResult CreatedDeployment(
        AmsDeploymentCreateTransportRequest request) => new()
        {
            DeploymentId = Deployment,
            AgentId = request.AgentId,
            TenantId = request.TenantId,
            HomeEnvironmentId = request.HomeEnvironmentId,
            TargetEnvironmentId = request.TargetEnvironmentId,
            PackageRevisionId = request.PackageRevisionId,
            PackageSha256 = new string('a', 64),
            PackageSizeInBytes = 1024,
            AgentType = "CodeAgent",
            DeploymentName = request.DeploymentName,
            DeploymentType = request.DeploymentType,
            EntraId = request.EntraId,
            EntraIdType = request.EntraIdType,
            Language = request.Language,
            LanguageVersion = request.LanguageVersion,
            RuntimeEndpoint = "https://runtime.example/api/messages",
            IsCustomEndpoint = false,
            IsQuarantined = false,
        };

    private static AgentDeployService Service(
        IAmsAgentReadTransport agents,
        IAmsDeploymentTransport deployments,
        string targetTenantId = EnvironmentFixtures.ContosoTenant,
        PowerPlatformApiTargetConfig? target = null,
        FakeEnvironmentClient? inventory = null)
    {
        var profiles = InMemoryAuthProfileStore.SignedIn(
            "op@contoso.com",
            EnvironmentFixtures.ContosoTenant);
        var selectionStore = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(
            inventory ?? new FakeEnvironmentClient(
                EnvironmentFixtures.Environment(HomeEnvironment),
                EnvironmentFixtures.Environment(
                    TargetEnvironment,
                    "Contoso Target",
                    endpoint: "https://target.api.powerplatform.com/",
                    tenantId: targetTenantId)),
            selectionStore,
            profiles);
        environments.SelectAsync(HomeEnvironment, default).GetAwaiter().GetResult();

        return new AgentDeployService(
            target ?? PowerPlatformApiTargetConfig.Public,
            environments,
            agents,
            deployments);
    }

    private sealed class RecordingAgentReadTransport(AmsAgentReadResult result) : IAmsAgentReadTransport
    {
        public AmsAgentReadTransportRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }

        public Task<AmsAgentReadResult> GetAsync(
            AmsAgentReadTransportRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingDeploymentTransport(
        Func<AmsDeploymentCreateTransportRequest, AmsDeploymentCreateResult> result)
        : IAmsDeploymentTransport
    {
        public Func<AmsDeploymentReadTransportRequest, AmsDeploymentCurrentResult>? OnRead { get; init; }
        public Func<AmsDeploymentUpdateTransportRequest, AmsDeploymentCreateResult>? OnUpdate { get; init; }
        public AmsDeploymentCreateTransportRequest? LastRequest { get; private set; }
        public AmsDeploymentReadTransportRequest? LastReadRequest { get; private set; }
        public AmsDeploymentUpdateTransportRequest? LastUpdateRequest { get; private set; }
        public int Calls { get; private set; }
        public int Reads { get; private set; }
        public int Updates { get; private set; }

        public Task<AmsDeploymentCurrentResult> GetAsync(
            AmsDeploymentReadTransportRequest request,
            CancellationToken cancellationToken)
        {
            Reads++;
            LastReadRequest = request;
            return Task.FromResult(
                (OnRead ?? throw new InvalidOperationException("Deployment read was not expected."))(request));
        }

        public Task<AmsDeploymentCreateResult> CreateAsync(
            AmsDeploymentCreateTransportRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(result(request));
        }

        public Task<AmsDeploymentCreateResult> UpdateAsync(
            AmsDeploymentUpdateTransportRequest request,
            CancellationToken cancellationToken)
        {
            Updates++;
            LastUpdateRequest = request;
            return Task.FromResult(
                (OnUpdate ?? throw new InvalidOperationException("Deployment update was not expected."))(request));
        }
    }
}
