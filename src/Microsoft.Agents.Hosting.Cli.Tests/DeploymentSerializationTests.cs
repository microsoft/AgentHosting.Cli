using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentSerializationTests
{
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void Aggregate_SerializesAsComposedDeployment()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Sample(), StoreOptions));
        var root = document.RootElement;

        Assert.Equal("deployment-1", root.GetProperty("deploymentId").GetString());
        Assert.Equal("agent-1", root.GetProperty("agentId").GetString());
        Assert.Equal("contoso-dev", root.GetProperty("deploymentName").GetString());
        Assert.False(root.TryGetProperty("entraIdentity", out _));
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            root.GetProperty("bot").GetProperty("entraId").GetString());
        Assert.Equal("AppReg", root.GetProperty("bot").GetProperty("entraIdType").GetString());
        Assert.Equal(
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/contoso-dev",
            root.GetProperty("bot").GetProperty("resourceId").GetString());
        Assert.True(root.GetProperty("isDefault").GetBoolean());
        var oauthConnection = root.GetProperty("oauthConnections")[0];
        Assert.Equal("provider-id", oauthConnection.GetProperty("serviceProviderId").GetString());
        Assert.Equal(
            "Azure Active Directory v2",
            oauthConnection.GetProperty("serviceProviderDisplayName").GetString());
        Assert.False(oauthConnection.TryGetProperty("serviceProviderName", out _));
        Assert.False(oauthConnection.TryGetProperty("serviceProvider", out _));
    }

    [Fact]
    public void Aggregate_RoundTrips()
    {
        var restored = JsonSerializer.Deserialize<CodeAgentDeployment>(
            JsonSerializer.Serialize(Sample(), StoreOptions),
            StoreOptions);

        Assert.NotNull(restored);
        var expected = Sample();
        Assert.Equal(expected.DeploymentId, restored!.DeploymentId);
        Assert.Equal(expected.AgentId, restored.AgentId);
        Assert.Equal(expected.DeploymentName, restored.DeploymentName);
        Assert.Equal(expected.DeploymentType, restored.DeploymentType);
        Assert.Equal(expected.IsDefault, restored.IsDefault);
        Assert.Equal(expected.Bot, restored.Bot);
        Assert.Equivalent(expected.OAuthConnections, restored.OAuthConnections, strict: true);
    }

    [Fact]
    public void Aggregate_DoesNotPersistRawProviderResponses()
    {
        var json = JsonSerializer.Serialize(Sample(), StoreOptions);

        Assert.DoesNotContain("rawArmResponse", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientSecret", json, StringComparison.OrdinalIgnoreCase);
    }

    private static CodeAgentDeployment Sample() => new()
    {
        DeploymentId = "deployment-1",
        AgentId = "agent-1",
        DeploymentName = "contoso-dev",
        DeploymentType = DeploymentType.Dev,
        IsDefault = true,
        Bot = new DeploymentBotResource
        {
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/contoso-dev",
            Name = "contoso-dev",
            TenantId = "22222222-2222-2222-2222-222222222222",
            Sku = "F0",
            Location = "global",
            DisplayName = "Contoso Dev",
            EntraId = "11111111-1111-1111-1111-111111111111",
            EntraIdType = "AppReg",
            Description = "A development deployment.",
            Endpoint = "https://contoso.example.com/api/messages",
        },
        OAuthConnections =
        [
            new DeploymentOAuthConnection
            {
                ResourceId = "/connections/graph",
                ConnectionName = "graph",
                ServiceProviderId = "provider-id",
                ServiceProviderDisplayName = "Azure Active Directory v2",
                Scopes = ["User.Read"],
            },
        ],
    };
}
