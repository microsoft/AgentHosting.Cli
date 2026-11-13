using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Concrete Core inventory composition for staged, inactive, or captured active profiles.</summary>
public sealed class ProfilePowerPlatformEnvironmentClientFactory : IProfileEnvironmentClientFactory
{
    private readonly HttpClient _http;
    private readonly IProfileResourceTokenProvider _tokens;
    private readonly string _binaryName;

    public ProfilePowerPlatformEnvironmentClientFactory(HttpClient http, IProfileResourceTokenProvider tokens, string binaryName = "msagent")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _binaryName = binaryName;
    }

    public IEnvironmentClient CreateClient(EnvironmentResolutionContext context) =>
        new PowerPlatformApiEnvironmentClient(_http, new ProfileBoundTokenProvider(_tokens, context), context.Target, _binaryName);
}
