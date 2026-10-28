using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class OraclePaginationTests
{
    private static readonly Uri CurrentPage = new("https://api.powerapps.com/providers/Microsoft.PowerApps/connections?api-version=2016-11-01");

    [Theory]
    [InlineData("https://api.powerapps.com/providers/Microsoft.PowerApps/connections?api-version=2016-11-01&$skiptoken=2")]
    [InlineData("https://API.POWERAPPS.COM/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("https://api.powerapps.com:443/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    public void ALinkOnTheSameHttpsOriginIsFollowed(string nextLink)
    {
        Assert.Equal(new Uri(nextLink), OracleHttp.NextPage(nextLink, CurrentPage, "Power Apps"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoLinkEndsThePaging(string? nextLink)
    {
        Assert.Null(OracleHttp.NextPage(nextLink, CurrentPage, "Power Apps"));
    }

    [Theory]
    [InlineData("http://api.powerapps.com/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("https://api.powerapps.com:8443/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("https://user:secret@api.powerapps.com/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("https://api.flow.microsoft.com/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("https://api.powerapps.com.attacker.example/providers/Microsoft.PowerApps/connections")]
    [InlineData("https://api.powerapps.com@attacker.example/providers/Microsoft.PowerApps/connections")]
    [InlineData("/providers/Microsoft.PowerApps/connections?$skiptoken=2")]
    [InlineData("not a url")]
    public void ALinkThatLeavesTheHttpsOriginIsRefusedBeforeTheTokenIsSent(string nextLink)
    {
        Assert.Equal("The Power Apps API returned an untrusted pagination URL.", Assert.Throws<E2EException>(() => OracleHttp.NextPage(nextLink, CurrentPage, "Power Apps")).Message);
    }

    [Fact]
    public void TheAllowedOriginIsThePageThatReturnedTheLink()
    {
        const string sameOrigin = "https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/env/flows?$skiptoken=2";
        var flowsPage = new Uri("https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/environments/env/flows?api-version=2016-11-01");

        Assert.Equal(new Uri(sameOrigin), OracleHttp.NextPage(sameOrigin, flowsPage, "Power Automate"));
        Assert.Equal("The Power Automate API returned an untrusted pagination URL.", Assert.Throws<E2EException>(() => OracleHttp.NextPage("https://api.powerapps.com/providers/Microsoft.ProcessSimple/environments/env/flows?$skiptoken=2", flowsPage, "Power Automate")).Message);
    }
}
