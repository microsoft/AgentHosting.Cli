using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsRoutesTests
{
    public static TheoryData<string, string> BoundedRouteIdentifiers => new()
    {
        { "11111111-1111-1111-1111-111111111111", "env/blue" },
        { "tenant /?#%", "environment /?#%" },
        { "租戶", "環境🚀" },
        { " tenant\t", "\nenvironment " },
        { new string('t', 256), new string('e', 256) },
    };

    [Theory]
    [MemberData(nameof(BoundedRouteIdentifiers))]
    public void Direct_PreservesBoundedIdentifiersAsEscapedSegments(string tenantId, string environmentId)
    {
        var uri = AmsRoutes.Agents(Target(AmsRouteForm.Direct, "https://amsdp-alias.example", tenantId, environmentId));

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("amsdp-alias.example", uri.Host);
        Assert.Equal(7, uri.Segments.Length);
        Assert.Equal("agenthosting/", uri.Segments[1]);
        Assert.Equal("tenants/", uri.Segments[2]);
        Assert.Equal(tenantId, Uri.UnescapeDataString(uri.Segments[3].TrimEnd('/')));
        Assert.Equal("environments/", uri.Segments[4]);
        Assert.Equal(environmentId, Uri.UnescapeDataString(uri.Segments[5].TrimEnd('/')));
        Assert.Equal("agents", uri.Segments[6]);
        Assert.Equal("?api-version=1", uri.Query);
    }

    [Theory]
    [MemberData(nameof(BoundedRouteIdentifiers))]
    public void EnvironmentHost_DoesNotProjectTenantOrEnvironmentIdentifiers(string tenantId, string environmentId)
    {
        Assert.Equal("https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1", AmsRoutes.Agents(Target(AmsRouteForm.EnvironmentHost, "https://env-1.api.powerplatform.com/", tenantId, environmentId)).AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(BoundedRouteIdentifiers))]
    public void TenantHost_ProjectsOnlyTheEnvironmentIdentifier(string tenantId, string environmentId)
    {
        var uri = AmsRoutes.Deployments(Target(AmsRouteForm.TenantHost, "https://api.powerplatform.com/", tenantId, environmentId), "agent-1");

        Assert.Equal(["/", "agenthosting/", "environments/"], uri.Segments[..3]);
        Assert.Equal(environmentId, Uri.UnescapeDataString(uri.Segments[3].TrimEnd('/')));
        Assert.Equal(["agents/", "agent-1/", "deployments"], uri.Segments[4..]);
    }

    [Fact]
    public void UnknownRouteForm_IsRejected()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => AmsRoutes.Agents(Target((AmsRouteForm)int.MaxValue)));

        Assert.Equal("target", exception.ParamName);
        Assert.Equal((AmsRouteForm)int.MaxValue, exception.ActualValue);
    }

    [Theory]
    [InlineData(AmsRouteForm.EnvironmentHost, "https://host.example/agenthosting/agents/agent-1/deployments/dep-1?api-version=1")]
    [InlineData(AmsRouteForm.TenantHost, "https://host.example/agenthosting/environments/env-1/agents/agent-1/deployments/dep-1?api-version=1")]
    [InlineData(AmsRouteForm.Direct, "https://host.example/agenthosting/tenants/tenant-1/environments/env-1/agents/agent-1/deployments/dep-1?api-version=1")]
    public void Deployment_FollowsTheRouteForm(AmsRouteForm form, string expected)
    {
        Assert.Equal(expected, AmsRoutes.Deployment(Target(form), "agent-1", "dep-1").AbsoluteUri);
    }

    [Fact]
    public void EveryRoute_KeepsTheEndpointBasePathAndEscapesIdentifiers()
    {
        var target = Target(AmsRouteForm.Direct, "https://gateway.example/base");

        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents?api-version=1", AmsRoutes.Agents(target).AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb?api-version=1", AmsRoutes.Agent(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/packages?api-version=1", AmsRoutes.Packages(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments?api-version=1", AmsRoutes.Deployments(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231?api-version=1", AmsRoutes.Deployment(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/channelTypes?api-version=1", AmsRoutes.ChannelTypes(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/channels?api-version=1", AmsRoutes.Channels(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/channels/ms%3Fteams?api-version=1", AmsRoutes.Channel(target, "a/b", "d#1", "ms?teams").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/channels/directline/token?api-version=1", AmsRoutes.DirectLineToken(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/channels/face%3Fbook/webhook?api-version=1", AmsRoutes.ChannelWebhook(target, "a/b", "d#1", "face?book").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/oauthProviders?api-version=1", AmsRoutes.OAuthProviders(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/oauthConnections?api-version=1", AmsRoutes.OAuthConnections(target, "a/b", "d#1").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/oauthConnections/graph%20login.v2%3F?api-version=1", AmsRoutes.OAuthConnection(target, "a/b", "d#1", "graph login.v2?").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/cascadeDelete?api-version=1", AmsRoutes.AgentCascadeDelete(target, "a/b").AbsoluteUri);
        Assert.Equal("https://gateway.example/base/agenthosting/tenants/tenant-1/environments/env-1/agents/a%2Fb/deployments/d%231/deploy?api-version=1", AmsRoutes.DeploymentDeploy(target, "a/b", "d#1").AbsoluteUri);
    }

    [Fact]
    public void ListPageRoutes_AskForTheLargestPageAndCarryTheEscapedSkipTokenOfTheNextPage()
    {
        var target = Target(AmsRouteForm.EnvironmentHost);

        Assert.Equal("https://host.example/agenthosting/agents?api-version=1&$top=100", AmsRoutes.AgentListPage(target, null).AbsoluteUri);
        Assert.Equal("https://host.example/agenthosting/agents?api-version=1&$top=100&$skiptoken=a%2Bb%3D%26c", AmsRoutes.AgentListPage(target, "a+b=&c").AbsoluteUri);
        Assert.Equal("https://host.example/agenthosting/agents/agent-1/packages?api-version=1&$top=100", AmsRoutes.PackageListPage(target, "agent-1", null).AbsoluteUri);
        Assert.Equal("https://host.example/agenthosting/agents/agent-1/packages?api-version=1&$top=100&$skiptoken=page-2", AmsRoutes.PackageListPage(target, "agent-1", "page-2").AbsoluteUri);
        Assert.Equal("https://host.example/agenthosting/agents/agent-1/deployments?api-version=1&$top=100", AmsRoutes.DeploymentListPage(target, "agent-1", null).AbsoluteUri);
        Assert.Equal("https://host.example/agenthosting/agents/agent-1/deployments?api-version=1&$top=100&$skiptoken=page-2", AmsRoutes.DeploymentListPage(target, "agent-1", "page-2").AbsoluteUri);
    }

    [Theory]
    [InlineData(AmsRouteForm.EnvironmentHost, "https://host.example/agenthosting/agents/agent-1/deployments/dep-1/channels/msteams?api-version=1")]
    [InlineData(AmsRouteForm.TenantHost, "https://host.example/agenthosting/environments/env-1/agents/agent-1/deployments/dep-1/channels/msteams?api-version=1")]
    [InlineData(AmsRouteForm.Direct, "https://host.example/agenthosting/tenants/tenant-1/environments/env-1/agents/agent-1/deployments/dep-1/channels/msteams?api-version=1")]
    public void Channel_FollowsTheRouteForm(AmsRouteForm form, string expected)
    {
        Assert.Equal(expected, AmsRoutes.Channel(Target(form), "agent-1", "dep-1", "msteams").AbsoluteUri);
    }

    private static AmsServiceTarget Target(AmsRouteForm form, string endpoint = "https://host.example/", string tenantId = "tenant-1", string environmentId = "env-1") =>
        new() { Endpoint = new Uri(endpoint), Audience = "https://api.powerplatform.com/", RouteForm = form, TenantId = tenantId, EnvironmentId = environmentId };
}
