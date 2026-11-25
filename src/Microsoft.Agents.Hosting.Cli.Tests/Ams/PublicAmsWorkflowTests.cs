using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

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

    [Theory]
    [InlineData(
        PowerPlatformApiRing.Production,
        false,
        "https://api.powerplatform.com/",
        "https://222222222222222222222222222222.22.environment.api.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Preprod,
        false,
        "https://api.preprod.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.preprod.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Test,
        false,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Production,
        true,
        "https://api.powerplatform.com/",
        "https://222222222222222222222222222222.22.environment.api.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Preprod,
        true,
        "https://api.preprod.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.preprod.powerplatform.com/")]
    [InlineData(
        PowerPlatformApiRing.Test,
        true,
        "https://api.test.powerplatform.com/",
        "https://2222222222222222222222222222222.2.environment.api.test.powerplatform.com/")]
    public async Task CreateUploadCreateDeploymentDeployRename_ResolvesEnvironmentHostWithoutAdvertisedEndpoints(
        PowerPlatformApiRing ring,
        bool useStoredGlobalEndpoint,
        string globalEndpoint,
        string environmentEndpoint)
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
            var profile = (await profiles.GetActiveProfileAsync(default))!;
            var committed = await profiles.SetDefaultEnvironmentAsync(
                profile.ProfileId, profile.Revision,
                EnvironmentFixtures.Selection(Home, endpoint: globalEndpoint) with { ApiAudience = target.Audience },
                default);
            await selections.SetActiveAsync(
                committed.DefaultEnvironment!,
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
        string DeploymentBody(string state, string deploymentName) => JsonSerializer.Serialize(new
        {
            id = Deployment,
            tenantId = Tenant,
            homeEnvironmentId = Home,
            environmentId = Target,
            agentId = Agent,
            eTag = $"\"{state}\"",
            state,
            packageRevisionId = state == "Deployed" ? Revision : null,
            packageSha256 = state == "Deployed" ? digest : null,
            packageSizeInBytes = state == "Deployed" ? packageBytes.Length : (int?)null,
            agentType = "CodeAgent",
            deploymentType = "Dev",
            deploymentName,
            entraAppId = Entra,
            entraAppIdType = "AppReg",
            language = "DotNet",
            languageVersion = "10",
            endpoint = state == "Deployed" ? "https://runtime.example/api/messages" : null,
            isCustomEndpoint = false,
            isQuarantined = false,
        });

        static string Resource(Uri uri) => uri.Segments[^1].TrimEnd('/');

        var handler = new CapturingPackageHandler(request =>
        {
            var body = (request.Method.Method, Resource(request.RequestUri!)) switch
            {
                ("POST", "agents") or ("GET", Agent) => agentBody,
                ("GET", "agents") => $$"""{ "value": [{{agentBody}}] }""",
                ("POST", "packages") => packageBody,
                ("POST", "deployments") => DeploymentBody("NotDeployed", "public-proof"),
                ("GET", "deployments") => $$"""{ "value": [{{DeploymentBody("NotDeployed", "public-proof")}}], "keptBots": [] }""",
                ("POST", "deploy") or ("GET", Deployment) => DeploymentBody("Deployed", "public-proof"),
                ("PATCH", Deployment) => DeploymentBody("Deployed", "public-renamed"),
                _ => throw new InvalidOperationException($"Unexpected workflow operation: {request.Method} {request.RequestUri!.AbsolutePath}."),
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
        var agentAdapter = new AmsAgentAdapter(http, tokens);
        var deploymentAdapter = new AmsDeploymentAdapter(http, tokens);
        var targets = new AmsTargets(new AmsEndpointProfile(), target);
        var addresses = new AmsAddressResolver(targets, environments, agentAdapter, deploymentAdapter);
        var create = new AgentCreateService(targets, environments, new AmsAgentCreationAdapter(http, tokens));
        var upload = new AgentPackageUploadService(addresses, agentAdapter, new AmsPackagePublicationAdapter(http, tokens));
        var deployments = new AmsDeploymentService(addresses, agentAdapter, deploymentAdapter, new ScriptedConfirmationPrompt());
        var deploy = new AgentDeployService(addresses, deploymentAdapter);
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var created = await create.CreateAsync(new AgentCreateRequest { DisplayName = "public-proof", Language = "DotNet", LanguageVersion = "10" }, default);
            await upload.UploadAsync(new AgentPackageUploadRequest { AgentId = created.AgentId, PackagePath = packagePath }, default);
            var record = await deployments.CreateAsync(new AgentDeploymentAmsCreateRequest { AgentId = created.AgentId, TargetEnvironmentId = Target, DeploymentName = "public-proof", DeploymentType = "dev", EntraId = Entra, EntraIdType = "app-reg" }, default);
            var deployed = await deploy.DeployAsync(new AgentDeployRequest { AgentName = "public-proof", DeploymentName = "public-proof" }, default);
            var renamed = await deployments.UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = created.AgentId, DeploymentId = record.Deployment.DeploymentId, NewName = "public-renamed" }, default);

            Assert.Equal("NotDeployed", record.Deployment.State);
            Assert.Equal(Revision, deployed.Deployment.PackageRevisionId);
            Assert.Equal("public-renamed", renamed.Deployment.DeploymentName);
            Assert.Equal(
                ["POST agents", "GET " + Agent, "POST packages", "GET " + Agent, "POST deployments", "GET agents", "GET deployments", "POST deploy", "GET " + Deployment, "PATCH " + Deployment],
                handler.Requests.Select(request => $"{request.Method.Method} {Resource(request.Uri)}"));
            Assert.Equal(["\"NotDeployed\"", "\"Deployed\""], handler.Requests.Where(request => request.IfMatch is not null).Select(request => request.IfMatch));
            var environmentOrigin = new Uri(environmentEndpoint).GetLeftPart(UriPartial.Authority);
            var globalOrigin = new Uri(globalEndpoint).GetLeftPart(UriPartial.Authority);
            Assert.All(handler.Requests, request =>
            {
                var expectedOrigin = Resource(request.Uri) is "deployments" or Deployment or "deploy"
                    ? globalOrigin
                    : environmentOrigin;
                Assert.Equal(expectedOrigin, request.Uri.GetLeftPart(UriPartial.Authority));
                Assert.True(request.HasAuthorization);
                Assert.True(Guid.TryParse(request.ClientRequestId, out _));
            });
            Assert.All(tokens.Resources, audience => Assert.Equal(target.Audience, audience));
            Assert.All(tokens.Tenants, tenant => Assert.Equal(Tenant, tenant));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }
}
