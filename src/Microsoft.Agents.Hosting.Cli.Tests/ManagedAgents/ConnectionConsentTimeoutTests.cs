using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConnectionConsentTimeoutTests
{
    [Fact]
    public void TheSharedWaitMatchesTheConsentLinkLifetime()
    {
        Assert.Equal(TimeSpan.FromMinutes(8), ConnectionConsentLink.DefaultConsentTimeout);
    }

    [Fact]
    public async Task TheConsentLinkTierAnnouncesTheSharedWait()
    {
        using var handler = new ScriptedHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false));
            }

            if (request.Uri.AbsolutePath.EndsWith("/getConsentLink", StringComparison.Ordinal))
            {
                return ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse());
            }

            return ConnectionFixtures.Ok(ConnectionFixtures.Connection(authenticated: true));
        });

        var presenter = new RecordingMessagePresenter();
        using var flow = new ConsentLinkConnectionConsentFlow(
            new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler),
            presenter,
            new RecordingBrowserLauncher(),
            CoreServicesClusterCategory.Prod);

        await flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: true, CancellationToken.None);

        Assert.Contains(ExpectedWait, Assert.Single(presenter.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBrowserTierAnnouncesTheSameWait()
    {
        var presenter = new RecordingMessagePresenter();
        var connectors = new FakeConnectorCatalogPort();
        connectors.InternalIds.Add("shared_office365");

        var flow = new BrowserConnectionConsentFlow(new RecordingBrowserLauncher(), presenter, connectors, ManagedAgentFixtures.Rules, ManagedAgentFixtures.Principals(), CoreServicesClusterCategory.Prod);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Record.ExceptionAsync(() => flow.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", openBrowser: false, cancellation.Token));

        Assert.Contains(ExpectedWait, presenter.Messages[0], StringComparison.Ordinal);
    }

    private static string ExpectedWait =>
        $"{ConnectionConsentLink.DefaultConsentTimeout.TotalMinutes:0} minutes";
}
