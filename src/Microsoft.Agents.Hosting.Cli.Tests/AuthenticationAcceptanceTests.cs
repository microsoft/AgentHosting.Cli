using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.AuthenticationChecks;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthenticationAcceptanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void NativeTierCannotBeDisabledAsASuccess(bool dedicated, bool prepared) =>
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.RequireNativePreparation(
            dedicated, prepared, "win-x64", "win", Architecture.X64, Architecture.X64));

    [Theory]
    [InlineData("win-x86", "win", Architecture.X64, Architecture.X64)]
    [InlineData("linux-arm64", "linux", Architecture.X64, Architecture.Arm64)]
    [InlineData("osx-arm64", "osx", Architecture.Arm64, Architecture.X64)]
    [InlineData("osx-x64", "win", Architecture.X64, Architecture.X64)]
    [InlineData("linux-musl-x64", "linux", Architecture.X64, Architecture.X64)]
    public void NativeTierRejectsMislabeledProcessesAndEmulation(string rid, string os, Architecture process, Architecture hardware) =>
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.RequireNativePreparation(
            true, true, rid, os, process, hardware));

    [Fact]
    public void X86RequiresX86ProcessButCanRunOnNativeX64Windows() =>
        AcceptanceChecks.RequireNativePreparation(true, true, "win-x86", "win", Architecture.X86, Architecture.X64);

    [Theory]
    [InlineData(".")]
    [InlineData("artifacts")]
    [InlineData("artifacts/msagent-native-not-generated")]
    public void NativeFixtureRejectsNonownedCleanupRoots(string path) =>
        Assert.Throws<InvalidOperationException>(() => NativeChecks.RequirePreparedRoot(
            Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar))));

    [Fact]
    public void PreparedPrivateUnixBusSupportsLongCheckoutPathsWithoutAcceptingSharedEndpoints()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "msagent-native-" + Guid.NewGuid().ToString("N"));
        var address = "unix:abstract=" + Path.GetFileName(root);
        NativeChecks.RequirePreparedBus(root, address, null);
        Assert.Throws<InvalidOperationException>(() => NativeChecks.RequirePreparedBus(root, "unix:abstract=shared", null));
        Assert.Throws<InvalidOperationException>(() => NativeChecks.RequirePreparedBus(root, address, "message"));
    }

    [Fact]
    public void DaemonPreparationRepairs022PermissionsBeforeSelectionUnderLongOwnedXdgRoot()
    {
        var root = "/" + new string('r', 153);
        var fallback = root + "/home/keyring";
        Assert.False((fallback + "/control")[..107].StartsWith(root, StringComparison.Ordinal));
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        int changes = 0;
        AcceptanceChecks.PrepareDaemonControlDirectory("control", true, value => { mode = value; changes++; }, () => mode);
        var selected = mode == OwnerOnly ? "control" : fallback;
        AcceptanceChecks.RequireDaemonControlPublication("GNOME_KEYRING_CONTROL=" + selected);
        Assert.Equal("control", selected);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DaemonPreparationFailsBeforeLaunchWhenOwnerOnlyPermissionChangeDoesNotTakeEffect()
    {
        var mode = OwnerOnly | UnixFileMode.GroupRead;
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.PrepareDaemonControlDirectory(
            "control", true, _ => { }, () => mode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DaemonPreparationRejectsUnownedOrOversizedArgumentsBeforePermissionMutation(bool oversized)
    {
        bool changed = false;
        var argument = oversized ? new string('é', 51) : "control";
        Assert.True(!oversized || System.Text.Encoding.UTF8.GetByteCount(argument + "/control") > 107);
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.PrepareDaemonControlDirectory(
            argument, oversized, _ => changed = true, () => OwnerOnly));
        Assert.False(changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GNOME_KEYRING_CONTROL=/owned/home/keyring")]
    [InlineData("GNOME_KEYRING_CONTROL=control/keyring")]
    [InlineData("GNOME_KEYRING_CONTROL=../control")]
    [InlineData("GNOME_KEYRING_CONTROL=control\nGNOME_KEYRING_CONTROL=other")]
    public void DaemonPublicationRejectsMissingFallbackOrUnexpectedSelection(string? publication) =>
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.RequireDaemonControlPublication(publication));

    [Fact]
    public void PublishedDaemonValidationDoesNotRepairPermissionsAfterLaunch()
    {
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.RequireDaemonControlDirectory(
            "control", true, OwnerOnly | UnixFileMode.OtherRead));
        AcceptanceChecks.RequireDaemonControlDirectory("control", true, OwnerOnly);
        AcceptanceChecks.RequireDaemonControlPublication("GNOME_KEYRING_CONTROL=control");
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void NativeDriverWaitsForControlPreparationAndPublicationBeforeNativeAcceptance()
    {
        string? script = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "scripts", "test-authentication-native.ps1");
            if (File.Exists(candidate)) { script = File.ReadAllText(candidate); break; }
        }
        Assert.NotNull(script);
        var preparation = script.IndexOf("Wait-OwnedChild $controlPreparation", StringComparison.Ordinal);
        var launch = script.IndexOf("Start-OwnedChild 'gnome-keyring-daemon'", StringComparison.Ordinal);
        var publication = script.IndexOf("Wait-OwnedChild $controlPublication", StringComparison.Ordinal);
        var native = script.IndexOf("$harness, 'native'", StringComparison.Ordinal);
        Assert.True(preparation >= 0 && preparation < launch);
        Assert.True(publication > launch && publication < native);
        Assert.Contains("$daemon.Output.Wait(30000)", script);
    }

    [Fact]
    public async Task NativeFixtureExercisesBothKindsAndCleansOnlyIssuedReferences()
    {
        var backend = new TestStorage();
        await AcceptanceChecks.CheckOwnedStorageAsync(backend, default);
        Assert.Equal(3, backend.Written.Count);
        Assert.Contains(backend.Written, r => r.Kind == OwnedProtectedStorageKind.Credential);
        Assert.Contains(backend.Written, r => r.Kind == OwnedProtectedStorageKind.TokenCache);
        Assert.Equal(backend.Written.Select(r => (r.Kind, r.OpaqueId)).ToHashSet(),
            backend.Cleared.Select(r => (r.Kind, r.OpaqueId)).ToHashSet());
        Assert.Empty(backend.Values);
    }

    [Fact]
    public async Task FailedIssuedWriteStillCleansItsExactReference()
    {
        var backend = new TestStorage { FailWrite = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckOwnedStorageAsync(backend, default));
        Assert.Single(backend.Written);
        Assert.Single(backend.Cleared);
        Assert.Equal(backend.Written[0], backend.Cleared[0]);
        Assert.Empty(backend.Values);
    }

    [Fact]
    public async Task CancellationBeforeNativeFixtureMakesZeroProviderCalls()
    {
        var backend = new TestStorage();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AcceptanceChecks.CheckOwnedStorageAsync(backend, canceled.Token));
        Assert.Empty(backend.Written);
        Assert.Empty(backend.Cleared);
    }

    [Fact]
    public async Task SilentCleanupFailureCannotReportNativeAcceptance()
    {
        var backend = new TestStorage { LeaveCacheOnClear = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckOwnedStorageAsync(backend, default));
    }

    [Fact]
    public async Task CollisionNeverDeletesAnUnissuedReferenceAndWipesItsReadBuffer()
    {
        byte[] collision = [1, 2, 3];
        var backend = new TestStorage { Collision = collision };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckOwnedStorageAsync(backend, default));
        Assert.Empty(backend.Written);
        Assert.Empty(backend.Cleared);
        Assert.All(collision, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task NativeDenialRequiresRealFailuresForBothExistingKindsAndRestoresBeforeCleanup()
    {
        var backend = new TestStorage();
        int assertions = 0, restores = 0;
        await AcceptanceChecks.CheckDeniedStorageAsync(backend, references =>
        {
            Assert.Equal(2, references.Count);
            Assert.Equal(2, backend.Values.Count);
            backend.Denied = true;
            return Task.CompletedTask;
        }, () =>
        {
            Assert.True(backend.Denied);
            assertions++;
            return Task.CompletedTask;
        }, () => { backend.Denied = false; restores++; return Task.CompletedTask; });
        Assert.Equal(6, backend.DeniedAttempts);
        Assert.Equal(7, assertions);
        Assert.Equal(1, restores);
        Assert.Empty(backend.Values);
    }

    [Fact]
    public async Task MissingReadIsNotAProxyForDeniedStorage()
    {
        var backend = new TestStorage { SilentDeniedRead = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckDeniedStorageAsync(backend,
            _ => { backend.Denied = true; return Task.CompletedTask; }, () => Task.CompletedTask,
            () => { backend.Denied = false; return Task.CompletedTask; }));
        Assert.False(backend.Denied);
        Assert.Empty(backend.Values);
    }

    [Fact]
    public async Task IssuedDenialThatFailsStillRestoresAndCleansItsSeededReferences()
    {
        var backend = new TestStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckDeniedStorageAsync(backend,
            _ => { backend.Denied = true; throw new InvalidOperationException("issued denial"); },
            () => Task.CompletedTask, () => { backend.Denied = false; return Task.CompletedTask; }));
        Assert.False(backend.Denied);
        Assert.Equal(2, backend.Cleared.Count);
        Assert.Empty(backend.Values);
    }

    [Fact]
    public async Task DeniedFixtureCannotPassWithFailedRestoration()
    {
        var backend = new TestStorage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptanceChecks.CheckDeniedStorageAsync(backend,
            _ => { backend.Denied = true; return Task.CompletedTask; }, () => Task.CompletedTask,
            () => throw new InvalidOperationException("restore failure")));
        Assert.Equal(6, backend.DeniedAttempts);
        Assert.Empty(backend.Cleared);
    }

    [Fact]
    public void NativePfxOrchestrationIncludesPasswordlessAndEveryInvalidInputWithoutSkipping()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-orchestration", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var calls = new List<(string, string?)>();
        int verified = 0;
        AcceptanceChecks.CheckCertificateCases((name, password) =>
        {
            calls.Add((name, password));
            if (name == "protected.pfx" && password == "test-password" ||
                name == "passwordless.pfx" && string.IsNullOrEmpty(password))
                return X509CertificateLoader.LoadCertificate(certificate.RawData);
            throw new AuthenticationException("test invalid input");
        }, _ => verified++, "test-password");
        Assert.Equal(3, verified);
        Assert.Equal(8, calls.Count);
        Assert.Contains(("protected.pfx", (string?)null), calls);
        Assert.Contains(("protected.pfx", "wrong-fixture-password"), calls);
        Assert.Contains(("passwordless.pfx", "test-password"), calls);
        Assert.Contains(("invalid.pfx", "test-password"), calls);
        Assert.Contains(("public-only.pfx", "test-password"), calls);
    }

    [Fact]
    public void NativePfxOrchestrationRejectsAnUnexpectedlyAcceptedNegative()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=offline-orchestration", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => AcceptanceChecks.CheckCertificateCases(
            (_, _) => X509CertificateLoader.LoadCertificate(certificate.RawData), _ => { }, "test-password"));
    }

    [Fact]
    public void NativeLinuxDenialOnlyTargetsPreparedSecretServiceAndLeavesBusControlAvailable()
    {
        const string address = "unix:abstract=msagent-native-test-owned";
        const string configuration = "<busconfig><type>session</type><listen>unix:abstract=msagent-native-test-owned</listen><auth>EXTERNAL</auth><policy context='default'><allow send_destination='*'/></policy></busconfig>";
        var denied = System.Xml.Linq.XDocument.Parse(NativeChecks.DeniedBusConfiguration(configuration, address));
        var rule = Assert.Single(denied.Root!.Element("policy")!.Elements("deny"));
        Assert.Equal("org.freedesktop.secrets", rule.Attribute("send_destination")!.Value);
        Assert.Single(rule.Attributes());
        Assert.Equal(address, denied.Root.Element("listen")!.Value);
    }

    [Theory]
    [InlineData("<busconfig><listen>unix:abstract=shared</listen><policy context='default'/></busconfig>")]
    [InlineData("<busconfig><listen>unix:abstract=owned</listen><standard_session_servicedirs/><policy context='default'/></busconfig>")]
    public void NativeLinuxDenialRejectsSharedOrActivatingBusConfigurations(string configuration) =>
        Assert.Throws<InvalidOperationException>(() => NativeChecks.DeniedBusConfiguration(configuration, "unix:abstract=owned"));

    [Fact]
    public void NativeFixtureCleanupAttemptsEveryIssuedPathAndReportsFailure()
    {
        string[] issued = ["owned-protected.pfx", "owned-passwordless.pfx", "owned-invalid.pfx"];
        var attempts = new List<string>();
        Assert.False(AcceptanceChecks.DeleteIssuedFixtures(issued, path =>
        {
            attempts.Add(path);
            if (path == issued[0]) throw new IOException("owned fixture cleanup failed");
        }));
        Assert.Equal(issued, attempts);
    }

    private sealed class TestStorage : IOwnedProtectedStorageBackend
    {
        public bool Denied { get; set; }
        public bool SilentDeniedRead { get; init; }
        public int DeniedAttempts { get; private set; }
        public bool FailWrite { get; init; }
        public bool LeaveCacheOnClear { get; init; }
        public byte[]? Collision { get; init; }
        public List<OwnedProtectedStorageReference> Written { get; } = [];
        public List<OwnedProtectedStorageReference> Cleared { get; } = [];
        public Dictionary<(OwnedProtectedStorageKind, string), byte[]> Values { get; } = [];
        public Task<byte[]?> ReadAsync(OwnedProtectedStorageReference r, CancellationToken ct)
        {
            if (Denied && SilentDeniedRead) return Task.FromResult<byte[]?>(null);
            RequireAccess();
            return Task.FromResult(Collision ?? (Values.TryGetValue((r.Kind, r.OpaqueId), out var v) ? v.ToArray() : null));
        }
        public Task WriteAsync(OwnedProtectedStorageReference r, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            RequireAccess();
            Written.Add(r);
            Values[(r.Kind, r.OpaqueId)] = data.ToArray();
            if (FailWrite) throw new InvalidOperationException("issued mutation");
            return Task.CompletedTask;
        }
        public Task ClearAsync(OwnedProtectedStorageReference r, CancellationToken ct)
        {
            RequireAccess();
            Cleared.Add(r);
            if (!LeaveCacheOnClear || r.Kind != OwnedProtectedStorageKind.TokenCache)
                Values.Remove((r.Kind, r.OpaqueId));
            return Task.CompletedTask;
        }
        private void RequireAccess()
        {
            if (!Denied) return;
            DeniedAttempts++;
            throw new CacheProtectionUnavailableException("test-owned store denied");
        }
    }
}
