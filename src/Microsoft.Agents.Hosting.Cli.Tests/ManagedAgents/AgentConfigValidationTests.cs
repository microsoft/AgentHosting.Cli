using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class AgentConfigValidationTests
{
    private static ManagedAgentException Rejected(Action write) => Assert.Throws<ManagedAgentException>(write);

    [Fact]
    public void ConcurrentWritersDoNotLoseAnUpdate()
    {
        using var project = new TemporaryProject();
        StoreFixtures.Create().Add(project.Root, StoreFixtures.Agent());

        var accepted = 0;

        Parallel.For(0, 8, index =>
        {
            try
            {
                StoreFixtures.Create().Add(project.Root, StoreFixtures.Deployment($"slot-{index}", "agent-1", $"name-{index}", DeploymentStages.Test, $"env-{index}"));
                Interlocked.Increment(ref accepted);
            }
            catch (ManagedAgentException)
            {
            }
        });

        Assert.Equal(accepted, StoreFixtures.Create().Read(project.Root).AgentDeployments.Count);
    }

    [Fact]
    public void ASecondAgentInOneProjectIsAccepted()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var stored = store.Add(project.Root, StoreFixtures.Agent("agent-2", "Another Agent") with { AgentLocation = "Another Agent" });

        Assert.Equal("agent-2", stored.AgentId);
        Assert.Equal(2, store.List(project.Root).Count);
    }

    [Fact]
    public void AnUnknownAgentTypeIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();

        var failure = Rejected(() => store.Add(project.Root, StoreFixtures.Agent() with { AgentType = "Banana" }));

        Assert.Contains("unknown type 'Banana'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownAgentStateIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();

        var failure = Rejected(() => store.Add(project.Root, StoreFixtures.Agent() with { AgentState = "Pending" }));

        Assert.Contains("unknown state 'Pending'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeploymentTypeThatDisagreesWithItsAgentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Rejected(() => store.Add(project.Root, StoreFixtures.Deployment() with { AgentType = AgentTypes.CodeAgent }));

        Assert.Contains("has type 'CodeAgent'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeploymentTenantThatDisagreesWithItsAgentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Rejected(() => store.Add(project.Root, StoreFixtures.Deployment() with { TenantId = "another-tenant" }));

        Assert.Contains("has tenant 'another-tenant'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeploymentHomeEnvironmentThatDisagreesWithItsAgentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Rejected(() => store.Add(project.Root, StoreFixtures.Deployment() with { HomeEnvironmentId = "env-elsewhere" }));

        Assert.Contains("has home environment 'env-elsewhere'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConsistentDeploymentIsAccepted()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var stored = store.Add(project.Root, StoreFixtures.Deployment());

        Assert.Equal(AgentTypes.McsAgent, stored.AgentType);
        Assert.True(stored.IsEnabled);
        Assert.False(stored.IsQuarantined);
    }

    private static ManagedAgentException RejectedOnRead(TemporaryProject project, string find, string replace)
    {
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var document = File.ReadAllText(project.ConfigPath);
        Assert.Contains(find, document, StringComparison.Ordinal);
        File.WriteAllText(project.ConfigPath, document.Replace(find, replace, StringComparison.Ordinal));

        return Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Read(project.Root));
    }

    [Fact]
    public void AHandEditedAgentTypeIsRejectedOnRead()
    {
        using var project = new TemporaryProject();

        var failure = RejectedOnRead(project, $"\"{AgentTypes.McsAgent}\"", "\"Banana\"");

        Assert.Contains("Banana", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandEditedAgentStateIsRejectedOnRead()
    {
        using var project = new TemporaryProject();

        var failure = RejectedOnRead(project, EntityStates.Saved, "Pending");

        Assert.Contains("Pending", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandEditedDeploymentStageIsRejectedOnRead()
    {
        using var project = new TemporaryProject();

        var failure = RejectedOnRead(project, $"\"{DeploymentStages.Dev}\"", "\"Whenever\"");

        Assert.Contains("Whenever", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandEditedDeploymentTenantIsRejectedOnRead()
    {
        using var project = new TemporaryProject();

        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var document = File.ReadAllText(project.ConfigPath);
        var deployments = document.IndexOf("agentDeployments", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(
            project.ConfigPath,
            document[..deployments] + document[deployments..].Replace("tenant-1", "tenant-9", StringComparison.Ordinal));

        Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Read(project.Root));
    }

    [Fact]
    public void AnUntouchedDocumentIsReadBack()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var document = StoreFixtures.Create().Read(project.Root);

        Assert.Single(document.ManagedAgents);
        Assert.Single(document.AgentDeployments);
    }
}

public sealed class ManagedAgentPrincipalResolverTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111.22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111")]
    [InlineData("no-tenant-segment", "no-tenant-segment")]
    public void ObjectIdIsTheLeadingSegmentOfTheAccountKey(string accountKey, string expected)
    {
        Assert.Equal(expected, ManagedAgentPrincipalResolver.ObjectIdOf(accountKey));
    }

    [Fact]
    public async Task ResolveReturnsTheSignedInPrincipal()
    {
        var resolver = ManagedAgentFixtures.Principals();

        var principal = await resolver.ResolveAsync(CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal(PrincipalObjectTypes.User, principal!.ObjectType);
        Assert.False(string.IsNullOrWhiteSpace(principal.ObjectId));
    }

    [Fact]
    public async Task ResolveReturnsNothingWhenSignedOut()
    {
        var resolver = new ManagedAgentPrincipalResolver(new InMemoryAuthProfileStore());

        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
    }
}
