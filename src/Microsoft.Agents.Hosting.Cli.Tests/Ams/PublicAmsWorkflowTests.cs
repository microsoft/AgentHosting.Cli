using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class PublicAmsWorkflowTests
{
    private const string Tenant = EnvironmentFixtures.ContosoTenant;
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Repository = "55555555-5555-5555-5555-555555555555";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Entra = "88888888-8888-8888-8888-888888888888";
    private const string UpdatedRevision = "99999999-9999-9999-9999-999999999999";

    [Theory]
    [InlineData(
        PowerPlatformApiRing.Production,
        false,
        "https://api.powerplatform.com/",
        "https://222222222222222222222222222222.22.environment.api.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Preprod,
        false,
        "https://api.preprod.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.preprod.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Test,
        false,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Production,
        true,
        "https://api.powerplatform.com/",
        "https://222222222222222222222222222222.22.environment.api.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Preprod,
        true,
        "https://api.preprod.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.preprod.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Test,
        true,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/",
        false)]
    [InlineData(
        PowerPlatformApiRing.Test,
        false,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/",
        true)]
    [InlineData(
        PowerPlatformApiRing.Test,
        true,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/",
        true)]
    public async Task CreateUploadDeployUpdate_UsesNativeRoutingAndVerifiedEnvironmentCoordinates(
        PowerPlatformApiRing ring,
        bool useStoredGlobalEndpoint,
        string globalEndpoint,
        string environmentEndpoint,
        bool usePersonalDevOverrides)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        var profiles = InMemoryAuthProfileStore.SignedOut();
        await profiles.UpsertAsync(EnvironmentFixtures.Account(ring: ring), default);
        var inventoryBody = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { name = Home, properties = new { tenantId = Tenant } },
                new { name = Target, properties = new { tenantId = Tenant } },
            },
        });
        var inventoryHandler = new CapturingPackageHandler(_ =>
            FakeHttpResponse.Json(HttpStatusCode.OK, inventoryBody));
        using var inventoryHttp = new HttpClient(inventoryHandler);
        var inventory = new PowerPlatformApiEnvironmentClient(
            inventoryHttp,
            new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.EnvironmentsRead },
            target);
        var selections = new InMemoryEnvironmentSelectionStore();
        if (useStoredGlobalEndpoint)
        {
            await selections.SetActiveAsync(
                EnvironmentFixtures.Selection(Home, endpoint: globalEndpoint) with
                {
                    ApiAudience = target.Audience,
                },
                default);
        }

        var environments = new EnvironmentSelectionService(
            inventory,
            selections,
            profiles,
            PowerPlatformCloudMap.ForTarget(target),
            new FixedClock(EnvironmentFixtures.SelectedAt),
            apiTarget: target);
        if (!useStoredGlobalEndpoint)
        {
            await environments.SelectAsync(Home, default);
        }

        byte[] packageBytes = [1, 2, 3, 4, 5];
        var digest = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var agentBody = JsonSerializer.Serialize(new
        {
            id = Agent,
            tenantId = Tenant,
            environmentId = Home,
            displayName = "public-proof",
            repositoryId = Repository,
            repositoryBindingStatus = "Bound",
            agentType = "CodeAgent",
            language = "DotNet",
            languageVersion = "10",
        });
        var packageBody = JsonSerializer.Serialize(new
        {
            operationId = Repository,
            agentId = Agent,
            sha256 = digest,
            sizeInBytes = packageBytes.Length,
            status = "Succeeded",
            packageRevisionId = Revision,
            createdTime = DateTimeOffset.UnixEpoch,
            changedTime = DateTimeOffset.UnixEpoch,
        });
        string DeploymentBody(string revision) => JsonSerializer.Serialize(new
        {
            id = Deployment,
            tenantId = Tenant,
            homeEnvironmentId = Home,
            environmentId = Target,
            agentId = Agent,
            packageRevisionId = revision,
            packageSha256 = digest,
            packageSizeInBytes = packageBytes.Length,
            agentType = "CodeAgent",
            deploymentType = "Dev",
            deploymentName = "public-proof",
            entraAppId = Entra,
            entraAppIdType = "AppReg",
            language = "DotNet",
            languageVersion = "10",
            endpoint = "https://runtime.example/api/messages",
            isCustomEndpoint = false,
            isQuarantined = false,
        });

        static string Resource(Uri uri) => uri.Segments[^1].TrimEnd('/');

        var currentRevision = Revision;
        var handler = new CapturingPackageHandler(request =>
        {
            var resource = Resource(request.RequestUri!);
            if (request.Method == HttpMethod.Patch && resource == Deployment)
            {
                currentRevision = UpdatedRevision;
            }

            var body = (request.Method.Method, resource) switch
            {
                ("POST", "agents") or ("GET", Agent) => agentBody,
                ("POST", "packages") => packageBody,
                ("POST", "deployments") or ("GET", Deployment) or ("PATCH", Deployment) =>
                    DeploymentBody(currentRevision),
                _ => throw new InvalidOperationException(
                    $"Unexpected workflow operation: {request.Method} {request.RequestUri!.AbsolutePath}."),
            };
            return FakeHttpResponse.Json(HttpStatusCode.OK, body);
        });
        using var http = new HttpClient(handler);
        var tokens = new FakePpapiTokenProvider
        {
            Scopes = string.Join(' ',
                PowerPlatformApiPermissions.AppsCreate,
                PowerPlatformApiPermissions.AppsRead,
                PowerPlatformApiPermissions.AppsUpdate),
        };
        var services = new ServiceCollection();
        Program.AddAmsServices(
            services,
            target,
            setting => usePersonalDevOverrides ? setting switch
            {
                "AH_CLI_AMS_ENDPOINT" => NativeAmsRoutingTests.ControlPlane,
                "AH_CLI_AMSDP_ENDPOINT" => NativeAmsRoutingTests.DataPlane,
                _ => null,
            } : null,
            () => http);
        services.AddAgentHostAuthentication(new AgentHostAuthOptions
        {
            ConfigDirectory = AppContext.BaseDirectory,
            Cloud = target.Cloud,
            InteractionAllowed = false,
        }, target);
        services.AddSingleton<ITokenProvider>(tokens);
        services.AddSingleton(environments);
        using var provider = services.BuildServiceProvider();
        var create = provider.GetRequiredService<AgentCreateService>();
        var upload = provider.GetRequiredService<AgentPackageUploadService>();
        var deploy = provider.GetRequiredService<AgentDeployService>();
        var packagePath = Path.Combine(AppContext.BaseDirectory, $"ams-routing-{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var created = await create.CreateAsync(new AgentCreateRequest
            {
                DisplayName = "public-proof",
                Language = "DotNet",
                LanguageVersion = "10",
            }, default);
            var uploaded = await upload.UploadAsync(new AgentPackageUploadRequest
            {
                AgentId = created.AgentId,
                PackagePath = packagePath,
            }, default);
            var deployed = await deploy.DeployAsync(new AgentDeployRequest
            {
                AgentId = created.AgentId,
                PackageRevisionId = uploaded.PackageRevisionId,
                TargetEnvironmentId = Target,
                DeploymentName = "public-proof",
                DeploymentType = "Dev",
                EntraId = Entra,
                EntraIdType = "AppReg",
            }, default);
            var updated = await deploy.UpdateAsync(new AgentDeploymentPackageUpdateRequest
            {
                AgentId = created.AgentId,
                DeploymentId = deployed.DeploymentId,
                PackageRevisionId = UpdatedRevision,
            }, default);

            Assert.Equal(UpdatedRevision, updated.PackageRevisionId);
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Post && Resource(request.Uri) == "agents");
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Post && Resource(request.Uri) == "packages");
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Post && Resource(request.Uri) == "deployments");
            Assert.Contains(handler.Requests, request =>
                request.Method == HttpMethod.Patch && Resource(request.Uri) == Deployment);

            var environmentOrigin = new Uri(environmentEndpoint).GetLeftPart(UriPartial.Authority);
            var globalOrigin = new Uri(globalEndpoint).GetLeftPart(UriPartial.Authority);
            Assert.Equal(7, handler.Requests.Count);
            Assert.All(handler.Requests, request =>
            {
                var isDeployment = Resource(request.Uri) is "deployments" or Deployment;
                var expectedOrigin = (usePersonalDevOverrides, isDeployment) switch
                {
                    (true, true) => NativeAmsRoutingTests.ControlPlane,
                    (true, false) => NativeAmsRoutingTests.DataPlane,
                    (false, true) => globalOrigin,
                    (false, false) => environmentOrigin,
                };
                var prefix = usePersonalDevOverrides
                    ? $"/agenthosting/tenants/{Tenant}/environments/{Home}/"
                    : isDeployment ? $"/agenthosting/environments/{Home}/" : "/agenthosting/";
                var suffix = (request.Method.Method, Resource(request.Uri)) switch
                {
                    ("POST", "agents") => "agents",
                    ("GET", Agent) => $"agents/{Agent}",
                    ("POST", "packages") => $"agents/{Agent}/packages",
                    ("POST", "deployments") => $"agents/{Agent}/deployments",
                    ("GET", Deployment) or ("PATCH", Deployment) =>
                        $"agents/{Agent}/deployments/{Deployment}",
                    _ => throw new InvalidOperationException("Unexpected AMS wire operation."),
                };
                Assert.Equal(expectedOrigin, request.Uri.GetLeftPart(UriPartial.Authority));
                Assert.Equal(prefix + suffix, request.Uri.AbsolutePath);
                Assert.Equal("?api-version=1", request.Uri.Query);
                Assert.True(request.HasAuthorization);
                Assert.True(Guid.TryParse(request.ClientRequestId, out _));
                if (isDeployment && request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(request.Body);
                    Assert.Equal(Target, body.RootElement.GetProperty("environmentId").GetString());
                }
            });
            Assert.All(tokens.Resources, audience => Assert.Equal(target.Audience, audience));
            Assert.All(tokens.Tenants, tenant => Assert.Equal(Tenant, tenant));
            Assert.All(inventoryHandler.Requests, request =>
                Assert.Equal(globalOrigin, request.Uri.GetLeftPart(UriPartial.Authority)));
            var selected = await selections.GetActiveAsync(default);
            Assert.NotNull(selected);
            Assert.Equal(Tenant, selected.TenantId);
            Assert.Equal(Home, selected.EnvironmentId);
            Assert.Equal(target.Audience, selected.ApiAudience);
            Assert.Equal(
                useStoredGlobalEndpoint ? globalOrigin : environmentOrigin,
                selected.ApiEndpoint.GetLeftPart(UriPartial.Authority));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }
}
