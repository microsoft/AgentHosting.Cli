using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundGlobalInstallerTests
{
    [Fact]
    public async Task InstallAsync_DetectsNpmFirst_UsesExactGlobalInstallArguments_AndReprobes()
    {
        var npm = Tool("10.9.2", "/usr/bin/npm");
        var installed = Tool("0.2.29", "/usr/bin/agentsplayground");
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(npm),
            LocateResult = Discovery(installed),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(0, "secret stdout", "secret stderr"));
        var installer = new PlaygroundGlobalInstaller(locator, runner);

        var result = await installer.InstallAsync(CancellationToken.None);

        Assert.Same(installed, result);
        Assert.Equal(["detect-npm", "locate", "detect-node"], locator.Calls);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(npm.ExecutablePath, request.ExecutablePath);
        Assert.Equal(
            [.. npm.ArgumentPrefix, "install", "-g", "@microsoft/m365agentsplayground@latest"],
            request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(600), request.Timeout);
        Assert.Null(request.WorkingDirectory);
        Assert.Empty(request.EnvironmentOverrides);
        Assert.DoesNotContain(request.Arguments, argument =>
            argument is "--registry" or "--prefix" ||
            argument.Contains("config", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallAsync_VerifiesTheInstalledPlaygroundsNodeRuntime()
    {
        var locator = new RecordingLocator
        {
            NodeResult = Discovery(null, new PlaygroundToolRejection("node", new PlaygroundFailure
            {
                Kind = PlaygroundFailureKind.NodeBelowMinimum,
                Message = "Node.js is below the minimum.",
                Remediation = PlaygroundToolLocator.NodeInstallRemediation,
            })),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(0, string.Empty, string.Empty));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.NodeBelowMinimum, failure.Kind);
        Assert.Contains("22.17.1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["detect-npm", "locate", "detect-node"], locator.Calls);
    }

    [Fact]
    public async Task InstallAsync_RefusesMissingOrOldPrerequisite_WithoutRunningInstall()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(
                tool: null,
                new PlaygroundToolRejection(
                    "node",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.NodeBelowMinimum,
                        Message = "Node.js is too old.",
                        Remediation = PlaygroundToolLocator.NodeInstallRemediation,
                    })),
        };
        var runner = new RecordingRunner(_ => throw new Xunit.Sdk.XunitException("install must not run"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.NodeBelowMinimum, failure.Kind);
        Assert.Null(failure.ExitCode);
        Assert.NotEmpty(failure.Remediation);
        Assert.Empty(runner.Requests);
        Assert.Equal(["detect-npm"], locator.Calls);
    }

    [Fact]
    public async Task InstallAsync_UsesTerminalRelevantPrerequisiteRejection_NotStaleNodeHistory()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(
                tool: null,
                new PlaygroundToolRejection(
                    "old-node",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.NodeBelowMinimum,
                        Message = "secret stale node message",
                        Remediation = "secret stale node remediation",
                    }),
                new PlaygroundToolRejection(
                    "invalid-path",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.InvalidPathEntry,
                        Message = "secret invalid path message",
                        Remediation = "secret invalid path remediation",
                    }),
                new PlaygroundToolRejection(
                    "npm",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.NpmNotFound,
                        Message = "secret terminal npm message",
                        Remediation = "secret terminal npm remediation",
                    })),
        };
        var runner = new RecordingRunner(_ => throw new Xunit.Sdk.XunitException("install must not run"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.NpmNotFound, failure.Kind);
        Assert.Contains("npm", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Node.js", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(PlaygroundFailureKind.NodeNotFound, PlaygroundInstallFailureKind.NodeNotFound)]
    [InlineData(PlaygroundFailureKind.NodeInvalidVersion, PlaygroundInstallFailureKind.NodeInvalidVersion)]
    [InlineData(PlaygroundFailureKind.NodeBelowMinimum, PlaygroundInstallFailureKind.NodeBelowMinimum)]
    [InlineData(PlaygroundFailureKind.NpmNotFound, PlaygroundInstallFailureKind.NpmNotFound)]
    [InlineData(PlaygroundFailureKind.NpmInvalidVersion, PlaygroundInstallFailureKind.NpmInvalidVersion)]
    public async Task InstallAsync_RetainsSafePrerequisiteCategoryAndGuidance(
        PlaygroundFailureKind locatorKind,
        PlaygroundInstallFailureKind expectedKind)
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(
                tool: null,
                new PlaygroundToolRejection(
                    "secret-path",
                    new PlaygroundFailure
                    {
                        Kind = locatorKind,
                        Message = "secret raw locator message",
                        Remediation = "secret raw locator remediation",
                        ExitCode = 23,
                    })),
        };
        var runner = new RecordingRunner(_ => throw new Xunit.Sdk.XunitException("install must not run"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(23, failure.ExitCode);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(failure.Remediation);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("EACCES: permission denied; secret-permission", PlaygroundInstallFailureKind.Permission)]
    [InlineData("npm ERR! code ENOTFOUND secret-registry", PlaygroundInstallFailureKind.Network)]
    [InlineData("npm: command not found secret-npm", PlaygroundInstallFailureKind.NpmNotFound)]
    [InlineData("npm ERR! unknown failure secret-generic", PlaygroundInstallFailureKind.GenericInstall)]
    public async Task InstallAsync_CategorizesNonZeroExitWithoutLeakingCapturedOutput(
        string standardError,
        PlaygroundInstallFailureKind expectedKind)
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(17, "secret-stdout", standardError));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(17, failure.ExitCode);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", failure.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.Null(failure.InnerException);
        Assert.Equal(["detect-npm"], locator.Calls);
    }

    [Fact]
    public async Task InstallAsync_MapsTimeoutWithoutLeakingRunnerException()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
        };
        var runner = new RecordingRunner(_ => throw new TimeoutException("secret timeout detail"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.Timeout, failure.Kind);
        Assert.Null(failure.ExitCode);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task InstallAsync_PropagatesExternalCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
        };
        var runner = new CancellingRunner(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(cancellation.Token));

        Assert.Single(runner.Requests);
        Assert.Equal(["detect-npm"], locator.Calls);
    }

    [Fact]
    public async Task InstallAsync_DoesNotTurnCanceledCleanupFailureIntoCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
        };
        var runner = new CancellationThenFailureRunner(cancellation);

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(cancellation.Token));

        Assert.Equal(PlaygroundInstallFailureKind.GenericInstall, failure.Kind);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task InstallAsync_DoesNotTreatRunnerCleanupFailureAsSuccess()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
        };
        var runner = new RecordingRunner(_ => throw new InvalidOperationException("secret cleanup failure"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.GenericInstall, failure.Kind);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(locator.Calls.Skip(1));
    }

    [Fact]
    public async Task InstallAsync_ReprobesAfterExitZero_AndReportsFailedVerification()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
            LocateResult = Discovery(Tool("0.2.28", "/usr/bin/agentsplayground")),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(0, "secret stdout", "secret stderr"));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.PostVerification, failure.Kind);
        Assert.Null(failure.ExitCode);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["detect-npm", "locate"], locator.Calls);
    }

    [Fact]
    public async Task InstallAsync_SanitizesVerificationTimeoutAsPlaygroundVerificationFailure()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
            LocateFailure = new TimeoutException("secret verification timeout"),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(0, string.Empty, string.Empty));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.Timeout, failure.Kind);
        Assert.Contains("playground", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Node.js", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_NormalizesInstalledNodeProbeTimeoutToTimeout()
    {
        var locator = new RecordingLocator
        {
            NpmResult = Discovery(Tool("10.9.2", "/usr/bin/npm")),
            NodeResult = Discovery(
                null,
                new PlaygroundToolRejection(
                    "/usr/bin/node",
                    new PlaygroundFailure
                    {
                        Kind = PlaygroundFailureKind.NodeProbeTimedOut,
                        Message = "Node.js did not return a version within the allowed time.",
                        Remediation = "Check the Node.js installation and try again.",
                    })),
        };
        var runner = new RecordingRunner(_ => new PlaygroundProcessResult(0, string.Empty, string.Empty));

        var failure = await Assert.ThrowsAsync<PlaygroundInstallException>(() =>
            new PlaygroundGlobalInstaller(locator, runner).InstallAsync(CancellationToken.None));

        Assert.Equal(PlaygroundInstallFailureKind.Timeout, failure.Kind);
        Assert.Contains("Node.js", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["detect-npm", "locate", "detect-node"], locator.Calls);
    }

    private static PlaygroundTool Tool(string version, string path) =>
        new(path, [], path, PlaygroundVersion.Parse(version));

    private static PlaygroundToolDiscoveryResult Discovery(
        PlaygroundTool? tool,
        params PlaygroundToolRejection[] rejections) =>
        new(tool, rejections);

    private sealed class RecordingLocator : IPlaygroundToolLocator
    {
        public PlaygroundToolDiscoveryResult NpmResult { get; init; } =
            Discovery(Tool("10.9.2", "/usr/bin/npm"));

        public PlaygroundToolDiscoveryResult LocateResult { get; init; } =
            Discovery(Tool("0.2.29", "/usr/bin/agentsplayground"));

        public PlaygroundToolDiscoveryResult NodeResult { get; init; } =
            Discovery(Tool("22.17.1", "/usr/bin/node"));

        public Exception? LocateFailure { get; init; }

        public List<string> Calls { get; } = [];

        public Task<PlaygroundToolDiscoveryResult> DetectNodeAsync(
            PlaygroundTool? playground,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(LocateResult.Tool, playground);
            Calls.Add("detect-node");
            return Task.FromResult(NodeResult);
        }

        public Task<PlaygroundToolDiscoveryResult> LocateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("locate");
            if (LocateFailure is not null)
            {
                throw LocateFailure;
            }

            return Task.FromResult(LocateResult);
        }

        public Task<PlaygroundToolDiscoveryResult> DetectNpmAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("detect-npm");
            return Task.FromResult(NpmResult);
        }
    }

    private sealed class RecordingRunner(
        Func<PlaygroundProcessRequest, PlaygroundProcessResult> handler) : IPlaygroundProcessRunner
    {
        public List<PlaygroundProcessRequest> Requests { get; } = [];

        public Task<PlaygroundProcessResult> RunAsync(
            PlaygroundProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private sealed class CancellingRunner(CancellationTokenSource cancellation) : IPlaygroundProcessRunner
    {
        public List<PlaygroundProcessRequest> Requests { get; } = [];

        public Task<PlaygroundProcessResult> RunAsync(
            PlaygroundProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CancellationThenFailureRunner(CancellationTokenSource cancellation)
        : IPlaygroundProcessRunner
    {
        public Task<PlaygroundProcessResult> RunAsync(
            PlaygroundProcessRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new InvalidOperationException("secret cleanup failure");
        }
    }
}
