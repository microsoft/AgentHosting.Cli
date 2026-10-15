using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class AsymmetricDeploymentRecordTests
{
    private static ManagedAgentRecord CodeAgent(string agentId = "agent-1", string location = ".") => StoreFixtures.Agent(agentId, $"Agent {agentId}") with
    {
        AgentType = AgentTypes.CodeAgent,
        AgentLocation = location,
    };

    private static AgentDeploymentRecord CodeAgentDeployment(string deploymentId = "slot-1", string agentId = "agent-1", string name = "dev") => new()
    {
        DeploymentId = deploymentId,
        AgentId = agentId,
        AgentType = AgentTypes.CodeAgent,
        TenantId = "tenant-1",
        DeploymentName = name,
        DeploymentType = DeploymentStages.Dev,
        EntityTag = "1",
        BotResourceId = "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/bot-1",
        EntraAppId = "app-1",
        EntraAppIdType = "SingleTenant",
        Location = "global",
        Sku = "F0",
        Endpoint = "https://example.invalid/api/messages",
    };

    [Fact]
    public void ACodeAgentDeploymentIsListableWithoutAnAgentRow()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgentDeployment("slot-1", "unregistered", "dev"));
        store.Add(project.Root, CodeAgentDeployment("slot-2", "unregistered", "prod") with { DeploymentType = DeploymentStages.Prod });

        var listed = new AgentDeploymentListService(new ManagedAgentResolver(store, store, new AgentConfigLocator())).List(new AgentDeploymentListRequest { Project = project.Root, AgentId = "unregistered" });

        Assert.Equal("unregistered", listed.AgentId);
        Assert.Equal(["dev", "prod"], listed.Slots.Select(slot => slot.DeploymentName));
    }

    [Fact]
    public void ListingAnUnknownAgentStillFails()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();

        Assert.Throws<ManagedAgentException>(() => new AgentDeploymentListService(new ManagedAgentResolver(store, store, new AgentConfigLocator())).List(new AgentDeploymentListRequest { Project = project.Root, AgentId = "nobody" }));
    }

    [Fact]
    public void ACodeAgentDeploymentNeedsNoOwningAgentRow()
    {
        using var project = new TemporaryProject();

        var stored = StoreFixtures.Create().Add(project.Root, CodeAgentDeployment(agentId: "unregistered"));

        Assert.Equal("unregistered", stored.AgentId);
    }

    [Fact]
    public void AnMcsDeploymentStillNeedsItsOwningAgentRow()
    {
        using var project = new TemporaryProject();

        var failure = Assert.Throws<ManagedAgentException>(() => StoreFixtures.Create().Add(project.Root, StoreFixtures.Deployment() with { AgentId = "unregistered" }));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains("references unknown agent", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDeploymentStillMatchesItsOwnerWhenOneExists()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, CodeAgentDeployment() with { TenantId = "other-tenant" }));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains("tenant", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDeploymentNeedsNoEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());

        var stored = store.Add(project.Root, CodeAgentDeployment());

        Assert.Null(stored.EnvironmentId);
        Assert.Null(stored.HomeEnvironmentId);
    }

    [Fact]
    public void TwoCodeAgentDeploymentsDoNotCollideOnTheAbsentEnvironment()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());

        store.Add(project.Root, CodeAgentDeployment("slot-1", name: "dev"));
        store.Add(project.Root, CodeAgentDeployment("slot-2", name: "prod"));

        Assert.Equal(2, ((IAgentDeploymentRegistry)store).List(project.Root).Count);
    }

    [Fact]
    public void AnMcsDeploymentMissingItsEnvironmentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment() with { EnvironmentId = null }));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains("is missing its environment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMcsDeploymentMissingItsHomeEnvironmentIsRejected()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());

        var failure = Assert.Throws<ManagedAgentException>(() => store.Add(project.Root, StoreFixtures.Deployment() with { HomeEnvironmentId = null }));

        Assert.Equal("config-invalid", failure.ErrorKind);
        Assert.Contains("is missing its home environment", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDeploymentOmitsEveryInapplicableFieldOnDisk()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());
        store.Add(project.Root, CodeAgentDeployment());

        using var document = JsonDocument.Parse(File.ReadAllText(project.ConfigPath));
        var deployment = document.RootElement.GetProperty("agentDeployments")[0];

        Assert.False(deployment.TryGetProperty("environmentId", out _));
        Assert.False(deployment.TryGetProperty("homeEnvironmentId", out _));
        Assert.False(deployment.TryGetProperty("isDefault", out _));
    }

    [Fact]
    public void AnMcsDeploymentOmitsEveryAzureFieldOnDisk()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var text = File.ReadAllText(project.ConfigPath);

        foreach (var field in new[] { "botResourceId", "entraAppId", "entraAppIdType", "sku", "endpoint", "location", "isDefault" })
        {
            Assert.DoesNotContain(field, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingADefaultClearsTheAgentsPreviousDefault()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());
        var first = store.Add(project.Root, CodeAgentDeployment("slot-1", name: "dev") with { IsDefault = true });
        store.Add(project.Root, CodeAgentDeployment("slot-2", name: "prod") with { IsDefault = true });

        var stored = ((IAgentDeploymentRegistry)store).List(project.Root);

        Assert.Null(stored.Single(deployment => deployment.DeploymentId == first.DeploymentId).IsDefault);
        Assert.True(stored.Single(deployment => deployment.DeploymentId == "slot-2").IsDefault);
    }

    [Fact]
    public void SettingADefaultLeavesAnotherAgentsDefaultAlone()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent("agent-1", "."));
        store.Add(project.Root, CodeAgent("agent-2", "second"));
        store.Add(project.Root, CodeAgentDeployment("slot-1", "agent-1", "dev") with { IsDefault = true });
        store.Add(project.Root, CodeAgentDeployment("slot-2", "agent-2", "dev") with { IsDefault = true });

        var stored = ((IAgentDeploymentRegistry)store).List(project.Root);

        Assert.True(stored.Single(deployment => deployment.DeploymentId == "slot-1").IsDefault);
        Assert.True(stored.Single(deployment => deployment.DeploymentId == "slot-2").IsDefault);
    }

    [Fact]
    public void PromotingADefaultThroughUpdateClearsThePrevious()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());
        var first = store.Add(project.Root, CodeAgentDeployment("slot-1", name: "dev") with { IsDefault = true });
        var second = store.Add(project.Root, CodeAgentDeployment("slot-2", name: "prod"));

        store.Update(project.Root, second with { IsDefault = true });

        var stored = ((IAgentDeploymentRegistry)store).List(project.Root);

        Assert.Null(stored.Single(deployment => deployment.DeploymentId == first.DeploymentId).IsDefault);
        Assert.True(stored.Single(deployment => deployment.DeploymentId == "slot-2").IsDefault);
    }

    [Fact]
    public void AnMcsDeploymentRoundTripsEveryGovernanceField()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, StoreFixtures.Agent());
        store.Add(project.Root, StoreFixtures.Deployment());

        var stored = ((IAgentDeploymentRegistry)store).List(project.Root).Single();

        Assert.Equal("env-home", stored.EnvironmentId);
        Assert.Equal("env-home", stored.HomeEnvironmentId);
        Assert.Equal(AgentTypes.McsAgent, stored.AgentType);
    }

    [Fact]
    public void ACodeAgentDeploymentRoundTripsEveryAzureField()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());
        var written = store.Add(project.Root, CodeAgentDeployment());

        var stored = ((IAgentDeploymentRegistry)store).List(project.Root).Single();

        Assert.Equal(written.BotResourceId, stored.BotResourceId);
        Assert.Equal(written.EntraAppId, stored.EntraAppId);
        Assert.Equal(written.EntraAppIdType, stored.EntraAppIdType);
        Assert.Equal(written.Sku, stored.Sku);
        Assert.Equal(written.Location, stored.Location);
        Assert.Equal(written.Endpoint, stored.Endpoint);
    }

    [Fact]
    public void TheStoredStageIsTextNotAnOrdinal()
    {
        using var project = new TemporaryProject();
        var store = StoreFixtures.Create();
        store.Add(project.Root, CodeAgent());
        store.Add(project.Root, CodeAgentDeployment());

        using var document = JsonDocument.Parse(File.ReadAllText(project.ConfigPath));
        var stage = document.RootElement.GetProperty("agentDeployments")[0].GetProperty("deploymentType");

        Assert.Equal(JsonValueKind.String, stage.ValueKind);
        Assert.Equal(DeploymentStages.Dev, stage.GetString());
    }
}
