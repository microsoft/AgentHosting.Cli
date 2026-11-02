using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class PlaygroundToolLocatorTests
{
    [Fact]
    public void SemanticVersion_OrdersPrereleaseNumericallyAndIgnoresBuildMetadata()
    {
        Assert.True(PlaygroundVersion.TryParse("1.2.3-alpha.2", out var alpha2));
        Assert.True(PlaygroundVersion.TryParse("1.2.3-alpha.10", out var alpha10));
        Assert.True(PlaygroundVersion.TryParse("1.2.3", out var release));
        Assert.True(PlaygroundVersion.TryParse("1.2.3+build.1", out var build1));
        Assert.True(PlaygroundVersion.TryParse("1.2.3+build.2", out var build2));

        Assert.True(alpha2.CompareTo(alpha10) < 0);
        Assert.True(alpha10.CompareTo(release) < 0);
        Assert.Equal(0, build1.CompareTo(build2));
        Assert.Equal(build1, build2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3 alpha")]
    [InlineData("1.2.3-!")]
    [InlineData("1.2.3+!")]
    public void SemanticVersion_RejectsMalformedInput(string value)
    {
        Assert.False(PlaygroundVersion.TryParse(value, out _));
    }

    [Fact]
    public void SemanticVersion_RejectsUnboundedInput()
    {
        var value = $"1.2.3+{new string('a', 300)}";

        Assert.False(PlaygroundVersion.TryParse(value, out _));
    }

    [Fact]
    public async Task Locate_UsesPathOrderAndSkipsBrokenAndOldCandidates()
    {
        var first = Path.Combine(Path.GetTempPath(), "playground-first");
        var second = Path.Combine(Path.GetTempPath(), "playground-second");
        var third = Path.Combine(Path.GetTempPath(), "playground-third");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, first, second, third),
            existingPaths:
            [
                Path.Combine(first, "agentsplayground"),
                Path.Combine(second, "agentsplayground"),
                Path.Combine(third, "agentsplayground"),
            ]);
        var runner = new RecordingProcessRunner(request =>
        {
            if (request.ExecutablePath == Path.Combine(first, "agentsplayground"))
            {
                throw new InvalidOperationException("raw process detail must not escape");
            }

            return request.ExecutablePath == Path.Combine(second, "agentsplayground")
                ? new PlaygroundProcessResult(0, "0.2.28", string.Empty)
                : new PlaygroundProcessResult(0, "0.2.29", string.Empty);
        });

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.NotNull(result.Tool);
        Assert.Equal(Path.Combine(third, "agentsplayground"), result.Tool.SelectedPath);
        Assert.Equal("0.2.29", result.Tool.Version.ToString());
        Assert.Equal(
            [
                Path.Combine(first, "agentsplayground"),
                Path.Combine(second, "agentsplayground"),
            ],
            result.Rejections.Select(rejection => rejection.SelectedPath));
        Assert.All(result.Rejections, rejection =>
        {
            Assert.NotEqual(PlaygroundFailureKind.None, rejection.Failure.Kind);
            Assert.DoesNotContain("raw process detail", rejection.Failure.Message, StringComparison.Ordinal);
        });
        Assert.Equal(
            environment.ExistingPaths.Count(path => Path.GetFileName(path) == "agentsplayground"),
            runner.Requests.Count);
        Assert.All(runner.Requests, request =>
        {
            Assert.True(Path.IsPathRooted(request.ExecutablePath));
            Assert.Equal(["--version"], request.Arguments);
            Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        });
    }

    [Theory]
    [InlineData("0.2.28", PlaygroundFailureKind.BelowMinimum)]
    [InlineData("0.2.29-beta.1", PlaygroundFailureKind.BelowMinimum)]
    public async Task Locate_EnforcesMinimumReleaseVersion(string output, PlaygroundFailureKind expectedKind)
    {
        var path = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var environment = EnvironmentWithDirectPlayground(path);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, output, string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.Null(result.Tool);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(expectedKind, rejection.Failure.Kind);
        Assert.Equal(0, rejection.Failure.ObservedVersion!.Value.Major);
        Assert.Equal(2, rejection.Failure.ObservedVersion!.Value.Minor);
        Assert.Contains("npm install", rejection.Failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_MapsProbeTimeoutToSanitizedCategorizedFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var environment = EnvironmentWithDirectPlayground(path);
        var runner = new RecordingProcessRunner(_ => throw new TimeoutException("secret command output"));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.ProbeTimedOut, rejection.Failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Requests[0].Timeout);
        Assert.DoesNotContain("secret command output", rejection.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret command output", rejection.Failure.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_MapsMalformedVersionWithoutEchoingProbeOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var environment = EnvironmentWithDirectPlayground(path);
        var runner = new RecordingProcessRunner(_ =>
            new PlaygroundProcessResult(0, "secret-output-not-a-version", "stderr-secret"));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.InvalidVersion, rejection.Failure.Kind);
        Assert.DoesNotContain("secret-output-not-a-version", rejection.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-secret", rejection.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_PropagatesCallerCancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var environment = EnvironmentWithDirectPlayground(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new RecordingProcessRunner(_ => throw new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateLocator(environment, runner).LocateAsync(cancellation.Token));
    }

    [Fact]
    public async Task Locate_DoesNotProbeNodeOrNpmWhenPlaygroundAlreadyWorks()
    {
        var playgroundPath = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var nodePath = Path.Combine(Path.GetTempPath(), "node");
        var npmPath = Path.Combine(Path.GetTempPath(), "npm");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, Path.GetDirectoryName(playgroundPath)!, Path.GetDirectoryName(nodePath)!),
            existingPaths: [playgroundPath, nodePath, npmPath]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.NotNull(result.Tool);
        Assert.Single(runner.Requests);
        Assert.Equal(playgroundPath, runner.Requests[0].ExecutablePath);
    }

    [Fact]
    public async Task WindowsCmdShim_UsesAdjacentNodeAndPackageEntryWithoutShell()
    {
        var shimDirectory = Path.Combine(Path.GetTempPath(), "playground-shim");
        var shimPath = Path.Combine(shimDirectory, "agentsplayground.cmd");
        var adjacentNode = Path.Combine(shimDirectory, "node.exe");
        var entryPoint = Path.Combine(
            shimDirectory,
            "node_modules",
            "@microsoft",
            "m365agentsplayground",
            "cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: shimDirectory,
            existingPaths: [shimPath, adjacentNode, entryPoint]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(shimPath, tool.SelectedPath);
        Assert.Equal(adjacentNode, tool.ExecutablePath);
        Assert.Equal([entryPoint], tool.ArgumentPrefix);
        Assert.Single(runner.Requests);
        Assert.Equal(adjacentNode, runner.Requests[0].ExecutablePath);
        Assert.Equal([entryPoint, "--version"], runner.Requests[0].Arguments);
        Assert.DoesNotContain(".cmd", runner.Requests[0].ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Requests[0].Timeout);
    }

    [Fact]
    public async Task WindowsCmdShim_FallsBackToNodeOnPathAndDoesNotLaunchPowerShellOrArbitraryShims()
    {
        var shimDirectory = Path.Combine(Path.GetTempPath(), "playground-unsafe-shim");
        var nodeDirectory = Path.Combine(Path.GetTempPath(), "playground-node");
        var shimPath = Path.Combine(shimDirectory, "agentsplayground.cmd");
        var unsafePowerShellPath = Path.Combine(shimDirectory, "agentsplayground.ps1");
        var arbitraryShimPath = Path.Combine(shimDirectory, "other-tool.cmd");
        var entryPoint = Path.Combine(
            shimDirectory,
            "node_modules",
            "@microsoft",
            "m365agentsplayground",
            "cli.js");
        var nodeOnPath = Path.Combine(nodeDirectory, "node.exe");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: JoinPathEntries(isWindows: true, shimDirectory, nodeDirectory),
            existingPaths: [shimPath, unsafePowerShellPath, arbitraryShimPath, entryPoint, nodeOnPath]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(nodeOnPath, tool.ExecutablePath);
        Assert.Equal([entryPoint], tool.ArgumentPrefix);
        Assert.DoesNotContain(runner.Requests, request =>
            request.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            request.ExecutablePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(arbitraryShimPath, result.Rejections.Select(rejection => rejection.SelectedPath));
    }

    [Fact]
    public async Task DetectNode_ValidatesSelectedWindowsRuntimeRatherThanAnotherNodeOnPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "selected-playground-node");
        var node = Path.Combine(directory, "node.exe");
        var otherDirectory = Path.Combine(Path.GetTempPath(), "newer-node");
        var otherNode = Path.Combine(otherDirectory, "node.exe");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: otherDirectory,
            existingPaths: [node, otherNode]);
        var runner = new RecordingProcessRunner(request => new PlaygroundProcessResult(
            0, request.ExecutablePath == node ? "v20.0.0" : "v24.0.0", string.Empty));
        var playground = new PlaygroundTool(
            node, ["playground-cli.js"], Path.Combine(directory, "agentsplayground.cmd"),
            PlaygroundVersion.Parse("0.2.29"));

        var result = await CreateLocator(environment, runner).DetectNodeAsync(playground, CancellationToken.None);

        Assert.Null(result.Tool);
        var failure = Assert.Single(result.Rejections).Failure;
        Assert.Equal(PlaygroundFailureKind.NodeBelowMinimum, failure.Kind);
        Assert.Contains("20.0.0", failure.Message, StringComparison.Ordinal);
        Assert.Contains("22.17.1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(PlaygroundToolLocator.NodeInstallRemediation, failure.Remediation);
        Assert.Equal(node, Assert.Single(runner.Requests).ExecutablePath);
    }

    [Theory]
    [InlineData("v22.17.0", false)]
    [InlineData("v22.17.1", true)]
    [InlineData("v24.0.0", true)]
    public async Task DetectNode_InstalledPosixPlaygroundChecksFirstNodeOnPathWithoutNpm(
        string version,
        bool usable)
    {
        var directory = Path.Combine(Path.GetTempPath(), "posix-playground-node");
        var node = Path.Combine(directory, "node");
        var otherDirectory = Path.Combine(Path.GetTempPath(), "other-posix-node");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, directory, otherDirectory),
            existingPaths: [node, Path.Combine(otherDirectory, "node")]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, version, string.Empty));
        var playground = new PlaygroundTool(
            Path.Combine(directory, "agentsplayground"), [], "agentsplayground",
            PlaygroundVersion.Parse("0.2.29"));

        var result = await CreateLocator(environment, runner).DetectNodeAsync(playground, CancellationToken.None);

        Assert.Equal(usable, result.Tool is not null);
        Assert.Equal(node, Assert.Single(runner.Requests).ExecutablePath);
    }

    [Fact]
    public async Task DetectNode_InstalledPosixPlaygroundSkipsNonExecutableNodeOnPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "posix-non-executable-node");
        var nonExecutableNode = Path.Combine(directory, "node");
        var otherDirectory = Path.Combine(Path.GetTempPath(), "posix-executable-node");
        var executableNode = Path.Combine(otherDirectory, "node");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, directory, otherDirectory),
            existingPaths: [nonExecutableNode, executableNode]);
        environment.NonExecutablePaths.Add(environment.GetFullPath(nonExecutableNode));
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "v22.17.1", string.Empty));
        var playground = new PlaygroundTool(
            Path.Combine(otherDirectory, "agentsplayground"), [], "agentsplayground",
            PlaygroundVersion.Parse("0.2.29"));

        var result = await CreateLocator(environment, runner).DetectNodeAsync(playground, CancellationToken.None);

        Assert.NotNull(result.Tool);
        Assert.Equal(executableNode, Assert.Single(runner.Requests).ExecutablePath);
    }

    [Fact]
    public async Task DetectNode_MissingNodeProvidesDownloadAndRetryGuidance()
    {
        var environment = new FakePlaygroundEnvironment(false, string.Empty, []);
        var runner = new RecordingProcessRunner(_ => throw new InvalidOperationException());

        var result = await CreateLocator(environment, runner).DetectNodeAsync(null, CancellationToken.None);

        Assert.Null(result.Tool);
        var failure = Assert.Single(result.Rejections).Failure;
        Assert.Equal(PlaygroundFailureKind.NodeNotFound, failure.Kind);
        Assert.Contains("https://nodejs.org/en/download", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("new terminal", failure.Remediation, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DetectNpm_ProbesNodeFirstAndReturnsRecognizedWindowsNpmShim()
    {
        var npmDirectory = Path.Combine(Path.GetTempPath(), "npm-shim");
        var nodeDirectory = Path.Combine(Path.GetTempPath(), "npm-node");
        var npmShim = Path.Combine(npmDirectory, "npm.cmd");
        var nodePath = Path.Combine(nodeDirectory, "node.exe");
        var npmEntryPoint = Path.Combine(npmDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: JoinPathEntries(isWindows: true, npmDirectory, nodeDirectory),
            existingPaths: [npmShim, nodePath, npmEntryPoint]);
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == nodePath && request.Arguments.SequenceEqual(["--version"])
                ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                : new PlaygroundProcessResult(0, "0.1.0", string.Empty));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(npmShim, tool.SelectedPath);
        Assert.Equal(nodePath, tool.ExecutablePath);
        Assert.Equal([npmEntryPoint], tool.ArgumentPrefix);
        Assert.Equal("0.1.0", tool.Version.ToString());
        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(nodePath, runner.Requests[0].ExecutablePath);
        Assert.Equal(["--version"], runner.Requests[0].Arguments);
        Assert.Equal([npmEntryPoint, "--version"], runner.Requests[1].Arguments);
        Assert.All(runner.Requests, request => Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout));
    }

    [Theory]
    [InlineData("v21.9.0", false, PlaygroundFailureKind.NodeBelowMinimum)]
    [InlineData("v22.0.0", false, PlaygroundFailureKind.NodeBelowMinimum)]
    [InlineData("v22.17.0", false, PlaygroundFailureKind.NodeBelowMinimum)]
    [InlineData("v22.17.1-rc.1", false, PlaygroundFailureKind.NodeBelowMinimum)]
    [InlineData("v22.17.1", true, PlaygroundFailureKind.None)]
    [InlineData("V22.17.1", false, PlaygroundFailureKind.NodeInvalidVersion)]
    public async Task DetectNpm_NormalizesOnlyLowercaseVOnNodeVersionOutput(
        string nodeOutput,
        bool expectedUsable,
        PlaygroundFailureKind expectedFailure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "node-v-prefix");
        var nodePath = Path.Combine(directory, "node.exe");
        var npmPath = Path.Combine(directory, "npm.exe");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: directory,
            existingPaths: [nodePath, npmPath]);
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == nodePath
                ? new PlaygroundProcessResult(0, nodeOutput, string.Empty)
                : new PlaygroundProcessResult(0, "10.9.2", string.Empty));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        if (expectedUsable)
        {
            Assert.NotNull(result.Tool);
            Assert.Equal(npmPath, result.Tool.ExecutablePath);
        }
        else
        {
            Assert.Null(result.Tool);
            Assert.Equal(expectedFailure, Assert.Single(result.Rejections).Failure.Kind);
        }
    }

    [Fact]
    public async Task LocateAndDetectNpm_KeepPlaygroundAndNpmVersionParsingStrict()
    {
        var playgroundPath = Path.Combine(Path.GetTempPath(), "agentsplayground");
        var playgroundEnvironment = EnvironmentWithDirectPlayground(playgroundPath);
        var playgroundResult = await CreateLocator(
            playgroundEnvironment,
            new RecordingProcessRunner(_ =>
                new PlaygroundProcessResult(0, "v0.2.29", string.Empty)))
            .LocateAsync(CancellationToken.None);

        var nodeDirectory = Path.Combine(Path.GetTempPath(), "strict-node");
        var nodePath = Path.Combine(nodeDirectory, "node");
        var npmPath = Path.Combine(nodeDirectory, "npm");
        var npmEntryPoint = Path.Combine(
            Path.GetDirectoryName(nodeDirectory)!,
            "lib",
            "node_modules",
            "npm",
            "bin",
            "npm-cli.js");
        var npmEnvironment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: nodeDirectory,
            existingPaths: [nodePath, npmPath, npmEntryPoint]);
        var npmResult = await CreateLocator(
            npmEnvironment,
            new RecordingProcessRunner(request =>
                request.ExecutablePath == nodePath &&
                request.Arguments.SequenceEqual(["--version"])
                    ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                    : new PlaygroundProcessResult(0, "v10.9.2", string.Empty)))
            .DetectNpmAsync(CancellationToken.None);

        Assert.Equal(PlaygroundFailureKind.InvalidVersion, Assert.Single(playgroundResult.Rejections).Failure.Kind);
        Assert.Null(npmResult.Tool);
        Assert.Equal(PlaygroundFailureKind.NpmInvalidVersion, Assert.Single(npmResult.Rejections).Failure.Kind);
    }

    [Fact]
    public async Task DetectNpm_UsesTheValidatedNodeWhenAnEarlierNodeProbeIsBroken()
    {
        var oldNodeDirectory = Path.Combine(Path.GetTempPath(), "npm-broken-node");
        var goodNodeDirectory = Path.Combine(Path.GetTempPath(), "npm-good-node");
        var npmDirectory = Path.Combine(Path.GetTempPath(), "npm-good-shim");
        var oldNodePath = Path.Combine(oldNodeDirectory, "node.exe");
        var goodNodePath = Path.Combine(goodNodeDirectory, "node.exe");
        var npmShim = Path.Combine(npmDirectory, "npm.cmd");
        var npmEntryPoint = Path.Combine(npmDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: JoinPathEntries(isWindows: true, oldNodeDirectory, goodNodeDirectory, npmDirectory),
            existingPaths: [oldNodePath, goodNodePath, npmShim, npmEntryPoint]);
        var runner = new RecordingProcessRunner(request =>
        {
            if (request.ExecutablePath == oldNodePath)
            {
                throw new InvalidOperationException("broken node");
            }

            return request.ExecutablePath == goodNodePath && request.Arguments.SequenceEqual(["--version"])
                ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                : request.ExecutablePath == goodNodePath
                    ? new PlaygroundProcessResult(0, "10.9.2", string.Empty)
                    : throw new InvalidOperationException("unexpected executable");
        });

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        Assert.NotNull(result.Tool);
        Assert.Equal(goodNodePath, result.Tool.ExecutablePath);
        Assert.Equal([npmEntryPoint], result.Tool.ArgumentPrefix);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(oldNodePath, runner.Requests[0].ExecutablePath);
        Assert.Equal(goodNodePath, runner.Requests[1].ExecutablePath);
        Assert.Equal(goodNodePath, runner.Requests[2].ExecutablePath);
    }

    [Fact]
    public async Task DetectNpm_RejectsOldNodeAndDoesNotProbeNpm()
    {
        var npmDirectory = Path.Combine(Path.GetTempPath(), "npm-old-node");
        var nodePath = Path.Combine(npmDirectory, "node.exe");
        var npmShim = Path.Combine(npmDirectory, "npm.cmd");
        var npmEntryPoint = Path.Combine(npmDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: npmDirectory,
            existingPaths: [nodePath, npmShim, npmEntryPoint]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "21.9.0", string.Empty));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        Assert.Null(result.Tool);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.NodeBelowMinimum, rejection.Failure.Kind);
        Assert.Single(runner.Requests);
        Assert.Equal(nodePath, runner.Requests[0].ExecutablePath);
    }

    [Fact]
    public async Task DetectNpm_AppendsNodeNotFoundAfterMalformedPathEntryWhenNoNodeCandidateExists()
    {
        var malformedPath = "\"unterminated-node-path";
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: malformedPath,
            existingPaths: []);
        var runner = new RecordingProcessRunner(_ =>
            throw new InvalidOperationException("no process should run"));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        Assert.Null(result.Tool);
        Assert.Contains(
            result.Rejections,
            rejection => rejection.Failure.Kind == PlaygroundFailureKind.InvalidPathEntry);
        var missingNode = Assert.Single(
            result.Rejections,
            rejection => rejection.Failure.Kind == PlaygroundFailureKind.NodeNotFound);
        Assert.Equal("node", missingNode.SelectedPath);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DetectNpm_MapsNodeTimeoutWithoutProbingNpm()
    {
        var nodeDirectory = Path.Combine(Path.GetTempPath(), "npm-timeout-node");
        var nodePath = Path.Combine(nodeDirectory, "node.exe");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: nodeDirectory,
            existingPaths: [nodePath]);
        var runner = new RecordingProcessRunner(_ => throw new TimeoutException("raw timeout detail"));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.NodeProbeTimedOut, rejection.Failure.Kind);
        Assert.DoesNotContain("raw timeout detail", rejection.Failure.Message, StringComparison.Ordinal);
        Assert.Single(runner.Requests);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Requests[0].Timeout);
    }

    [Fact]
    public async Task DetectNpm_OnPosixUsesNpmCliEntryPointWithValidatedNode()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "posix-npm-layout");
        var nodeDirectory = Path.Combine(runtimeRoot, "bin");
        var nodePath = Path.Combine(nodeDirectory, "node");
        var npmPath = Path.Combine(nodeDirectory, "npm");
        var npmEntryPoint = Path.Combine(
            runtimeRoot,
            "lib",
            "node_modules",
            "npm",
            "bin",
            "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: nodeDirectory,
            existingPaths: [nodePath, npmPath, npmEntryPoint]);
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == nodePath &&
            request.Arguments.SequenceEqual(["--version"])
                ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                : new PlaygroundProcessResult(0, "10.9.2", string.Empty));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(npmPath, tool.SelectedPath);
        Assert.Equal(nodePath, tool.ExecutablePath);
        Assert.Equal([npmEntryPoint], tool.ArgumentPrefix);
        Assert.Equal("10.9.2", tool.Version.ToString());
    }

    [Fact]
    public async Task DetectNpm_OnPosixUsesValidatedNodeForNpmScriptWhenPathSelectsAnOlderNode()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "posix-npm-runtime");
        var oldNodeDirectory = Path.Combine(runtimeRoot, "old", "bin");
        var goodNodeDirectory = Path.Combine(runtimeRoot, "good", "bin");
        var oldNodePath = Path.Combine(oldNodeDirectory, "node");
        var goodNodePath = Path.Combine(goodNodeDirectory, "node");
        var npmPath = Path.Combine(goodNodeDirectory, "npm");
        var npmEntryPoint = Path.Combine(
            runtimeRoot,
            "good",
            "lib",
            "node_modules",
            "npm",
            "bin",
            "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, oldNodeDirectory, goodNodeDirectory),
            existingPaths: [oldNodePath, goodNodePath, npmPath, npmEntryPoint]);
        var runner = new RecordingProcessRunner(request =>
        {
            if (request.ExecutablePath == oldNodePath)
            {
                return new PlaygroundProcessResult(0, "21.9.0", string.Empty);
            }

            if (request.ExecutablePath == goodNodePath &&
                request.Arguments.SequenceEqual(["--version"]))
            {
                return new PlaygroundProcessResult(0, "22.17.1", string.Empty);
            }

            if (request.ExecutablePath == goodNodePath &&
                request.Arguments.SequenceEqual([npmEntryPoint, "--version"]))
            {
                return new PlaygroundProcessResult(0, "10.9.2", string.Empty);
            }

            throw new InvalidOperationException("npm must run through the validated node");
        });

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(npmPath, tool.SelectedPath);
        Assert.Equal(goodNodePath, tool.ExecutablePath);
        Assert.Equal([npmEntryPoint], tool.ArgumentPrefix);
        Assert.Equal("10.9.2", tool.Version.ToString());
        Assert.Equal(
            [oldNodePath, goodNodePath, goodNodePath],
            runner.Requests.Select(request => request.ExecutablePath));
        Assert.Equal(
            [["--version"], ["--version"], [npmEntryPoint, "--version"]],
            runner.Requests.Select(request => request.Arguments));
    }

    [Fact]
    public async Task DetectNpm_OnPosixResolvesSymlinkedDistroPackagedNpmEntryPoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "posix-distro-npm");
        var binDirectory = Path.Combine(root, "usr", "bin");
        var nodePath = Path.Combine(binDirectory, "node");
        var npmPath = Path.Combine(binDirectory, "npm");
        var npmEntryPoint = Path.Combine(root, "usr", "share", "nodejs", "npm", "bin", "npm-cli.js");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: binDirectory,
            existingPaths: [nodePath, npmPath, npmEntryPoint]);
        environment.Links[environment.GetFullPath(npmPath)] = npmEntryPoint;
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == nodePath && request.Arguments.SequenceEqual(["--version"])
                ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                : request.Arguments.SequenceEqual([npmEntryPoint, "--version"])
                    ? new PlaygroundProcessResult(0, "10.9.2", string.Empty)
                    : throw new InvalidOperationException("unexpected process"));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        var tool = Assert.IsType<PlaygroundTool>(result.Tool);
        Assert.Equal(nodePath, tool.ExecutablePath);
        Assert.Equal([npmEntryPoint], tool.ArgumentPrefix);
    }

    [Fact]
    public async Task DetectNpm_OnPosixRejectsNpmScriptWithoutResolvableNpmCliEntryPoint()
    {
        var nodeDirectory = Path.Combine(Path.GetTempPath(), "posix-npm-without-cli");
        var nodePath = Path.Combine(nodeDirectory, "node");
        var npmPath = Path.Combine(nodeDirectory, "npm");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: nodeDirectory,
            existingPaths: [nodePath, npmPath]);
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == nodePath
                ? new PlaygroundProcessResult(0, "22.17.1", string.Empty)
                : throw new InvalidOperationException("npm must not run without a resolved CLI entry point"));

        var result = await CreateLocator(environment, runner).DetectNpmAsync(CancellationToken.None);

        Assert.Null(result.Tool);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.EntryPointNotFound, rejection.Failure.Kind);
        Assert.Equal(npmPath, rejection.SelectedPath);
        Assert.Single(runner.Requests);
        Assert.Equal(nodePath, runner.Requests[0].ExecutablePath);
    }

    [Fact]
    public async Task Locate_AllowsQuotedWindowsPathSegments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quoted-playground");
        var selectedPath = Path.Combine(directory, "agentsplayground.exe");
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: $"\"{directory}\"",
            existingPaths: [selectedPath]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.Equal(selectedPath, Assert.IsType<PlaygroundTool>(result.Tool).SelectedPath);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Locate_UsesCaseSensitiveCandidateDeduplicationOnPosix()
    {
        var firstDirectory = Path.Combine(Path.GetTempPath(), "PlaygroundCase");
        var secondDirectory = Path.Combine(Path.GetTempPath(), "playgroundcase");
        var firstPath = Path.Combine(firstDirectory, "agentsplayground");
        var secondPath = Path.Combine(secondDirectory, "agentsplayground");
        var environment = new FakePlaygroundEnvironment(
            isWindows: false,
            pathValue: string.Join(Path.PathSeparator, firstDirectory, secondDirectory),
            existingPaths: [firstPath, secondPath]);
        var runner = new RecordingProcessRunner(request =>
            request.ExecutablePath == firstPath
                ? throw new InvalidOperationException("first candidate is broken")
                : new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.Equal(secondPath, Assert.IsType<PlaygroundTool>(result.Tool).SelectedPath);
        Assert.Equal([firstPath, secondPath], runner.Requests.Select(request => request.ExecutablePath));
    }

    [Fact]
    public async Task Locate_RejectsMalformedWindowsPathEntryAndContinues()
    {
        var goodDirectory = Path.Combine(Path.GetTempPath(), "good-playground-path");
        var goodPath = Path.Combine(goodDirectory, "agentsplayground.exe");
        var malformedPath = "\"unterminated-quote";
        var environment = new FakePlaygroundEnvironment(
            isWindows: true,
            pathValue: JoinPathEntries(isWindows: true, malformedPath, goodDirectory),
            existingPaths: [goodPath]);
        var runner = new RecordingProcessRunner(_ => new PlaygroundProcessResult(0, "0.2.29", string.Empty));

        var result = await CreateLocator(environment, runner).LocateAsync(CancellationToken.None);

        Assert.Equal(goodPath, Assert.IsType<PlaygroundTool>(result.Tool).SelectedPath);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(PlaygroundFailureKind.InvalidPathEntry, rejection.Failure.Kind);
        Assert.Equal("A PATH entry was malformed and was skipped.", rejection.Failure.Message);
        Assert.DoesNotContain(malformedPath, rejection.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessRequest_CarriesLaunchEnvironmentAndWorkingDirectoryWithoutShellText()
    {
        var request = new PlaygroundProcessRequest
        {
            ExecutablePath = Path.GetFullPath("node.exe"),
            Arguments = ["entry.js", "--version"],
            Timeout = TimeSpan.FromSeconds(10),
            WorkingDirectory = Path.GetFullPath("workspace"),
            EnvironmentOverrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KEEP"] = "value",
                ["REMOVE"] = null,
            },
        };

        Assert.True(Path.IsPathRooted(request.ExecutablePath));
        Assert.Equal(["entry.js", "--version"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.Equal(Path.GetFullPath("workspace"), request.WorkingDirectory);
        Assert.Equal("value", request.EnvironmentOverrides["KEEP"]);
        Assert.Null(request.EnvironmentOverrides["REMOVE"]);
    }

    private static FakePlaygroundEnvironment EnvironmentWithDirectPlayground(string path) =>
        new(
            isWindows: false,
            pathValue: Path.GetDirectoryName(path)!,
            existingPaths: [path]);

    private static string JoinPathEntries(bool isWindows, params string[] entries) =>
        string.Join(isWindows ? ';' : Path.PathSeparator, entries);

    private static PlaygroundToolLocator CreateLocator(
        FakePlaygroundEnvironment environment,
        RecordingProcessRunner runner) =>
        new(runner, environment);

    private sealed class FakePlaygroundEnvironment(
        bool isWindows,
        string pathValue,
        IEnumerable<string> existingPaths) : IPlaygroundToolEnvironment
    {
        public bool IsWindows { get; } = isWindows;

        public string? PathValue { get; } = pathValue;

        public HashSet<string> ExistingPaths { get; } =
            new(
                existingPaths.Select(path => NormalizePath(path, isWindows)),
                isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public bool FileExists(string path) => ExistingPaths.Contains(GetFullPath(path));

        public HashSet<string> NonExecutablePaths { get; } = [];

        public bool IsExecutable(string path) =>
            FileExists(path) && !NonExecutablePaths.Contains(GetFullPath(path));

        public Dictionary<string, string> Links { get; } = [];

        public string ResolveRealPath(string path)
        {
            var current = GetFullPath(path);
            while (Links.TryGetValue(current, out var target))
            {
                current = GetFullPath(target);
            }

            return current;
        }

        public string GetFullPath(string path) => NormalizePath(path, IsWindows);

        private static string NormalizePath(string path, bool isWindows) =>
            Path.GetFullPath(
                isWindows
                    ? path.Replace('\\', Path.DirectorySeparatorChar)
                    : path);
    }

    private sealed class RecordingProcessRunner(
        Func<PlaygroundProcessRequest, PlaygroundProcessResult> handler) : IPlaygroundProcessRunner
    {
        public List<PlaygroundProcessRequest> Requests { get; } = [];

        public Task<PlaygroundProcessResult> RunAsync(
            PlaygroundProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }
}
