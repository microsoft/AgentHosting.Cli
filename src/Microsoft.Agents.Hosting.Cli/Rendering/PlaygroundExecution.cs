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
        "The native playground owns sign-in. If it asks for a device code, follow its instructions in this terminal.";

    private readonly PlaygroundRequest _request;
    private readonly Func<LiveTestRequest, LiveTestDestination> _resolve;
    private readonly IPlaygroundToolLocator _locator;
    private readonly IPlaygroundGlobalInstaller _installer;
    private readonly IPlaygroundLauncher _launcher;
    private readonly IConfirmationPrompt _confirmation;
    private readonly Func<AgentHostClientAppConfiguration> _clientConfiguration;
    private readonly Func<bool> _isInputRedirected;
    private readonly Action<PlaygroundTelemetryDetails> _recordTelemetry;
    private PlaygroundSemanticOutcome _semanticOutcome;

    internal PlaygroundExecution(
        PlaygroundRequest request,
        Func<LiveTestRequest, LiveTestDestination> resolve,
        IPlaygroundToolLocator locator,
        IPlaygroundGlobalInstaller installer,
        IPlaygroundLauncher launcher,
        IConfirmationPrompt confirmation,
        Func<AgentHostClientAppConfiguration> clientConfiguration,
        Func<bool> isInputRedirected,
        Action<PlaygroundTelemetryDetails> recordTelemetry)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _clientConfiguration = clientConfiguration ?? throw new ArgumentNullException(nameof(clientConfiguration));
        _isInputRedirected = isInputRedirected ?? throw new ArgumentNullException(nameof(isInputRedirected));
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

            AgentHostClientAppConfiguration clientConfiguration;
            try
            {
                // Resolve the public client before discovery, prompting, or npm mutation. A malformed
                // client id is a local preflight failure and must not cause a global install.
                clientConfiguration = _clientConfiguration();
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
            await WriteLineAsync(stdout, LiveEffectsWarning).ConfigureAwait(false);
            await WriteLineAsync(stdout, DeviceCodeDisclosure).ConfigureAwait(false);
            await WriteLineAsync(stdout, $"Playground tool: {SafeValue(tool.SelectedPath)} (version {SafeValue(version)}).")
                .ConfigureAwait(false);

            Action started = _request.Plain
                ? static () => { }
                : () => stdout.WriteLine("Playground process started.");
            try
            {
                var exitCode = await _launcher.RunAsync(
                    tool,
                    target,
                    clientConfiguration.ClientId,
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
