using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsRoutesTests
{
    [Theory]
    [MemberData(nameof(AmsAgentCreationAdapterTests.BoundedRouteIdentifiers), MemberType = typeof(AmsAgentCreationAdapterTests))]
    public void Direct_PreservesBoundedIdentifiersAsEscapedSegments(string tenantId, string environmentId)
    {
        var uri = AmsRoutes.McsRegister(Target(AmsRouteForm.Direct, "https://amsdp-alias.example", tenantId, environmentId));

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("amsdp-alias.example", uri.Host);
        Assert.Equal(8, uri.Segments.Length);
        Assert.Equal("agenthosting/", uri.Segments[1]);
        Assert.Equal("tenants/", uri.Segments[2]);
        Assert.Equal(tenantId, Uri.UnescapeDataString(uri.Segments[3].TrimEnd('/')));
        Assert.Equal("environments/", uri.Segments[4]);
        Assert.Equal(environmentId, Uri.UnescapeDataString(uri.Segments[5].TrimEnd('/')));
        Assert.Equal("agents/", uri.Segments[6]);
        Assert.Equal("mcs", uri.Segments[7]);
        Assert.Equal("?api-version=1", uri.Query);
    }

    [Theory]
    [MemberData(nameof(AmsAgentCreationAdapterTests.BoundedRouteIdentifiers), MemberType = typeof(AmsAgentCreationAdapterTests))]
    public void EnvironmentHost_DoesNotProjectTenantOrEnvironmentIdentifiers(string tenantId, string environmentId)
    {
        Assert.Equal("https://env-1.api.powerplatform.com/agenthosting/agents/mcs?api-version=1", AmsRoutes.McsRegister(Target(AmsRouteForm.EnvironmentHost, "https://env-1.api.powerplatform.com/", tenantId, environmentId)).AbsoluteUri);
    }

    [Fact]
    public void UnknownRouteForm_IsRejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => AmsRoutes.McsRegister(Target((AmsRouteForm)int.MaxValue)));

        Assert.Equal("target", exception.ParamName);
        Assert.Equal((AmsRouteForm)int.MaxValue, exception.ActualValue);
    }

    [Theory]
    [InlineData(AmsRouteForm.EnvironmentHost, "https://host.example/agenthosting/agents/agent-1/mcs/deployments/dep-1/deploy?api-version=1")]
    [InlineData(AmsRouteForm.TenantHost, "https://host.example/agenthosting/environments/env-1/agents/agent-1/mcs/deployments/dep-1/deploy?api-version=1")]
    [InlineData(AmsRouteForm.Direct, "https://host.example/agenthosting/tenants/tenant-1/environments/env-1/agents/agent-1/mcs/deployments/dep-1/deploy?api-version=1")]
    public void McsDeploy_FollowsTheRouteForm(AmsRouteForm form, string expected)
    {
        Assert.Equal(expected, AmsRoutes.McsDeploy(Target(form), "agent-1", "dep-1").AbsoluteUri);
    }

    [Fact]
    public void EveryRoute_KeepsTheEndpointBasePathAndEscapesIdentifiers()
    {
        var target = Target(AmsRouteForm.Direct, "https://gateway.example/base");

        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/mcs/operations/op%3F1?api-version=1", AmsRoutes.McsOperation(target, "a/b", "op?1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb?api-version=1", AmsRoutes.Agent(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/mcs?api-version=1", AmsRoutes.McsAgent(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs?api-version=1", AmsRoutes.McsRegister(target).AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs?api-version=1&$top=25", AmsRoutes.McsAgentRecords(target, 25, null, null, null).AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs?api-version=1&$top=25&$skiptoken=a%2Bb%3D%26c&agentType=MCSWorkflow&repositoryId=r%2F1", AmsRoutes.McsAgentRecords(target, 25, "MCSWorkflow", "r/1", "a+b=&c").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments?api-version=1", AmsRoutes.Deployments(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231?api-version=1", AmsRoutes.Deployment(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/mcs/deployments/d%231/capture?api-version=1", AmsRoutes.McsCapture(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/mcs/deployments/d%231/deploy-records?api-version=1", AmsRoutes.McsDeployRecords(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs/repositories/r%2F1?api-version=1", AmsRoutes.McsRepository(target, "r/1", null, includeDeployments: false).AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs/repositories/r%2F1?api-version=1&agentFolder=Contoso%20HR%2Fa&includeDeployments=true", AmsRoutes.McsRepository(target, "r/1", "Contoso HR/a", includeDeployments: true).AbsoluteUri);
    }

    [Theory]
    [InlineData(AmsRouteForm.EnvironmentHost, "https://host.example/agenthosting/agents/mcs?api-version=1")]
    [InlineData(AmsRouteForm.Direct, "https://host.example/agenthosting/tenants/tenant-1/environments/env-1/agents/mcs?api-version=1")]
    public void McsRegister_IsTheMcsSegmentOfTheAgentCollection(AmsRouteForm form, string expected)
    {
        Assert.Equal(expected, AmsRoutes.McsRegister(Target(form)).AbsoluteUri);
    }

    private static AmsServiceTarget Target(AmsRouteForm form, string endpoint = "https://host.example/", string tenantId = "tenant-1", string environmentId = "env-1") =>
        new() { Endpoint = new Uri(endpoint), Audience = "https://api.powerplatform.com/", RouteForm = form, TenantId = tenantId, EnvironmentId = environmentId };
}
