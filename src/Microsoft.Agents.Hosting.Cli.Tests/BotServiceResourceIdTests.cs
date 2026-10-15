using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class BotServiceResourceIdTests
{
    [Theory]
    [InlineData("subscriptions")]
    [InlineData("resourceGroups")]
    [InlineData("botServices")]
    public void Parse_StructuralWordInResourceGroup_PreservesCoordinates(string resourceGroup)
    {
        var resourceId =
            $"/subscriptions/sub-1/resourceGroups/{resourceGroup}/providers/Microsoft.BotService/botServices/dev1";

        var parsed = BotServiceResourceId.Parse(resourceId, "deployment-1");

        Assert.Equal("sub-1", parsed.SubscriptionId);
        Assert.Equal(resourceGroup, parsed.ResourceGroup);
        Assert.Equal("dev1", parsed.BotName);
    }

    [Theory]
    [InlineData("https://attacker.example/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1")]
    [InlineData("@attacker.example/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1")]
    [InlineData("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/dev1")]
    [InlineData("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/..")]
    [InlineData("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1#fragment")]
    [InlineData("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev1?query")]
    [InlineData("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/%2e%2e")]
    public void Parse_NonBotResourceShape_Throws(string resourceId)
    {
        var ex = Assert.Throws<DeploymentException>(() =>
            BotServiceResourceId.Parse(resourceId, "deployment-1"));

        Assert.Equal("deployment-invalid-bot", ex.ErrorKind);
    }

    [Fact]
    public void BuildRequestPath_EscapesEachCoordinateAsAPathSegment()
    {
        var path = BotServiceResourceId.BuildRequestPath("sub 1", "rg (one)", "bot one");

        Assert.Equal(
            "/subscriptions/sub%201/resourceGroups/rg%20%28one%29/providers/Microsoft.BotService/botServices/bot%20one",
            path);
    }

    [Theory]
    [InlineData(".", "rg-1", "dev1")]
    [InlineData("sub-1", "..", "dev1")]
    [InlineData("sub-1", "rg-1", ".")]
    public void BuildRequestPath_RejectsUnsafeCoordinates(
        string subscriptionId,
        string resourceGroup,
        string botName)
    {
        Assert.Throws<DeploymentException>(() =>
            BotServiceResourceId.BuildRequestPath(subscriptionId, resourceGroup, botName));
    }
}
