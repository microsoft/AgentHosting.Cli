using System.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>Marks the playground's streaming result for playground-specific host outcome mapping.</summary>
internal enum PlaygroundSemanticOutcome
{
    None,
    Usage,
    Timeout,
    Cancellation,
}

internal interface IPlaygroundStreamingResult : ILiveTestStreamingResult
{
    PlaygroundSemanticOutcome SemanticOutcome { get; }
}

/// <summary>Owns the lazy playground state machine and all human output for one invocation.</summary>
internal sealed class PlaygroundExecution : IPlaygroundStreamingResult
{
    private const string PackageSpecifier = "@microsoft/m365agentsplayground@latest";
    private const string JsonUnsupportedMessage = "--json is not yet supported for playground.";
    private const string LiveEffectsWarning =
        "Warning: this launches a live playground session; actions can have real effects against the selected agent.";
    private const string DeviceCodeDisclosure =
        "Endpoint discovery uses the selected CLI account; the native playground owns its session sign-in. If it asks for a device code, follow its instructions in the new terminal window.";
    private const string DirectLineDisclosure =
        "Enter the MCS token endpoint or Direct Line secret only in the native playground's masked terminal prompt in the new terminal window. Do not put credentials in command arguments or chat.";

    private readonly PlaygroundRequest _request;
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
    private PlaygroundSemanticOutcome _semanticOutcome;

    internal PlaygroundExecution(
        PlaygroundRequest request,
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
        _request = request ?? throw new ArgumentNullException(nameof(request));
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

    public PlaygroundSemanticOutcome SemanticOutcome => _semanticOutcome;

    public async Task<int> RenderAsync(
        TextWriter stdout,
        TextWriter stderr,
        bool color,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var stopwatch = Stopwatch.StartNew();
        var installResult = PlaygroundInstallResult.NotNeeded;
        var errorCategory = PlaygroundErrorCategory.None;
        string? version = null;

        try
        {
            // This is intentionally the first state-machine transition. JSON is not a supported
            // playground transport, so it must not touch target configuration, PATH, npm, or prompts.
            if (_request.Json)
            {
                await WriteErrorAsync(stderr, JsonUnsupportedMessage).ConfigureAwait(false);
                errorCategory = PlaygroundErrorCategory.Unsupported;
                _semanticOutcome = PlaygroundSemanticOutcome.Usage;
                return ExitCodes.Usage;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var isDirectLine = string.Equals(_request.Target, "directline", StringComparison.OrdinalIgnoreCase);
            var validationError = ValidateRequest(isDirectLine);
            if (validationError is not null)
            {
                errorCategory = PlaygroundErrorCategory.Unsupported;
                _semanticOutcome = PlaygroundSemanticOutcome.Usage;
                await WriteErrorAsync(stderr, validationError).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            PlaygroundConnection connection;
            if (isDirectLine)
            {
                connection = new PlaygroundConnection.DirectLine();
            }
            else
            {
                LiveTestDestination target;
                try
                {
                    target = _resolve(_request.ToLiveTestRequest());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    errorCategory = ResolverCategory(failure);
                    _semanticOutcome = PlaygroundSemanticOutcome.Usage;
                    return await WriteKnownFailureAsync(stderr, failure, ExitCodes.Usage).ConfigureAwait(false);
                }

                try
                {
                    // A malformed client id must fail before discovery, consent, or npm mutation.
                    connection = new PlaygroundConnection.DirectEngine(target, _clientConfiguration().ClientId);
                }
                catch (AgentHostingException failure)
                {
                    errorCategory = PlaygroundErrorCategory.LaunchFailed;
                    return await WriteKnownFailureAsync(stderr, failure, ExitCodes.Failure).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    errorCategory = PlaygroundErrorCategory.LaunchFailed;
                    await WriteErrorAsync(stderr, "The playground client configuration could not be created. Configure the AgentHost public-client id and try again.")
                        .ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
            }

            PlaygroundTool? tool;
            PlaygroundToolDiscoveryResult discovery;
            try
            {
                discovery = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
                tool = discovery.Tool;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                errorCategory = PlaygroundErrorCategory.Cancellation;
                _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
                await WriteErrorAsync(stderr, "Playground startup was cancelled.").ConfigureAwait(false);
                return 130;
            }
            catch (TimeoutException)
            {
                errorCategory = PlaygroundErrorCategory.Timeout;
                _semanticOutcome = PlaygroundSemanticOutcome.Timeout;
                await WriteErrorAsync(stderr, "The playground tool check timed out. Check PATH and try again.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }
            catch (Exception)
            {
                errorCategory = PlaygroundErrorCategory.ToolNotFound;
                await WriteErrorAsync(stderr, "The playground tool could not be checked safely. Check PATH and try again.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            if (tool is null)
            {
                errorCategory = DiscoveryCategory(discovery);
                var consent = await GetInstallConsentAsync(stderr, cancellationToken).ConfigureAwait(false);
                if (!consent)
                {
                    installResult = PlaygroundInstallResult.Declined;
                    errorCategory = PlaygroundErrorCategory.InstallDeclined;
                    await WriteErrorAsync(
                        stderr,
                        "The playground installation was not authorized. Re-run interactively and type install, or pass --install-playground for an explicit unattended opt-in.")
                        .ConfigureAwait(false);
                    return ExitCodes.Failure;
                }

                try
                {
                    tool = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);
                    installResult = PlaygroundInstallResult.Installed;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    errorCategory = PlaygroundErrorCategory.Cancellation;
                    _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
                    await WriteErrorAsync(stderr, "Playground installation was cancelled.").ConfigureAwait(false);
                    return 130;
                }
                catch (PlaygroundInstallException failure)
                {
                    installResult = PlaygroundInstallResult.Failed;
                    errorCategory = failure.Kind == PlaygroundInstallFailureKind.Timeout
                        ? PlaygroundErrorCategory.Timeout
                        : PlaygroundErrorCategory.InstallFailed;
                    if (failure.Kind == PlaygroundInstallFailureKind.Timeout)
                    {
                        _semanticOutcome = PlaygroundSemanticOutcome.Timeout;
                    }
                    await WriteErrorAsync(stderr, $"{failure.Message} {failure.Remediation}").ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
                catch (TimeoutException)
                {
                    installResult = PlaygroundInstallResult.Failed;
                    errorCategory = PlaygroundErrorCategory.Timeout;
                    _semanticOutcome = PlaygroundSemanticOutcome.Timeout;
                    await WriteErrorAsync(stderr, "The playground installation timed out. Check registry connectivity and try again.")
                        .ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
                catch (Exception)
                {
                    installResult = PlaygroundInstallResult.Failed;
                    errorCategory = PlaygroundErrorCategory.InstallFailed;
                    await WriteErrorAsync(stderr,
                        "The playground could not be installed. Check Node.js, npm, and registry configuration, then try again.")
                        .ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
            }

            if (tool is null)
            {
                errorCategory = PlaygroundErrorCategory.ToolNotFound;
                await WriteErrorAsync(stderr,
                    "The playground installation did not produce a usable tool. Check npm's global bin directory and try again.")
                    .ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            errorCategory = PlaygroundErrorCategory.None;
            version = tool.Version.ToString();
            if (connection is PlaygroundConnection.DirectEngine directEngine)
            {
                try
                {
                    connection = directEngine with
                    {
                        Target = await _discover(directEngine.Target, _request.NonInteractive, cancellationToken).ConfigureAwait(false),
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    errorCategory = PlaygroundErrorCategory.Cancellation;
                    _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
                    await WriteErrorAsync(stderr, "Playground endpoint discovery was cancelled.").ConfigureAwait(false);
                    return 130;
                }
                catch (AgentHostingException failure)
                {
                    errorCategory = PlaygroundErrorCategory.LaunchFailed;
                    return await WriteKnownFailureAsync(stderr, failure, ExitCodes.Failure).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    errorCategory = PlaygroundErrorCategory.LaunchFailed;
                    await WriteErrorAsync(stderr,
                        "The playground endpoint could not be discovered. Check the selected CLI account, delegated Copilot Studio permissions, and published agent before retrying.")
                        .ConfigureAwait(false);
                    return ExitCodes.Failure;
                }
            }

            await WriteLineAsync(stdout, LiveEffectsWarning).ConfigureAwait(false);
            await WriteLineAsync(stdout, isDirectLine ? DirectLineDisclosure : DeviceCodeDisclosure).ConfigureAwait(false);
            await WriteLineAsync(stdout, $"Playground tool: {SafeValue(tool.SelectedPath)} (version {SafeValue(version)}).")
                .ConfigureAwait(false);

            Action started = _request.Plain
                ? static () => { }
                : () => stdout.WriteLine("Playground process started in a new terminal window. This terminal is free to use; stop Playground in its own window.");
            try
            {
                var exitCode = await _launcher.RunAsync(
                    tool,
                    connection,
                    started,
                    cancellationToken).ConfigureAwait(false);

                if (exitCode == ExitCodes.Success)
                {
                    return ExitCodes.Success;
                }

                if (exitCode == 130 && cancellationToken.IsCancellationRequested)
                {
                    errorCategory = PlaygroundErrorCategory.Cancellation;
                    _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
                    await WriteErrorAsync(stderr, "The playground process was cancelled.").ConfigureAwait(false);
                    return 130;
                }

                errorCategory = PlaygroundErrorCategory.LaunchFailed;
                await WriteErrorAsync(stderr, $"The playground process exited with code {exitCode}.").ConfigureAwait(false);
                return exitCode > 0 ? exitCode : ExitCodes.Failure;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                errorCategory = PlaygroundErrorCategory.Cancellation;
                _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
                await WriteErrorAsync(stderr, "The playground process was cancelled.").ConfigureAwait(false);
                return 130;
            }
            catch (PlaygroundLaunchException failure)
            {
                errorCategory = PlaygroundErrorCategory.LaunchFailed;
                await WriteErrorAsync(stderr, $"{failure.Message} {failure.Remediation}").ConfigureAwait(false);
                return ExitCodes.Failure;
            }
            catch (Exception)
            {
                errorCategory = PlaygroundErrorCategory.LaunchFailed;
                await WriteErrorAsync(stderr,
                    "The playground process could not be started or stopped safely. Verify the installation and try again.")
                    .ConfigureAwait(false);
                return ExitCodes.Failure;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorCategory = PlaygroundErrorCategory.Cancellation;
            _semanticOutcome = PlaygroundSemanticOutcome.Cancellation;
            await WriteErrorAsync(stderr, "Playground startup was cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception)
        {
            errorCategory = PlaygroundErrorCategory.LaunchFailed;
            await WriteErrorAsync(stderr, "The playground command could not complete safely. Verify the target and installation, then try again.")
                .ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        finally
        {
            try
            {
                _recordTelemetry(new PlaygroundTelemetryDetails(
                    stopwatch.ElapsedMilliseconds,
                    version,
                    installResult,
                    errorCategory));
            }
            catch
            {
                OptionalMeasurementDiagnostics.Report(
                    OptionalMeasurementFailureCategory.TelemetrySerialization);
            }
        }
    }

    private string? ValidateRequest(bool isDirectLine)
    {
        if (!isDirectLine && !string.Equals(_request.Target, "directengine", StringComparison.OrdinalIgnoreCase))
        {
            return "The --target value is not supported. Choose directline or directengine.";
        }

        if (!isDirectLine)
        {
            return null;
        }

        if (_request.Project is not null ||
            _request.AgentId is not null ||
            _request.AgentName is not null ||
            _request.SchemaName is not null ||
            _request.DeploymentId is not null ||
            _request.DeploymentName is not null ||
            _request.EnvironmentId is not null ||
            _request.Tenant is not null)
        {
            return "Direct Line does not accept --project, --agent-id, --agent-name, --schema-name, --deployment-id, --deployment-name, --environment-id, or --tenant. Remove these options or select --target directengine.";
        }

        if (_request.NonInteractive)
        {
            return "Direct Line does not support --non-interactive. Run without that option in an interactive terminal and enter the credential at the native masked prompt.";
        }

        return _isInputRedirected() || _isOutputRedirected()
            ? "Direct Line requires an interactive terminal for both input and output. Run without input/output redirection and enter the credential at the native masked prompt."
            : null;
    }

    private async Task<bool> GetInstallConsentAsync(
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (_request.NonInteractive || _isInputRedirected())
        {
            if (_request.InstallPlayground)
            {
                return true;
            }

            return false;
        }

        var detail =
            $"The playground is missing or below the required version. Install {PackageSpecifier} globally? npm install -g runs package lifecycle scripts. Type install to continue.";
        try
        {
            return await _confirmation
                .ConfirmAsync(detail, "install", cancellationToken)
                .ConfigureAwait(false) == ConfirmationOutcome.Confirmed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await WriteErrorAsync(stderr, "The playground installation prompt could not be completed. Re-run the command and try again.")
                .ConfigureAwait(false);
            return false;
        }
    }

    private static PlaygroundErrorCategory DiscoveryCategory(PlaygroundToolDiscoveryResult discovery) =>
        discovery.Rejections.Any(rejection =>
            rejection.Failure.Kind is PlaygroundFailureKind.BelowMinimum or PlaygroundFailureKind.InvalidVersion)
            ? PlaygroundErrorCategory.VersionTooOld
            : PlaygroundErrorCategory.ToolNotFound;

    private static PlaygroundErrorCategory ResolverCategory(Exception failure) =>
        failure is LiveTestTargetResolutionException { ErrorKind: "unsupported-target" }
            ? PlaygroundErrorCategory.Unsupported
            : PlaygroundErrorCategory.None;

    private static async Task<int> WriteKnownFailureAsync(
        TextWriter stderr,
        Exception failure,
        int fallbackExitCode)
    {
        if (failure is AgentHostingException typed)
        {
            await WriteErrorAsync(stderr, $"{typed.Message}{(string.IsNullOrWhiteSpace(typed.Remediation) ? string.Empty : $" {typed.Remediation}")}")
                .ConfigureAwait(false);
            return typed.ExitCode;
        }

        await WriteErrorAsync(stderr, "The playground target could not be resolved safely. Check the selectors and try again.")
            .ConfigureAwait(false);
        return fallbackExitCode;
    }

    private static Task WriteLineAsync(TextWriter writer, string message) =>
        writer.WriteLineAsync(message);

    private static Task WriteErrorAsync(TextWriter writer, string message) =>
        writer.WriteLineAsync(message);

    private static string SafeValue(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? "<unavailable>" : value;
}
