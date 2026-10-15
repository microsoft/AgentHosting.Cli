using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

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
        "https://api.powerplatform.com/",
        "https://home.environment.api.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Preprod,
        "https://api.preprod.powerplatform.com/",
        "https://home.environment.api.preprod.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Test,
        "https://api.test.powerplatform.com/",
        "https://home.environment.api.test.powerplatform.com/")]
    public async Task CreateUploadDeployUpdate_UsesPublicRoutesWithoutEndpointOverrides(
        PowerPlatformApiRing ring,
        string globalEndpoint,
        string environmentEndpoint)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(ring);
        var profiles = InMemoryAuthProfileStore.SignedOut();
        await profiles.UpsertAsync(EnvironmentFixtures.Account(ring: ring), default);
        var home = EnvironmentFixtures.Environment(Home, endpoint: environmentEndpoint) with
        {
            ApiAudience = target.Audience,
        };
        var targetEnvironment = home with
        {
            EnvironmentId = Target,
            ApiEndpoint = new Uri($"https://target.environment.{target.GlobalEndpoint.Host}/"),
        };
        var environments = new EnvironmentSelectionService(
            new FakeEnvironmentClient(home, targetEnvironment),
            new InMemoryEnvironmentSelectionStore(),
            profiles,
            PowerPlatformCloudMap.ForTarget(target),
            new FixedClock(EnvironmentFixtures.SelectedAt),
            apiTarget: target);
        await environments.SelectAsync(Home, default);

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

        var agentUrl = $"{environmentEndpoint}agenthosting/agents/{Agent}?api-version=1";
        var deploymentUrl =
            $"{globalEndpoint}agenthosting/environments/{Home}/agents/{Agent}/deployments";
        (HttpMethod Method, string Url, string Body)[] expected =
        [
            (HttpMethod.Post, $"{environmentEndpoint}agenthosting/agents?api-version=1", agentBody),
            (HttpMethod.Get, agentUrl, agentBody),
            (HttpMethod.Post, $"{environmentEndpoint}agenthosting/agents/{Agent}/packages?api-version=1", packageBody),
            (HttpMethod.Get, agentUrl, agentBody),
            (HttpMethod.Post, $"{deploymentUrl}?api-version=1", DeploymentBody(Revision)),
            (HttpMethod.Get, $"{deploymentUrl}/{Deployment}?api-version=1", DeploymentBody(Revision)),
            (HttpMethod.Patch, $"{deploymentUrl}/{Deployment}?api-version=1", DeploymentBody(UpdatedRevision)),
        ];
        var requestIndex = 0;
        var handler = new CapturingPackageHandler(request =>
        {
            Assert.True(requestIndex < expected.Length, "An unexpected additional request was sent.");
            var next = expected[requestIndex++];
            Assert.Equal(next.Method, request.Method);
            Assert.Equal(next.Url, request.RequestUri!.AbsoluteUri);
            return FakeHttpResponse.Json(HttpStatusCode.OK, next.Body);
        });
        using var http = new HttpClient(handler);
        var tokens = new FakePpapiTokenProvider
        {
            Scopes = string.Join(' ',
                PowerPlatformApiPermissions.AppsCreate,
                PowerPlatformApiPermissions.AppsRead,
                PowerPlatformApiPermissions.AppsUpdate),
        };
        var agentAdapter = new AmsAgentCreationAdapter(http, tokens);
        var deploymentAdapter = new AmsDeploymentAdapter(http, tokens);
        var create = new AgentCreateService(new AgentCreationTarget(), environments, agentAdapter);
        var upload = new AgentPackageUploadService(
            environments,
            deploymentAdapter,
            new AmsPackagePublicationAdapter(http, tokens));
        var deploy = new AgentDeployService(target, environments, deploymentAdapter, deploymentAdapter);
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
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
            Assert.Equal(expected.Length, requestIndex);
            Assert.Equal(packageBytes, handler.Requests[2].Body);
            Assert.All(handler.Requests, request =>
            {
                Assert.True(request.HasAuthorization);
                Assert.True(Guid.TryParse(request.ClientRequestId, out _));
            });
            Assert.All(tokens.Resources, audience => Assert.Equal(target.Audience, audience));
            Assert.All(tokens.Tenants, tenant => Assert.Equal(Tenant, tenant));
            using var patch = JsonDocument.Parse(handler.Requests[^1].Body);
            Assert.Equal(Revision, patch.RootElement.GetProperty("expectedPackageRevisionId").GetString());
            Assert.Equal(UpdatedRevision, patch.RootElement.GetProperty("packageRevisionId").GetString());
        }
        finally
        {
            File.Delete(packagePath);
        }
    }
}
