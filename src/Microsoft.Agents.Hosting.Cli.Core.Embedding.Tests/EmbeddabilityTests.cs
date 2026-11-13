using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Packaging;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests;

/// <summary>
/// Proves that a future service host can construct and drive <see cref="AgentHostingOperations"/>
/// directly — no command host, no <c>System.CommandLine</c>, no MSAL adapter, and no console — and
/// that the Core assembly references none of the identity or Azure libraries. This assembly references
/// <c>Microsoft.Agents.Hosting.Cli.Core</c> only; the tests below could not compile against a host or MSAL type even
/// if they tried.
/// </summary>
public sealed class EmbeddabilityTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Subscription = "22222222-2222-2222-2222-222222222222";
    private const string MsaAppId = "33333333-3333-3333-3333-333333333333";

    private static readonly AuthSessionContext Context = new()
    {
        ClientId = "44444444-4444-4444-4444-444444444444",
        Cloud = CloudInstance.Public,
        Authority = "https://login.microsoftonline.com/organizations",
    };

    private static AgentHostingOperations CreateEmbeddedOperations(
        EmbeddedAuthenticationSession session,
        EmbeddedAuthProfileStore store,
        SilentMessagePresenter presenter,
        int constructorShape = 0)
    {
        var reader = new EmbeddedAbsStateReader();
        var environments = new EnvironmentSelectionService(
            new EmptyEnvironmentClient(),
            new EmbeddedEnvironmentSelectionStore(),
            store,
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public),
            new EmbeddedClock());

        // The apply seam is wired with the pending mutation port: the preview path must never resolve or
        // call it, so any accidental mutation from the read-only path would throw here.
        var platform = new RuntimePlatformInfo();
        var provisioning = new AbsProvisioningService(reader);
        var apply = new AbsApplyService(reader, new PendingAbsMutationPort());
        var create = new AgentCreateService(new AgentCreationTarget(), environments, new PendingAmsAgentCreationTransport());
        var upload = new AgentPackageUploadService(environments, new PendingAmsAgentReadTransport(), new PendingAmsPackagePublicationTransport());
        var deploy = new AgentDeployService(PowerPlatformApiTargetConfig.Public, environments,
            new PendingAmsAgentReadTransport(), new PendingAmsDeploymentTransport());
        return constructorShape switch
        {
            0 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy),
            1 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null),
            2 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null),
            3 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null, null),
            4 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null, null, null),
            5 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null, null, null, null),
            6 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null, null, null, null, null),
            7 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, null, null, null, null, null, null, null),
            8 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy, blueprintIdentity: null),
            9 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy,
                deploymentChannels: null, managedAgents: null, appRegistrationIdentity: null),
            10 => new(session, store, StaticInteractionPolicy.NonInteractive, platform, presenter, Context,
                provisioning, apply, environments, create, upload, deploy,
                evaluations: null, deploymentChannels: null, deploymentOAuth: null, deployments: null,
                managedAgents: null, appRegistrationIdentity: null, blueprintIdentity: null),
            _ => throw new ArgumentOutOfRangeException(nameof(constructorShape)),
        };
    }

    private static AbsProvisioningRequest CompletePreviewRequest() => new()
    {
        Tenant = Tenant,
        Subscription = Subscription,
        ResourceGroup = "rg-embedding",
        Location = "westus",
        BotName = "embedding-bot",
        Endpoint = "https://gateway.contoso.com/api/messages",
        MsaAppId = MsaAppId,
    };

    [Fact]
    public async Task EmbeddedLegacyConstructorsAcceptNullLiteralsAndNamedArguments()
    {
        var session = new EmbeddedAuthenticationSession();
        var presenter = new SilentMessagePresenter();
        for (int shape = 0; shape <= 10; shape++)
        {
            var operations = CreateEmbeddedOperations(session, new EmbeddedAuthProfileStore(), presenter, shape);
            Assert.Equal("signed-out", (await operations.GetAuthStatusAsync(default)).Status);
        }
        Assert.Equal(0, session.LoginFlowCalls);
        Assert.Equal(0, presenter.PresentCalls);
    }

    [Fact]
    public void EmbeddableCore_ReferencesNoHostOrIdentityAssembly()
    {
        // "Graph" is spelled out as "Microsoft.Graph": the token being excluded is the Microsoft Graph
        // client, and a bare "Graph" also matches System.Security.Cryptography, which Core references
        // for the package digest. Every Graph SDK assembly is still covered — they are all
        // Microsoft.Graph or Microsoft.Graph.*.
        var forbiddenTokens = new[] { "Msal", "Identity.Client", "Azure", "Microsoft.Graph", "WAM", "System.CommandLine" };
        var forbiddenExactNames = new[] { "Microsoft.Agents.Hosting.Cli", "Microsoft.Agents.Hosting.Cli.Auth" };

        var referencedNames = typeof(AgentHostingOperations).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (var name in referencedNames)
        {
            Assert.DoesNotContain(forbiddenTokens, token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(forbiddenExactNames, exact => string.Equals(name, exact, StringComparison.Ordinal));
        }

        // The command host and MSAL adapter must not even be loaded into this process by exercising Core.
        var loadedNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .ToArray();

        foreach (var exact in forbiddenExactNames)
        {
            Assert.DoesNotContain(exact, loadedNames);
        }
    }

    [Fact]
    public async Task EmbeddedAuthStatus_ReportsSignedOut_WithoutOpeningAnyLoginFlow()
    {
        var session = new EmbeddedAuthenticationSession();
        var presenter = new SilentMessagePresenter();
        var operations = CreateEmbeddedOperations(session, new EmbeddedAuthProfileStore(), presenter);

        var status = await operations.GetAuthStatusAsync(CancellationToken.None);

        Assert.Equal("signed-out", status.Status);
        Assert.False(status.SignedIn);
        Assert.Null(status.Account);
        Assert.Equal(0, session.LoginFlowCalls);
        Assert.Equal(0, presenter.PresentCalls);
    }

    [Fact]
    public async Task EmbeddedDacUserProfile_DoesNotInventMsalHomeAccountId()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"msagent-embedding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var store = new FileAuthProfileStore(path);
            var created = await store.CreateAsync(new AuthProfile
            {
                AccountKey = "55555555-5555-5555-5555-555555555555",
                Username = "developer@contoso.com",
                TenantId = Tenant,
                ClientId = Context.ClientId,
                Authority = Context.Authority,
                CredentialMethod = AuthCredentialMethod.DefaultAzureCredential,
                PrincipalType = AuthPrincipalType.User,
            }, CancellationToken.None);

            Assert.Null(created.HomeAccountId);
            var persisted = Assert.Single(
                await new FileAuthProfileStore(path).GetProfilesAsync(CancellationToken.None));
            Assert.Null(persisted.HomeAccountId);
            Assert.Equal(created.AccountKey, persisted.AccountKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmbeddedProfileDefault_CarriesCapturedDacProfileWithoutConstructingCredentialClient()
    {
        var directory = Path.Combine(Path.GetTempPath(), $".test-embedding-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FileAuthProfileStore(Path.Combine(directory, "profiles.json"));
            var created = await store.CreateAsync(new AuthProfile
            {
                AccountKey = "55555555-5555-5555-5555-555555555555",
                Username = "developer@contoso.com",
                TenantId = Tenant,
                ClientId = Context.ClientId,
                Authority = Context.Authority,
                CredentialMethod = AuthCredentialMethod.DefaultAzureCredential,
                DefaultEnvironment = new ActiveEnvironment
                {
                    EnvironmentId = "env-embedding",
                    DisplayName = "Embedded",
                    TenantId = Tenant,
                    ApiEndpoint = new Uri("https://embedded.api.powerplatform.com"),
                    ApiAudience = PowerPlatformApiTargetConfig.Public.Audience,
                },
            }, default);
            var factory = new UnusedProfileEnvironmentClientFactory();
            var environments = new EnvironmentSelectionService(factory, new EmbeddedEnvironmentSelectionStore(), store, new EmbeddedClock());
            var binding = await environments.GetActiveBindingAsync(created, default);
            var resolved = await environments.ResolveForCommandAsync(null, default);

            Assert.True(binding?.IsBound);
            Assert.Equal(created.ProfileId, resolved.Context?.Profile.ProfileId);
            Assert.Null(resolved.Context?.Profile.HomeAccountId);
            Assert.Equal("env-embedding", resolved.EnvironmentId);
            Assert.Equal(0, factory.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class UnusedProfileEnvironmentClientFactory : IProfileEnvironmentClientFactory
    {
        public int Calls { get; private set; }
        public IEnvironmentClient CreateClient(EnvironmentResolutionContext context)
        {
            Calls++;
            throw new InvalidOperationException("Metadata and profile defaults must not construct credential clients.");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task EmbeddedDeploy_RequiresExplicitTargetWithoutOpeningAnyLoginFlow(string? targetEnvironmentId)
    {
        var session = new EmbeddedAuthenticationSession();
        var presenter = new SilentMessagePresenter();
        var operations = CreateEmbeddedOperations(session, new EmbeddedAuthProfileStore(), presenter);
        var request = new AgentDeployRequest
        {
            AgentId = "55555555-5555-5555-5555-555555555555",
            PackageRevisionId = "66666666-6666-6666-6666-666666666666",
            TargetEnvironmentId = targetEnvironmentId!,
            DeploymentName = "dev-proof",
            DeploymentType = "dev",
            EntraId = MsaAppId,
            EntraIdType = "app-reg",
        };

        var exception = await Assert.ThrowsAsync<UsageException>(() =>
            operations.DeployAgentAsync(request, CancellationToken.None));

        Assert.Contains("--target-environment-id", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, session.LoginFlowCalls);
        Assert.Equal(0, presenter.PresentCalls);
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("Failed")]
    public void EmbeddedUploadFailure_ExposesTypedOperationIdentityWithoutTheCommandHost(string status)
    {
        const string operationId = "77777777-7777-7777-7777-777777777777";
        var exception = PackagePublicationException.DurableOperationDidNotSucceed(
            operationId,
            status,
            "client-request-id",
            serviceRequestId: null);

        var partial = Assert.IsType<AgentPackageUploadPartialResult>(exception.PartialResult);
        Assert.Equal(operationId, partial.PackageOperationId);
        Assert.Equal(status, partial.OperationStatus);
    }

    [Fact]
    public async Task EmbeddedPreview_ProducesPlannedZeroGraphReport_WithoutMutating()
    {
        var session = new EmbeddedAuthenticationSession();
        var presenter = new SilentMessagePresenter();
        var operations = CreateEmbeddedOperations(session, new EmbeddedAuthProfileStore(), presenter);

        var report = await operations.PreviewAzureBotAsync(CompletePreviewRequest(), CancellationToken.None);

        Assert.Equal("planned", report.Status);
        Assert.False(report.Identity.SecretMaterialReturned);
        Assert.Equal("displayNameFilter", report.Correlation.Graph.Mode);
        Assert.NotEmpty(report.Changes);

        // The embedded read-only path opened no identity UI and mutated nothing (the pending mutation
        // port would have thrown had preview reached it).
        Assert.Equal(0, session.LoginFlowCalls);
        Assert.Equal(0, presenter.PresentCalls);
    }
}
