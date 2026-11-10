using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class GrsRemoteTests
{
    private const string Id = "cccccccc-3333-4333-8333-000000000003";
    private const string EnvironmentHost = "22222222222222222222222222222222.22.environment.api.powerplatform.com";
    private const string DevClusterHost = "pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com";

    private static readonly GitHostTrust Public = new(PowerPlatformApiTargetConfig.Public, new AmsEndpointProfile());

    [Theory]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/{Id}")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/{Id}/")]
    [InlineData($"https://{EnvironmentHost}/git/repositories/CCCCCCCC-3333-4333-8333-000000000003")]
    [InlineData($"https://{EnvironmentHost}:443/appframework/Git/Repositories/{Id}")]
    public void AnEnvironmentHostUrl_NamesItsRepository(string url) => Assert.Equal(Id, GrsRemote.RepositoryId(url, Public));

    [Fact]
    public void APersonalDevClusterUrl_NamesItsRepository_OnlyWhereThatHostIsSet()
    {
        var url = $"https://{DevClusterHost}/appframework/tenants/5cae182f-63ff-49f9-9f35-3acc2724e738/environments/env-1/git/repositories/{Id}";

        Assert.Equal(Id, GrsRemote.RepositoryId(url, new GitHostTrust(PowerPlatformApiTargetConfig.Test, new AmsEndpointProfile { GitHost = DevClusterHost })));
        Assert.Null(GrsRemote.RepositoryId(url, new GitHostTrust(PowerPlatformApiTargetConfig.Test, new AmsEndpointProfile())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData($"http://{EnvironmentHost}/appframework/git/repositories/{Id}")]
    [InlineData($"ssh://git@{EnvironmentHost}/appframework/git/repositories/{Id}")]
    [InlineData($"git@{EnvironmentHost}:appframework/git/repositories/{Id}")]
    [InlineData($"https://maker@{EnvironmentHost}/appframework/git/repositories/{Id}")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/{Id}?ref=main")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/{Id}#main")]
    [InlineData($"https://{EnvironmentHost}:8443/appframework/git/repositories/{Id}")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repos/{Id}")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/contoso-agents")]
    [InlineData($"https://{EnvironmentHost}/appframework/git/repositories/00000000-0000-0000-0000-000000000000")]
    [InlineData($"https://github.com/contoso/git/repositories/{Id}")]
    [InlineData($"https://environment.api.powerplatform.com/appframework/git/repositories/{Id}")]
    [InlineData($"https://{DevClusterHost}/appframework/git/repositories/{Id}")]
    public void AnyOtherUrl_NamesNoRepository(string? url) => Assert.Null(GrsRemote.RepositoryId(url, Public));

    [Theory]
    [InlineData("CCCCCCCC-3333-4333-8333-000000000003")]
    [InlineData("{cccccccc-3333-4333-8333-000000000003}")]
    public void RequireRepositoryId_GivesTheCanonicalId(string value) => Assert.Equal(Id, GrsRemote.RequireRepositoryId(value));

    [Theory]
    [InlineData("contoso-agents")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RequireRepositoryId_RefusesAValueThatIsNoRepositoryId(string value)
    {
        var failure = Assert.Throws<UsageException>(() => GrsRemote.RequireRepositoryId(value));

        Assert.Equal($"--repository-id '{value}' is not a repository id; `msagent agent show` names an agent's repository.", failure.Message);
    }
}
