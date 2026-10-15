using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class AgentDeploymentListSelectorTests
{
    private static AgentDeploymentListService Create(out TemporaryProject project)
    {
        project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent("agent-1", "Contoso Helpdesk"));
        store.Add(project.Root, StoreFixtures.Deployment("slot-1", "agent-1", "dev", DeploymentStages.Dev, "env-dev"));
        store.Add(project.Root, StoreFixtures.Deployment("slot-2", "agent-1", "prod", DeploymentStages.Prod, "env-prod"));

        return new AgentDeploymentListService(new ManagedAgentResolver(store, store, new AgentConfigLocator()));
    }

    [Fact]
    public void ListingByAgentNameMatchesListingByAgentId()
    {
        var service = Create(out var project);
        using var _ = project;

        var byId = service.List(new AgentDeploymentListRequest { Project = project.Root, AgentId = "agent-1" });
        var byName = service.List(new AgentDeploymentListRequest { Project = project.Root, AgentName = "Contoso Helpdesk" });

        Assert.Equal(byId.AgentId, byName.AgentId);
        Assert.Equal(
            byId.Slots.Select(slot => slot.DeploymentId),
            byName.Slots.Select(slot => slot.DeploymentId));
    }

    [Fact]
    public void ListingByAgentNameReturnsTheSlotsInPromotionOrder()
    {
        var service = Create(out var project);
        using var _ = project;

        var listed = service.List(new AgentDeploymentListRequest { Project = project.Root, AgentName = "Contoso Helpdesk" });

        Assert.Equal(["dev", "prod"], listed.Slots.Select(slot => slot.DeploymentName));
    }

    [Fact]
    public void TheAgentIdWinsWhenBothSelectorsAreSupplied()
    {
        var service = Create(out var project);
        using var _ = project;

        var listed = service.List(new AgentDeploymentListRequest { Project = project.Root, AgentId = "agent-1", AgentName = "no such agent" });

        Assert.Equal("agent-1", listed.AgentId);
        Assert.Equal(2, listed.Slots.Count);
    }

    [Fact]
    public void ListingWithNeitherSelectorListsEveryAgentsDeployments()
    {
        var service = Create(out var project);
        using var _ = project;

        var listed = service.List(new AgentDeploymentListRequest { Project = project.Root });

        Assert.Null(listed.AgentId);
        Assert.NotEmpty(listed.Slots);
        Assert.All(listed.Slots, slot => Assert.False(string.IsNullOrWhiteSpace(slot.AgentId)));
    }

    [Fact]
    public void ListingByAnUnknownAgentNameFails()
    {
        var service = Create(out var project);
        using var _ = project;

        Assert.Throws<ManagedAgentException>(() => service.List(new AgentDeploymentListRequest { Project = project.Root, AgentName = "no such agent" }));
    }

    [Fact]
    public void ListingByAgentNameWhenTheAgentHasNoDeploymentsReturnsTheResolvedAgent()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent("agent-2", "Lonely Agent"));
        var service = new AgentDeploymentListService(new ManagedAgentResolver(store, store, new AgentConfigLocator()));

        var listed = service.List(new AgentDeploymentListRequest { Project = project.Root, AgentName = "Lonely Agent" });

        Assert.Equal("agent-2", listed.AgentId);
        Assert.Empty(listed.Slots);
    }
}
