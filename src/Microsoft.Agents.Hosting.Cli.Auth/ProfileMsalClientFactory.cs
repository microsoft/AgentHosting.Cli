using System.Security.Cryptography.X509Certificates;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public interface IProfileMsalClientFactory
{
    IPublicClientApplication CreatePublic(ProfileTokenRequest request, bool broker);
    IConfidentialClientApplication CreateConfidential(ProfileTokenRequest request, X509Certificate2? certificate);
}

public sealed class ProfileMsalClientFactory(
    IPlatformInfo platform, ProfileCredentialRuntimeOptions runtime,
    ProfileFederationAssertionProvider federation, IMsalHttpClientFactory? httpClientFactory = null) : IProfileMsalClientFactory
{
    public IPublicClientApplication CreatePublic(ProfileTokenRequest request, bool broker)
    {
        var redirect = broker ? null : request.RedirectUri ?? runtime.BrowserRedirectUri ?? "http://localhost";
        var app = AgentHostClientAppConfiguration.Create(request.ClientId.ToString("D"), platform, redirect);
        if (!broker && request.Method == AuthCredentialMethod.Browser && !AgentHostClientAppConfiguration.IsLoopbackRedirect(app.RedirectUri.AbsoluteUri))
            throw new ClientApplicationConfigurationException("Private public clients require an HTTP loopback browser redirect URI.");
        var builder = PublicClientApplicationBuilder.Create(app.ClientId.ToString("D"))
            .WithRedirectUri(app.RedirectUri.AbsoluteUri)
            .WithAuthority(request.Authority.AuthorityUrl, validateAuthority: false)
            .WithInstanceDiscovery(false);
        if (httpClientFactory is not null) builder.WithHttpClientFactory(httpClientFactory);
        if (broker)
        {
            builder.WithBroker(MsalPublicClientProvider.CreateBrokerOptions(platform, listAccounts: false));
            if (platform.IsWindows) builder.WithParentActivityOrWindow(NativeMethods.GetConsoleOrTerminalWindow);
        }
        return builder.Build();
    }
    public IConfidentialClientApplication CreateConfidential(ProfileTokenRequest request, X509Certificate2? certificate)
    {
        var builder = ConfidentialClientApplicationBuilder.Create(request.ClientId.ToString("D"))
            .WithAuthority(request.Authority.AuthorityUrl, validateAuthority: false)
            .WithInstanceDiscovery(false);
        if (httpClientFactory is not null) builder.WithHttpClientFactory(httpClientFactory);
        if (certificate is not null) builder.WithCertificate(certificate);
        else if (request.Method == AuthCredentialMethod.ClientSecret) builder.WithClientSecret(request.ClientSecret);
        else builder.WithClientAssertion(assertion => federation.GetAssertionAsync(request.Method, assertion.CancellationToken));
        return builder.Build();
    }
}
