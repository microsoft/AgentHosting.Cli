using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Builds the lazy output-owning playground execution without doing live work at dispatch.</summary>
internal sealed class PlaygroundCommandHandler : ICommandHandler<PlaygroundRequest, ILiveTestStreamingResult>
{
    private readonly Func<LiveTestRequest, LiveTestDestination> _resolve;
    private readonly Func<LiveTestDestination, bool, CancellationToken, Task<LiveTestDestination>> _discover;
    private readonly IPlaygroundToolLocator _locator;
    private readonly IPlaygroundGlobalInstaller _installer;
    private readonly IPlaygroundLauncher _launcher;
    private readonly IConfirmationPrompt _confirmation;
    private readonly Func<AgentHostClientAppConfiguration> _clientConfiguration;
    private readonly Func<bool> _isInputRedirected;
    private readonly Func<bool> _isOutputRedirected;
    private readonly Action<PlaygroundTelemetryDetails> _recordTelemetry;

    internal PlaygroundCommandHandler(
        Func<LiveTestRequest, LiveTestDestination> resolve,
        Func<LiveTestDestination, bool, CancellationToken, Task<LiveTestDestination>> discover,
        IPlaygroundToolLocator locator,
        IPlaygroundGlobalInstaller installer,
        IPlaygroundLauncher launcher,
        IConfirmationPrompt confirmation,
        Func<AgentHostClientAppConfiguration> clientConfiguration,
        Func<bool> isInputRedirected,
        Func<bool> isOutputRedirected,
        Action<PlaygroundTelemetryDetails> recordTelemetry)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _clientConfiguration = clientConfiguration ?? throw new ArgumentNullException(nameof(clientConfiguration));
        _isInputRedirected = isInputRedirected ?? throw new ArgumentNullException(nameof(isInputRedirected));
        _isOutputRedirected = isOutputRedirected ?? throw new ArgumentNullException(nameof(isOutputRedirected));
        _recordTelemetry = recordTelemetry ?? throw new ArgumentNullException(nameof(recordTelemetry));
    }

    public Task<ILiveTestStreamingResult> HandleAsync(
        PlaygroundRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<ILiveTestStreamingResult>(
            new PlaygroundExecution(
                request,
                _resolve,
                _discover,
                _locator,
                _installer,
                _launcher,
                _confirmation,
                _clientConfiguration,
                _isInputRedirected,
                _isOutputRedirected,
                _recordTelemetry));
}
