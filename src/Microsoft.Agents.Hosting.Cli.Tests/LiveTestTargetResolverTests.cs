using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class LiveTestTargetResolverTests
{
    private const string TenantGuid = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenantGuid = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void FullyExplicitTargetDoesNotReadConfiguration()
    {
        using var project = new TemporaryProject();
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var target = resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = TenantGuid,
            DeploymentId = "operator-label",
        });

        Assert.Equal(LiveTestTargetSource.Explicit, target.Source);
        Assert.Equal("Bot01", target.SchemaName);
        Assert.Equal("A47151CF-4F34-488F-B377-EBE84E17B478", target.EnvironmentId);
        Assert.Equal(TenantGuid, target.TenantId);
        Assert.Equal("operator-label", target.DeploymentId);
        Assert.Null(target.Agent);
        Assert.Null(target.Deployment);
        Assert.Equal(
            "a47151cf4f34488fb377ebe84e17b4.78.environment.api.powerplatform.com",
            target.Endpoint.Host);
        Assert.Equal(
            "/copilotstudio/dataverse-backed/authenticated/bots/Bot01/conversations",
            target.Endpoint.AbsolutePath);
        Assert.Equal("api-version=2022-03-01-preview", target.Endpoint.Query.TrimStart('?'));

        var targetByName = resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = TenantGuid,
            DeploymentName = "operator-label",
        });

        Assert.Equal(LiveTestTargetSource.Explicit, targetByName.Source);
        Assert.Equal("operator-label", targetByName.DeploymentName);
        Assert.Equal(0, agents.Reads);
    }

    [Fact]
    public void GitHubCopilotTypeSelectsAgenticRuntimeEndpointWithoutChangingEnvironmentHost()
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var target = resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = TenantGuid,
            DeploymentId = "operator-label",
            AgentType = LiveTestAgentType.GitHubCopilot,
        });

        Assert.Equal(LiveTestAgentType.GitHubCopilot, target.AgentType);
        Assert.Equal(
            "a47151cf4f34488fb377ebe84e17b4.78.environment.api.powerplatform.com",
            target.Endpoint.Host);
        Assert.Equal(
            "/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/Bot01/conversations",
            target.Endpoint.AbsolutePath);
        Assert.Equal("api-version=1", target.Endpoint.Query.TrimStart('?'));
        Assert.Equal(0, agents.Reads);
    }

    [Theory]
    [InlineData("harness")]
    [InlineData("")]
    public void UnknownAgentTypeIsRejectedBeforeConfigurationOrAuthentication(string agentType)
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var error = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            Tenant = TenantGuid,
            DeploymentId = "operator-label",
            AgentType = agentType,
        }));

        Assert.Equal("invalid-input", error.ErrorKind);
        Assert.Contains("--agent-harness", error.Message);
        Assert.Contains("--agent-harness", error.Remediation);
        Assert.Equal(0, agents.Reads);
    }

    [Fact]
    public void UndefinedAgentHarnessValueUsesCommandOptionNameInValidationError()
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var error = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = TenantGuid,
            DeploymentId = "operator-label",
            AgentType = (LiveTestAgentType)int.MaxValue,
        }));

        Assert.Equal("invalid-input", error.ErrorKind);
        Assert.Contains("--agent-harness", error.Message);
        Assert.Contains("--agent-harness", error.Remediation);
        Assert.Equal(0, agents.Reads);
    }

    [Fact]
    public void OperationRequestMapsTenantAndTargetSource()
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var target = resolver.Resolve(new LiveTestRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            Tenant = TenantGuid,
            DeploymentId = "operator-label",
        });

        Assert.Equal("A47151CF-4F34-488F-B377-EBE84E17B478", target.EnvironmentId);
        Assert.Equal(TenantGuid, target.TenantId);
        Assert.Equal("Bot01", target.SchemaName);
        Assert.Equal("explicit", target.TargetSource);
        Assert.Equal(0, agents.Reads);
    }

    [Fact]
    public void InvalidTenantIsRejectedBeforeAuthenticationWithoutEchoingValue()
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var explicitFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = "tenant-1",
            DeploymentId = "operator-label",
        }));

        Assert.Equal("invalid-input", explicitFailure.ErrorKind);
        Assert.DoesNotContain("tenant-1", explicitFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, agents.Reads);

        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent() with
        {
            TenantId = "tenant-1",
            McsSchemaName = "schema-one",
        });
        store.Add(project.Root, StoreFixtures.Deployment() with
        {
            TenantId = "tenant-1",
            IsDefault = true,
        });

        var configuredFailure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            DeploymentId = "slot-1",
        }));

        Assert.Equal("invalid-configuration", configuredFailure.ErrorKind);
        Assert.DoesNotContain("tenant-1", configuredFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfiguredTargetUsesSelectedDeploymentEnvironmentAndDefaultRules()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent(environmentId: "home-environment") with
        {
            McsSchemaName = "schema-one",
        });
        store.Add(project.Root, Deployment("slot-default", environmentId: "target-environment") with
        {
            HomeEnvironmentId = "home-environment",
            IsDefault = true,
        });
        store.Add(project.Root, Deployment("slot-other", name: "other", stage: DeploymentStages.Test, environmentId: "other-environment") with
        {
            HomeEnvironmentId = "home-environment",
        });

        var target = new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            EnvironmentId = "target-environment",
            TenantId = TenantGuid,
        });

        Assert.Equal(LiveTestTargetSource.Configured, target.Source);
        Assert.Equal("target-environment", target.EnvironmentId);
        Assert.Equal(TenantGuid, target.TenantId);
        Assert.Equal("schema-one", target.SchemaName);
        Assert.Equal("slot-default", target.Deployment!.DeploymentId);
        Assert.Equal("home-environment", target.Agent!.EnvironmentId);
    }

    [Fact]
    public void ConfiguredTargetCanSelectByAgentNameAndDeploymentName()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Deployment("slot-test", name: "test", stage: DeploymentStages.Test, environmentId: "target-test"));

        var target = new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentName = "Contoso Helpdesk",
            DeploymentName = "test",
        });

        Assert.Equal(LiveTestTargetSource.Configured, target.Source);
        Assert.Equal("target-test", target.EnvironmentId);
        Assert.Equal("schema-one", target.SchemaName);
        Assert.Equal("slot-test", target.Deployment!.DeploymentId);
    }

    [Fact]
    public void ConfiguredTargetCanSelectByUniqueSchemaName()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Agent("agent-2", "Other Agent", "home-two") with
        {
            AgentLocation = "agent-2",
            McsSchemaName = "schema-two",
        });
        store.Add(project.Root, Deployment("slot-one", environmentId: "target-one") with
        {
            IsDefault = true,
        });
        store.Add(project.Root, Deployment("slot-two", "agent-2", environmentId: "target-two") with
        {
            HomeEnvironmentId = "home-two",
            IsDefault = true,
        });

        var target = new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            SchemaName = "schema-two",
        });

        Assert.Equal(LiveTestTargetSource.Configured, target.Source);
        Assert.Equal("agent-2", target.Agent!.AgentId);
        Assert.Equal("target-two", target.EnvironmentId);
        Assert.Equal("schema-two", target.SchemaName);
    }

    [Fact]
    public void ConfiguredTargetRejectsMultipleDefaultDeployments()
    {
        using var project = new TemporaryProject();
        var seedStore = StoreFixtures.Create();
        var agent = seedStore.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        var deployments = new[]
        {
            Deployment("slot-one", environmentId: "target-one") with { IsDefault = true },
            Deployment("slot-two", name: "test", stage: DeploymentStages.Test, environmentId: "target-two") with { IsDefault = true },
        };
        var registry = new FixedRegistry([agent], deployments);

        var failure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(registry, registry, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
        }));

        Assert.Equal("invalid-configuration", failure.ErrorKind);
        Assert.Contains("more than one", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfiguredTargetTreatsEnvironmentAndTenantAsAssertions()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Deployment(environmentId: "target-environment") with
        {
            IsDefault = true,
        });
        var resolver = new LiveTestTargetResolver(store, store, new AgentConfigLocator());

        var environmentFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            EnvironmentId = "another-environment",
        }));
        var tenantFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            TenantId = OtherTenantGuid,
        }));

        Assert.Equal("invalid-configuration", environmentFailure.ErrorKind);
        Assert.Equal("invalid-configuration", tenantFailure.ErrorKind);
    }

    [Fact]
    public void ConfiguredTargetRejectsDisabledOrQuarantinedDeployments()
    {
        using var disabledProject = new TemporaryProject();
        var disabledStore = StoreFixtures.Create();
        disabledStore.Add(disabledProject.Root, Agent() with { McsSchemaName = "schema-one" });
        disabledStore.Add(disabledProject.Root, Deployment() with { IsEnabled = false });

        using var quarantinedProject = new TemporaryProject();
        var quarantinedStore = StoreFixtures.Create();
        quarantinedStore.Add(quarantinedProject.Root, Agent() with { McsSchemaName = "schema-one" });
        quarantinedStore.Add(quarantinedProject.Root, Deployment() with { IsQuarantined = true });

        var disabledFailure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(disabledStore, disabledStore, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = disabledProject.Root,
            DeploymentId = "slot-1",
        }));
        var quarantinedFailure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(quarantinedStore, quarantinedStore, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = quarantinedProject.Root,
            DeploymentId = "slot-1",
        }));

        Assert.Equal("invalid-configuration", disabledFailure.ErrorKind);
        Assert.Equal("invalid-configuration", quarantinedFailure.ErrorKind);
    }

    [Fact]
    public void AgentIdWinsSelectionButConflictingNameAndSchemaFail()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Agent("agent-2", "Other Agent") with { McsSchemaName = "schema-two", AgentLocation = "agent-2" });
        store.Add(project.Root, Deployment("slot-1", environmentId: "target") with { IsDefault = true });
        store.Add(project.Root, Deployment("slot-2", agentId: "agent-2", name: "other", stage: DeploymentStages.Test, environmentId: "target-2") with { IsDefault = true });

        var resolver = new LiveTestTargetResolver(store, store, new AgentConfigLocator());

        var nameFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            AgentName = "Other Agent",
        }));
        Assert.Equal("invalid-configuration", nameFailure.ErrorKind);
        Assert.Equal(ExitCodes.Usage, nameFailure.ExitCode);

        var schemaFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            SchemaName = "schema-two",
        }));
        Assert.Equal("invalid-configuration", schemaFailure.ErrorKind);
    }

    [Fact]
    public void DeploymentIdWinsButConflictingNameAndOwnerFail()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Agent("agent-2", "Other Agent") with { McsSchemaName = "schema-two", AgentLocation = "agent-2" });
        store.Add(project.Root, Deployment("slot-1", environmentId: "target") with { IsDefault = true });
        store.Add(project.Root, Deployment("slot-2", agentId: "agent-2", name: "other", stage: DeploymentStages.Test, environmentId: "target-2") with { IsDefault = true });

        var resolver = new LiveTestTargetResolver(store, store, new AgentConfigLocator());

        var nameFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            DeploymentId = "slot-1",
            DeploymentName = "other",
        }));
        Assert.Equal("invalid-configuration", nameFailure.ErrorKind);

        var ownerFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-2",
            DeploymentId = "slot-1",
        }));
        Assert.Equal("invalid-configuration", ownerFailure.ErrorKind);

        var deploymentOnly = resolver.Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            DeploymentName = "other",
        });
        Assert.Equal("agent-2", deploymentOnly.Agent!.AgentId);
        Assert.Equal("slot-2", deploymentOnly.Deployment!.DeploymentId);
    }

    [Fact]
    public void MissingDefaultDoesNotFallBackToSingletonOrHomeEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent(environmentId: "home-environment") with { McsSchemaName = "schema-one" });
        store.Add(project.Root, Deployment(environmentId: "target-environment") with
        {
            HomeEnvironmentId = "home-environment",
        });

        var failure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
        }));

        Assert.Equal("invalid-configuration", failure.ErrorKind);
        Assert.Contains("default", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedCloudAndUnsafeLocatorFailBeforeRegistryAccess()
    {
        var agents = new ThrowingRegistry();
        var resolver = new LiveTestTargetResolver(agents, agents, new AgentConfigLocator());

        var cloudFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            Cloud = CloudInstance.UsGov,
            SchemaName = "Bot01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = "tenant-1",
            DeploymentName = "operator-label",
        }));
        Assert.Equal("unsupported-target", cloudFailure.ErrorKind);
        Assert.Equal(0, agents.Reads);

        var locatorFailure = Assert.Throws<LiveTestTargetResolutionException>(() => resolver.Resolve(new LiveTestTargetRequest
        {
            SchemaName = "Bot/01",
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
            TenantId = "tenant-1",
            DeploymentName = "operator-label",
        }));
        Assert.Equal("invalid-input", locatorFailure.ErrorKind);
        Assert.Equal(0, agents.Reads);
    }

    [Fact]
    public void CodeAgentTargetIsRejectedAsUnsupported()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, Agent() with { AgentType = AgentTypes.CodeAgent, McsSchemaName = "schema-one" });
        store.Add(project.Root, Deployment("slot-1", environmentId: string.Empty) with
        {
            AgentType = AgentTypes.CodeAgent,
            HomeEnvironmentId = null,
            IsDefault = true,
        });

        var failure = Assert.Throws<LiveTestTargetResolutionException>(() => new LiveTestTargetResolver(store, store, new AgentConfigLocator()).Resolve(new LiveTestTargetRequest
        {
            Project = project.Root,
            AgentId = "agent-1",
            DeploymentId = "slot-1",
        }));

        Assert.Equal("unsupported-target", failure.ErrorKind);
    }

    private static ManagedAgentRecord Agent(
        string agentId = "agent-1",
        string displayName = "Contoso Helpdesk",
        string environmentId = "env-home") =>
        StoreFixtures.Agent(agentId, displayName, environmentId) with { TenantId = TenantGuid };

    private static AgentDeploymentRecord Deployment(
        string deploymentId = "slot-1",
        string agentId = "agent-1",
        string name = "dev",
        string stage = DeploymentStages.Dev,
        string environmentId = "env-home") =>
        StoreFixtures.Deployment(deploymentId, agentId, name, stage, environmentId) with { TenantId = TenantGuid };

    private sealed class ThrowingRegistry : IManagedAgentRegistry, IAgentDeploymentRegistry
    {
        public int Reads { get; private set; }

        public IReadOnlyList<ManagedAgentRecord> List(string projectDirectory)
        {
            Reads++;
            throw new InvalidOperationException("configuration should not be read");
        }

        IReadOnlyList<AgentDeploymentRecord> IAgentDeploymentRegistry.List(string projectDirectory)
        {
            Reads++;
            throw new InvalidOperationException("configuration should not be read");
        }

        public ManagedAgentRecord Add(string projectDirectory, ManagedAgentRecord record) => throw new NotSupportedException();

        public ManagedAgentRecord Update(string projectDirectory, ManagedAgentRecord record) => throw new NotSupportedException();

        public int RemoveWithDeployments(string projectDirectory, string agentId, string expectedETag) => throw new NotSupportedException();

        AgentDeploymentRecord IAgentDeploymentRegistry.Add(string projectDirectory, AgentDeploymentRecord record) => throw new NotSupportedException();

        AgentDeploymentRecord IAgentDeploymentRegistry.Update(string projectDirectory, AgentDeploymentRecord record) => throw new NotSupportedException();

        void IAgentDeploymentRegistry.Remove(string projectDirectory, string deploymentId, string expectedETag) => throw new NotSupportedException();
    }

    private sealed class FixedRegistry(
        IReadOnlyList<ManagedAgentRecord> agents,
        IReadOnlyList<AgentDeploymentRecord> deployments) : IManagedAgentRegistry, IAgentDeploymentRegistry
    {
        public IReadOnlyList<ManagedAgentRecord> List(string projectDirectory) => agents;

        IReadOnlyList<AgentDeploymentRecord> IAgentDeploymentRegistry.List(string projectDirectory) => deployments;

        public ManagedAgentRecord Add(string projectDirectory, ManagedAgentRecord record) => throw new NotSupportedException();

        public ManagedAgentRecord Update(string projectDirectory, ManagedAgentRecord record) => throw new NotSupportedException();

        public int RemoveWithDeployments(string projectDirectory, string agentId, string expectedETag) => throw new NotSupportedException();

        AgentDeploymentRecord IAgentDeploymentRegistry.Add(string projectDirectory, AgentDeploymentRecord record) => throw new NotSupportedException();

        AgentDeploymentRecord IAgentDeploymentRegistry.Update(string projectDirectory, AgentDeploymentRecord record) => throw new NotSupportedException();

        void IAgentDeploymentRegistry.Remove(string projectDirectory, string deploymentId, string expectedETag) => throw new NotSupportedException();
    }
}
