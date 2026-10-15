using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionRemovalTests
{
    private static PowerAppsConnectionResponse Answered(HttpStatusCode status, string body = "") => new(status, body);

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public void Absence_IsProvenByASuccessfulDelete(HttpStatusCode status) =>
        Assert.True(PowerAppsConnectionRemoval.ProvesAbsent(Answered(status)));

    [Theory]
    [InlineData("")]
    [InlineData("{\"error\":{\"code\":\"ConnectionNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ApiNotFound\"}}")]
    public void Absence_IsProvenWhenTheServiceSaysTheConnectionIsGone(string body) =>
        Assert.True(PowerAppsConnectionRemoval.ProvesAbsent(Answered(HttpStatusCode.NotFound, body)));

    [Theory]
    [InlineData("{\"error\":{\"code\":\"EnvironmentNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ServiceToServiceEnvironmentNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"RouteNotFound\"}}")]
    [InlineData("{\"error\":{\"code\":\"ServiceDecommissioned\"}}")]
    public void Absence_IsNotProvenWhenTheRequestNeverReachedTheConnection(string body) =>
        Assert.False(PowerAppsConnectionRemoval.ProvesAbsent(Answered(HttpStatusCode.NotFound, body)));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void Absence_IsNotProvenByARefusal(HttpStatusCode status) =>
        Assert.False(PowerAppsConnectionRemoval.ProvesAbsent(Answered(status, "denied")));
}
