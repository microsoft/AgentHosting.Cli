namespace Microsoft.Agents.Hosting.Cli.Core.Playground;

/// <summary>Installs the global playground package using the user's detected npm.</summary>
public interface IPlaygroundGlobalInstaller
{
    Task<PlaygroundTool> InstallAsync(CancellationToken cancellationToken);
}

/// <summary>Safe, fixed categories for a global playground installation failure.</summary>
public enum PlaygroundInstallFailureKind
{
    Prerequisite,
    NodeNotFound,
    NodeInvalidVersion,
    NodeBelowMinimum,
    NodeProbeTimedOut,
    NodeProbeFailed,
    NpmInvalidVersion,
    NpmProbeTimedOut,
    NpmProbeFailed,
    Permission,
    Network,
    NpmNotFound,
    Timeout,
    GenericInstall,
    PostVerification,
}

/// <summary>A sanitized failure produced while installing or verifying the playground.</summary>
public sealed class PlaygroundInstallException : Exception
{
    public PlaygroundInstallException(
        PlaygroundInstallFailureKind kind,
        string message,
        string remediation,
        int? exitCode = null)
        : base(message)
    {
        Kind = kind;
        Remediation = remediation;
        ExitCode = exitCode;
    }

    public PlaygroundInstallFailureKind Kind { get; }

    public int? ExitCode { get; }

    public string Remediation { get; }
}

/// <summary>Installs the playground globally through the user's native npm configuration.</summary>
public sealed class PlaygroundGlobalInstaller : IPlaygroundGlobalInstaller
{
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(600);

    private const string PackageSpecifier = "@microsoft/m365agentsplayground@latest";

    private readonly IPlaygroundToolLocator _toolLocator;
    private readonly IPlaygroundProcessRunner _processRunner;

    public PlaygroundGlobalInstaller(
        IPlaygroundToolLocator toolLocator,
        IPlaygroundProcessRunner processRunner)
    {
        _toolLocator = toolLocator;
        _processRunner = processRunner;
    }

    public async Task<PlaygroundTool> InstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var npmDiscovery = await DetectNpmAsync(cancellationToken).ConfigureAwait(false);
        if (npmDiscovery.Tool is not { } npm)
        {
            throw CreatePrerequisiteFailure(npmDiscovery);
        }

        var request = new PlaygroundProcessRequest
        {
            ExecutablePath = npm.ExecutablePath,
            Arguments = [.. npm.ArgumentPrefix, "install", "-g", PackageSpecifier],
            Timeout = InstallTimeout,
        };

        PlaygroundProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw CreateFailure(
                PlaygroundInstallFailureKind.Timeout,
                "npm did not finish installing the playground within the allowed time.",
                "Check registry connectivity and try the installation again.");
        }
        catch (FileNotFoundException)
        {
            throw CreateFailure(
                PlaygroundInstallFailureKind.NpmNotFound,
                "The npm executable could not be found while installing the playground package.",
                "Ensure npm is installed and available on PATH, then try again.");
        }
        catch (Exception)
        {
            throw CreateFailure(
                PlaygroundInstallFailureKind.GenericInstall,
                "npm could not install the playground package.",
                "Review your npm installation and configuration, then try again.");
        }

        if (result.ExitCode != 0)
        {
            throw CreateInstallFailure(result);
        }

        cancellationToken.ThrowIfCancellationRequested();

        PlaygroundToolDiscoveryResult verification;
        try
        {
            verification = await _toolLocator.LocateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw CreateFailure(
                PlaygroundInstallFailureKind.Timeout,
                "The playground installation verification did not complete within the allowed time.",
                "Restart the terminal and ensure npm's global bin directory is on PATH, then try again.");
        }
        catch (Exception)
        {
            throw CreatePostVerificationFailure();
        }

        if (verification.Tool is not { } installedTool ||
            installedTool.Version < PlaygroundToolLocator.MinimumPlaygroundVersion)
        {
            throw CreatePostVerificationFailure();
        }

        return installedTool;
    }

    private async Task<PlaygroundToolDiscoveryResult> DetectNpmAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _toolLocator.DetectNpmAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw CreateFailure(
                PlaygroundInstallFailureKind.Prerequisite,
                "A supported Node.js and npm installation could not be detected.",
                "Install Node.js 22 or newer with npm and ensure both are available on PATH.");
        }
    }

    private static PlaygroundInstallException CreatePrerequisiteFailure(
        PlaygroundToolDiscoveryResult discovery)
    {
        var failure = discovery.Rejections
            .Select(rejection => rejection.Failure)
            .LastOrDefault(rejection => rejection.Kind != PlaygroundFailureKind.InvalidPathEntry);

        return failure?.Kind switch
        {
            PlaygroundFailureKind.NodeNotFound => CreateFailure(
                PlaygroundInstallFailureKind.NodeNotFound,
                "Node.js 22 or newer was not found on PATH.",
                "Install Node.js 22 or newer and ensure it is available on PATH.",
                failure.ExitCode),
            PlaygroundFailureKind.NodeInvalidVersion => CreateFailure(
                PlaygroundInstallFailureKind.NodeInvalidVersion,
                "Node.js returned an invalid version.",
                "Install Node.js 22 or newer and try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NodeBelowMinimum => CreateFailure(
                PlaygroundInstallFailureKind.NodeBelowMinimum,
                "Node.js is below the required version.",
                "Install Node.js 22 or newer and try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NodeProbeTimedOut => CreateFailure(
                PlaygroundInstallFailureKind.NodeProbeTimedOut,
                "Node.js did not return a version within the allowed time.",
                "Check the Node.js installation and try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NodeProbeFailed => CreateFailure(
                PlaygroundInstallFailureKind.NodeProbeFailed,
                "Node.js could not be started for a version probe.",
                "Check that Node.js is available and runnable, then try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NpmNotFound => CreateFailure(
                PlaygroundInstallFailureKind.NpmNotFound,
                "npm was not found on PATH.",
                "Install npm with Node.js 22 or newer and ensure npm is available on PATH.",
                failure.ExitCode),
            PlaygroundFailureKind.NpmInvalidVersion => CreateFailure(
                PlaygroundInstallFailureKind.NpmInvalidVersion,
                "npm returned an invalid version.",
                "Install a supported npm version and try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NpmProbeTimedOut => CreateFailure(
                PlaygroundInstallFailureKind.NpmProbeTimedOut,
                "npm did not return a version within the allowed time.",
                "Check the npm installation and try again.",
                failure.ExitCode),
            PlaygroundFailureKind.NpmProbeFailed => CreateFailure(
                PlaygroundInstallFailureKind.NpmProbeFailed,
                "npm could not be started for a version probe.",
                "Check that npm is available and runnable, then try again.",
                failure.ExitCode),
            _ => CreateFailure(
                PlaygroundInstallFailureKind.Prerequisite,
                "A supported Node.js and npm installation could not be detected.",
                "Install Node.js 22 or newer with npm and ensure both are available on PATH.",
                failure?.ExitCode),
        };
    }

    private static PlaygroundInstallException CreateInstallFailure(
        PlaygroundProcessResult result)
    {
        var diagnostics = $"{result.StandardError}\n{result.StandardOutput}";
        var kind = ContainsAny(diagnostics, "EACCES", "EPERM")
            ? PlaygroundInstallFailureKind.Permission
            : ContainsAny(diagnostics, "ENOTFOUND", "ECONNRESET", "ETIMEDOUT")
                ? PlaygroundInstallFailureKind.Network
                : IsNpmNotFound(diagnostics)
                    ? PlaygroundInstallFailureKind.NpmNotFound
                    : PlaygroundInstallFailureKind.GenericInstall;

        return kind switch
        {
            PlaygroundInstallFailureKind.Permission => CreateFailure(
                kind,
                "npm did not have permission to install the playground package.",
                "Use a user-writable npm global prefix or adjust npm permissions, then try again.",
                result.ExitCode),
            PlaygroundInstallFailureKind.Network => CreateFailure(
                kind,
                "npm could not reach the configured package registry.",
                "Check registry connectivity, proxy, and authentication, then try again.",
                result.ExitCode),
            PlaygroundInstallFailureKind.NpmNotFound => CreateFailure(
                kind,
                "The npm executable could not be found while installing the playground package.",
                "Ensure npm is installed and available on PATH, then try again.",
                result.ExitCode),
            _ => CreateFailure(
                PlaygroundInstallFailureKind.GenericInstall,
                "npm could not install the playground package.",
                "Review your npm installation and configuration, then try again.",
                result.ExitCode),
        };
    }

    private static bool IsNpmNotFound(string diagnostics) =>
        diagnostics.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase) ||
        diagnostics.Contains("ENOENT", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static PlaygroundInstallException CreatePostVerificationFailure() =>
        CreateFailure(
            PlaygroundInstallFailureKind.PostVerification,
            "The playground installation could not be verified.",
            "Restart the terminal and ensure npm's global bin directory is on PATH, then try again.");

    private static PlaygroundInstallException CreateFailure(
        PlaygroundInstallFailureKind kind,
        string message,
        string remediation,
        int? exitCode = null) =>
        new(kind, message, remediation, exitCode);
}
