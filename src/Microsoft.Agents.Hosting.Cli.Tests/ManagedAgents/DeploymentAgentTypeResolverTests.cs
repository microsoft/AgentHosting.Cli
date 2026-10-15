using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class DeploymentAgentTypeResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly JsonAgentConfigStore _store = ManagedAgentFixtures.Store();
    private readonly DeploymentAgentTypeResolver _resolver;

    public DeploymentAgentTypeResolverTests()
    {
        Directory.CreateDirectory(_root);
        _resolver = new DeploymentAgentTypeResolver(new AgentConfigLocator(), _store, _store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AnUnconfiguredProject_ReportsTheDefaultSourceTheGuardMustStillCatch()
    {
        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentName = "Development" });

        Assert.Equal(DeploymentAgentTypeSource.Default, resolved.Source);
        Assert.Equal(AgentTypes.CodeAgent, resolved.AgentType);
    }

    [Fact]
    public void DeploymentName_IsScopedToTheSuppliedAgent()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second", AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-code", agentId: "agent-2", name: "dev") with { AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-mcs", name: "dev"));

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "agent-1", DeploymentName = "dev" });

        Assert.Equal(AgentTypes.McsAgent, resolved.AgentType);
    }

    [Fact]
    public void DeploymentName_ScopedByAgentName_PicksThatAgentsRow()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second", AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-code", agentId: "agent-2", name: "dev") with { AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-mcs", name: "dev"));

        Assert.Equal(AgentTypes.CodeAgent, _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentName = "Second", DeploymentName = "dev" }).AgentType);
    }

    [Fact]
    public void DeploymentName_MatchingBothTypes_IsRejectedRatherThanGuessed()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second", AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-code", agentId: "agent-2", name: "dev") with { AgentType = AgentTypes.CodeAgent });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-mcs", name: "dev"));

        var failure = Assert.Throws<ManagedAgentException>(() => _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentName = "dev" }));

        Assert.Equal("deployment-ambiguous", failure.ErrorKind);
    }

    [Fact]
    public void DeploymentName_MatchingOneTypeTwice_ResolvesThatType()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Agent() with { AgentId = "agent-2", DisplayName = "Second", AgentLocation = "Second" });
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-2", agentId: "agent-2", name: "dev"));

        Assert.Equal(AgentTypes.McsAgent, _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentName = "dev" }).AgentType);
    }

    [Fact]
    public void DeploymentId_IsNotScopedByTheAgentSelector()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "dev"));

        Assert.Equal(AgentTypes.McsAgent, _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentId = "slot-1" }).AgentType);
    }

    [Fact]
    public void DeploymentRecord_WinsOverEverythingElse()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1"));

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentId = "slot-1", HasCodeAgentOptions = true });

        Assert.Equal(AgentTypes.McsAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.DeploymentRecord, resolved.Source);
        Assert.True(resolved.IsMcs);
    }

    [Fact]
    public void DeploymentRecord_ResolvesByName()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());
        _store.Add(_root, ManagedAgentFixtures.Deployment(deploymentId: "slot-1", name: "production"));

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentName = "production" });

        Assert.Equal(DeploymentAgentTypeSource.DeploymentRecord, resolved.Source);
    }

    [Fact]
    public void AgentRecord_ResolvesWhenNoDeploymentMatches()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent(agentId: "agent-1"));

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "agent-1" });

        Assert.Equal(AgentTypes.McsAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.AgentRecord, resolved.Source);
    }

    [Fact]
    public void SingleAgentProject_ResolvesWithoutASelector()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent());

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root });

        Assert.Equal(DeploymentAgentTypeSource.AgentRecord, resolved.Source);
    }

    [Fact]
    public void MultiAgentProject_DoesNotGuessFromTheAgentTable()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent(agentId: "agent-1", displayName: "First") with { AgentLocation = "first" });
        _store.Add(_root, ManagedAgentFixtures.Agent(agentId: "agent-2", displayName: "Second") with { AgentLocation = "second" });

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root });

        Assert.Equal(DeploymentAgentTypeSource.Unresolved, resolved.Source);
    }

    [Fact]
    public void ConfiguredProjectWithAnUnknownAgent_IsUnresolvedRatherThanDefaulted()
    {
        _store.Add(_root, ManagedAgentFixtures.Agent(agentId: "agent-1", displayName: "First"));

        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, AgentId = "agent-missing" });

        Assert.Equal(DeploymentAgentTypeSource.Unresolved, resolved.Source);
    }

    [Fact]
    public void McsOptionShape_ResolvesMcsWhenNoRecordMatches()
    {
        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, HasMcsOptions = true });

        Assert.Equal(AgentTypes.McsAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.OptionShape, resolved.Source);
    }

    [Fact]
    public void CodeAgentOptionShape_ResolvesCodeAgentWhenNoRecordMatches()
    {
        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, HasCodeAgentOptions = true });

        Assert.Equal(AgentTypes.CodeAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.OptionShape, resolved.Source);
    }

    [Fact]
    public void AmbiguousOptionShape_FallsBackToTheDefault()
    {
        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, HasMcsOptions = true, HasCodeAgentOptions = true });

        Assert.Equal(AgentTypes.CodeAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.Default, resolved.Source);
    }

    [Fact]
    public void NoProjectConfiguration_FallsBackToTheDefault()
    {
        var resolved = _resolver.Resolve(new DeploymentAgentTypeQuery { Project = _root, DeploymentId = "slot-1" });

        Assert.Equal(AgentTypes.CodeAgent, resolved.AgentType);
        Assert.Equal(DeploymentAgentTypeSource.Default, resolved.Source);
    }

    [Fact]
    public void NullQuery_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(null!));
    }
}
