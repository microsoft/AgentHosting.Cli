using System.Diagnostics;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProfileCredentialLegacyCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCacheLeaseCreatesMissingConfigurationAncestors(bool protectedCache)
    {
        using var h = new ProfileCredentialTests.Harness();
        var root = Path.Combine(h.Directory, "missing-parent", "config");
        ILegacyMsalTokenCacheBackend Create() => protectedCache
            ? LegacyMsalTokenCacheBackendFactory.Windows(root, new Protector())
            : LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(root);
        var backend = Create();
        Assert.False(Directory.Exists(root));
        var value = "synthetic-token-cache"u8.ToArray();
        using (backend.AcquireLease(default))
        {
            Assert.Null(await backend.ReadAsync(default));
            await backend.WriteAsync(value, default);
        }
        var reopened = Create();
        using (reopened.AcquireLease(default))
            Assert.Equal(value, await reopened.ReadAsync(default));
        OwnedProfileFiles.CheckFile(AgentHostCacheDescriptor.Create(root).CacheFilePath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CredentialFilesRejectNonDirectoryAncestors(bool rootIsFile, bool ownedProfile)
    {
        using var h = new ProfileCredentialTests.Harness();
        var file = Path.Combine(h.Directory, "not-a-directory");
        File.WriteAllText(file, "unchanged");
        var root = rootIsFile ? file : Path.Combine(file, "missing", "config");
        if (ownedProfile)
            Assert.Throws<IOException>(() => new OwnedProfileFiles(root).EnsureDirectory());
        else
        {
            var backend = LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(root);
            Assert.Throws<CacheProtectionUnavailableException>(() => backend.AcquireLease(default));
        }
        Assert.Equal("unchanged", File.ReadAllText(file));
        Assert.Single(Directory.GetFileSystemEntries(h.Directory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CredentialFilesRejectReparseAncestorBeforeCreatingMissingDirectories(bool missingChild, bool ownedProfile)
    {
        using var h = new ProfileCredentialTests.Harness();
        var target = Path.Combine(h.Directory, "target");
        var link = Path.Combine(h.Directory, "link");
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null",
                },
            })!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            var root = missingChild ? Path.Combine(link, "missing", "config") : link;
            if (ownedProfile)
                Assert.Throws<AuthProfileStateException>(() => new OwnedProfileFiles(root).EnsureDirectory());
            else
            {
                var backend = LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(root);
                Assert.Throws<AuthProfileStateException>(() => backend.AcquireLease(default));
            }
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied")]
    [InlineData("org.freedesktop.DBus.Error.InvalidArgs")]
    [InlineData("org.freedesktop.DBus.Error.InvalidSignature")]
    [InlineData("org.freedesktop.Secret.Error.TOP_SECRET")]
    [InlineData("org.freedesktop.DBus.Error.NoReply")]
    [InlineData(null)]
    public async Task GioErrorsNeverDowngradeProtection(string? remoteError)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport
        {
            OpenFailure = new NativeStorageException(GioSecretServiceTransport.ClassifyRemoteError(remoteError)),
        };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using var lease = backend.AcquireLease(default);
        var error = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(default));
        Assert.Equal(12006, error.ErrorCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("TOP_SECRET", error.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<AuthProfileStateException>(() => backend.WriteAsync("must-not-write"u8.ToArray(), default));
        Assert.Equal(0, plain.Reads);
        Assert.Equal(0, plain.Writes);
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown")]
    [InlineData("org.freedesktop.DBus.Error.NameHasNoOwner")]
    public async Task GioKnownAbsenceAllowsOptedInFallback(string remoteError)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport
        {
            OpenFailure = new NativeStorageException(GioSecretServiceTransport.ClassifyRemoteError(remoteError)),
        };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using var lease = backend.AcquireLease(default);
        Assert.Equal(plain.Value, await backend.ReadAsync(default));
        await backend.WriteAsync("rotated-cache"u8.ToArray(), default);
        Assert.Equal(1, plain.Reads);
        Assert.Equal(1, plain.Writes);
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown")]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied")]
    public async Task GioMutationFailureNeverReplaysPlaintext(string remoteError)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport
        {
            WriteFailure = new NativeStorageException(GioSecretServiceTransport.ClassifyRemoteError(remoteError)),
        };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using var lease = backend.AcquireLease(default);
        Assert.Equal("private-cache"u8.ToArray(), await backend.ReadAsync(default));
        var error = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.WriteAsync("changed-cache"u8.ToArray(), default));
        Assert.Equal(12006, error.ErrorCode);
        Assert.Equal(1, transport.Methods.Count(method => method == "SetSecret"));
        Assert.Equal(0, plain.Reads);
        Assert.Equal(0, plain.Writes);
    }

    [Fact]
    public async Task MissingProtectedItemReadsOptedInPlaintextAndKeepsHealthyProtectedWrites()
    {
        using var h = new ProfileCredentialTests.Harness();
        var plain = LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(h.Directory);
        var existing = "existing-plaintext-cache"u8.ToArray();
        var transport = new LinuxTransport { MissingItem = true };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using (backend.AcquireLease(default))
        {
            await plain.WriteAsync(existing, default);
            Assert.Equal(existing, await backend.ReadAsync(default));
            await backend.WriteAsync("rotated-cache"u8.ToArray(), default);
            Assert.Equal(existing, await plain.ReadAsync(default));
        }
        Assert.Contains("CreateItem", transport.Methods);
        Assert.DoesNotContain("GetSecret", transport.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaintextPermissionIsOnlyAProtectedReadAvailabilityFallback(bool unavailable)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport { OpenFailure = unavailable ? new NativeStorageException(NativeStorageFailure.Unavailable) : null };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using (backend.AcquireLease(default))
        {
            Assert.Equal(unavailable ? plain.Value : "private-cache"u8.ToArray(), await backend.ReadAsync(default));
            await backend.WriteAsync("rotated-cache"u8.ToArray(), default);
        }
        Assert.Equal(unavailable ? 0 : 1, transport.Methods.Count(m => m == "SetSecret"));
        Assert.Equal(unavailable ? 1 : 0, plain.Writes);
        Assert.Equal(unavailable ? 1 : 0, plain.Reads);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("schema")]
    [InlineData("base64")]
    [InlineData("unclassified")]
    [InlineData("io")]
    [InlineData("construction")]
    public async Task InvalidOrUnclassifiedProtectedReadNeverFallsBackToPlaintext(string kind)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport
        {
            Mismatch = kind == "schema", CorruptValue = kind == "base64",
            OpenFailure = kind switch
            {
                "malformed" => new NativeStorageException(NativeStorageFailure.Malformed),
                "unclassified" => new CacheProtectionUnavailableException("synthetic-unclassified"),
                "io" => new IOException("synthetic-io"),
                _ => null,
            },
        };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(
            () => kind == "construction" ? throw new CacheProtectionUnavailableException("synthetic-construction") : native, plain);
        using var lease = backend.AcquireLease(default);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(default));
        await Assert.ThrowsAsync<AuthProfileStateException>(() => backend.WriteAsync("must-not-write"u8.ToArray(), default));
        Assert.Equal(0, plain.Reads);
        Assert.Equal(0, plain.Writes);
        Assert.DoesNotContain("SetSecret", transport.Methods);
        Assert.DoesNotContain("CreateItem", transport.Methods);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("prompt-required")]
    [InlineData("missing-protection")]
    [InlineData("library-unavailable")]
    public async Task OnlyKnownNativeProtectionUnavailabilityPermitsOptedInFallback(string kind)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport
        {
            MissingCollection = kind == "missing-protection",
            OpenFailure = kind switch
            {
                "locked" => new NativeStorageException(NativeStorageFailure.Locked),
                "prompt-required" => new NativeStorageException(NativeStorageFailure.PromptRequired),
                "library-unavailable" => new DllNotFoundException(),
                _ => null,
            },
        };
        var native = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using var lease = backend.AcquireLease(default);
        Assert.Equal(plain.Value, await backend.ReadAsync(default));
        await backend.WriteAsync("rotated-cache"u8.ToArray(), default);
        Assert.Equal(1, plain.Reads);
        Assert.Equal(1, plain.Writes);
        Assert.DoesNotContain("SetSecret", transport.Methods);
        Assert.DoesNotContain("CreateItem", transport.Methods);
    }

    [Fact]
    public async Task ProtectedWriteUncertaintyNeverReplaysMutationIntoPlaintext()
    {
        var native = new Cache { FailWrite = true };
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        using var lease = backend.AcquireLease(default);
        await backend.ReadAsync(default);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.WriteAsync("changed-cache"u8.ToArray(), default));
        Assert.Equal(0, plain.Writes);
        Assert.Equal(0, plain.Reads);
    }

    [Fact]
    public async Task ClearAttemptsBothDedicatedLegacyVariantsAndReportsLockedProtectedData()
    {
        var native = new Cache { Unavailable = true };
        var plain = new Cache();
        var backend = new ProtectedFirstLegacyCache(() => native, plain);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ClearAsync(default));
        Assert.Equal(1, native.Clears);
        Assert.Equal(1, plain.Clears);
    }

    [Fact]
    public async Task LegacyWindowsUsesReleasedMsalEntropylessWireFormatWithoutNativeProbe()
    {
        using var h = new ProfileCredentialTests.Harness();
        var protector = new Protector();
        var backend = LegacyMsalTokenCacheBackendFactory.Windows(h.Directory, protector);
        var value = Encoding.UTF8.GetBytes("synthetic-private-cache");
        using (backend.AcquireLease(default))
        {
            await backend.WriteAsync(value, default);
            Assert.Equal(value, await backend.ReadAsync(default));
        }
        Assert.Equal(2, protector.Calls);
        Assert.All(protector.Entropies, Assert.Empty);
        Assert.Equal(value.Select(b => (byte)(b ^ 0x5a)), await File.ReadAllBytesAsync(AgentHostCacheDescriptor.Create(h.Directory).CacheFilePath));
        await backend.ClearAsync(default);
        Assert.False(File.Exists(AgentHostCacheDescriptor.Create(h.Directory).CacheFilePath));
    }

    [Fact]
    public async Task ExplicitUnprotectedLegacyTokensAreBoundToOnlyKnownMsagentFile()
    {
        using var h = new ProfileCredentialTests.Harness();
        var backend = LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(h.Directory);
        using (backend.AcquireLease(default))
        {
            await backend.WriteAsync(Encoding.UTF8.GetBytes("synthetic-token-cache"), default);
            Assert.Equal("synthetic-token-cache", Encoding.UTF8.GetString((await backend.ReadAsync(default))!));
        }
        Assert.Empty(h.Backend.Values);
        await backend.ClearAsync(default);
        Assert.False(File.Exists(AgentHostCacheDescriptor.Create(h.Directory).CacheFilePath));
    }

    [Fact]
    public async Task LegacyMacExactMsagentNamespaceReadUpdateClearRequireAlreadyEstablishedNoUiPolicy()
    {
        using var h = new ProfileCredentialTests.Harness();
        var native = new Mac();
        var policy = MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default);
        var backend = LegacyMsalTokenCacheBackendFactory.MacOs(h.Directory, policy);
        await backend.WriteAsync(Encoding.UTF8.GetBytes("private-cache"), default);
        Assert.Equal("private-cache", Encoding.UTF8.GetString((await backend.ReadAsync(default))!));
        Assert.All(native.Names, name => Assert.Equal(("agenthost_cli_csharp_service", "agenthost_cli_csharp_account"), name));
        native.Locked = true;
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ClearAsync(default));
        Assert.NotNull(native.Value);
        native.Locked = false;
        await backend.ClearAsync(default);
        Assert.Null(native.Value);
        Assert.Equal(1, native.PolicySets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyLinuxRequiresExactSchemaBeforeReadingOrDeletingAndNeverRequestsUnlock(bool mismatched)
    {
        using var h = new ProfileCredentialTests.Harness();
        var transport = new LinuxTransport { Mismatch = mismatched };
        var backend = LegacyMsalTokenCacheBackendFactory.Linux(h.Directory, transport);
        if (mismatched)
        {
            await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(default));
            Assert.DoesNotContain("GetSecret", transport.Methods);
            Assert.DoesNotContain("Delete", transport.Methods);
        }
        else
        {
            Assert.Equal("private-cache", Encoding.UTF8.GetString((await backend.ReadAsync(default))!));
            await backend.ClearAsync(default);
            Assert.Contains("Delete", transport.Methods);
        }
        Assert.DoesNotContain("Unlock", transport.Methods);
        Assert.DoesNotContain("Prompt", transport.Methods);
        Assert.DoesNotContain("CreateCollection", transport.Methods);
        Assert.Equal("com.microsoft.agenthost.cli.csharp", transport.SearchAttributes!["xdg:schema"]);
    }

    [Fact]
    public void LegacyFileLeaseFailsBoundedlyAcrossBackendInstances()
    {
        using var h = new ProfileCredentialTests.Harness();
        var one = LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(h.Directory);
        using var lease = one.AcquireLease(default);
        Assert.Throws<CacheProtectionUnavailableException>(() => LegacyMsalTokenCacheBackendFactory.CreateUnprotectedTokenCache(h.Directory).AcquireLease(default));
    }

    private sealed class Cache : ILegacyMsalTokenCacheBackend
    {
        internal byte[] Value = Guid.NewGuid().ToByteArray();
        internal bool Unavailable, FailWrite;
        internal int Reads, Writes, Clears;
        public IDisposable AcquireLease(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return new Handle(); }
        public Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Reads++;
            if (Unavailable) throw new CacheProtectionUnavailableException("synthetic-unavailable");
            return Task.FromResult<byte[]?>(Value.ToArray());
        }
        public Task WriteAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Writes++; Value = value.ToArray();
            if (FailWrite) throw new CacheProtectionUnavailableException("synthetic-indeterminate-write");
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Clears++;
            if (Unavailable) throw new CacheProtectionUnavailableException("synthetic-unavailable");
            Value = []; return Task.CompletedTask;
        }
    }

    private sealed class Protector : IDataProtector
    {
        internal int Calls;
        internal List<byte[]> Entropies { get; } = [];
        public byte[] Protect(byte[] data, byte[] entropy) { Calls++; Entropies.Add(entropy); return data.Select(b => (byte)(b ^ 0x5a)).ToArray(); }
        public byte[] Unprotect(byte[] data, byte[] entropy) => Protect(data, entropy);
    }

    private sealed class Handle : IMacOwnedHandle { public void Dispose() { } }
    private sealed class Mac : IMacOsKeychainApi
    {
        internal byte[]? Value;
        internal bool Locked;
        internal int PolicySets;
        internal List<(string Service, string Account)> Names { get; } = [];
        public int SetInteractionAllowed(bool allowed) { Assert.False(allowed); PolicySets++; return 0; }
        public int GetInteractionAllowed(out bool allowed) { allowed = false; return 0; }
        public int CopyDefaultKeychain(out IMacOwnedHandle? keychain) { keychain = new Handle(); return 0; }
        public int GetStatus(IMacOwnedHandle keychain, out uint status) { status = Locked ? 0u : 1u; return 0; }
        public int Find(IMacOwnedHandle keychain, string service, string account, bool readData, out byte[]? data, out IMacOwnedHandle? item)
        {
            Names.Add((service, account)); data = readData ? Value?.ToArray() : null; item = Value is null ? null : new Handle();
            return Value is null ? -25300 : 0;
        }
        public int Add(IMacOwnedHandle keychain, string service, string account, byte[] data) { Names.Add((service, account)); Value = data.ToArray(); return 0; }
        public int Modify(IMacOwnedHandle item, byte[] data) { Value = data.ToArray(); return 0; }
        public int Delete(IMacOwnedHandle item) { Value = null; return 0; }
    }

    private sealed class LinuxTransport : ISecretServiceTransportFactory, ISecretServiceTransport
    {
        private const string Collection = "/org/freedesktop/secrets/collection/default";
        internal bool Mismatch, MissingItem, MissingCollection, CorruptValue;
        internal Exception? OpenFailure, WriteFailure;
        internal List<string> Methods { get; } = [];
        internal Dictionary<string, string>? SearchAttributes;
        public ISecretServiceTransport Open(CancellationToken ct)
        {
            if (OpenFailure is { } failure) throw failure;
            return this;
        }
        public DbusValue Call(string path, string interfaceName, string method, DbusValue parameters, string replySignature, CancellationToken ct)
        {
            Methods.Add(method);
            if (method == "ReadAlias") return DbusValue.Tuple(DbusValue.Path(MissingCollection ? "/" : Collection));
            if (method == "SearchItems")
            {
                var fields = (DbusValue[])((DbusValue[])parameters.Data)[0].Data;
                SearchAttributes = fields.Select(e => (DbusValue[])e.Data).ToDictionary(e => (string)e[0].Data, e => (string)e[1].Data);
                return DbusValue.Tuple(MissingItem ? DbusValue.Array("o") : DbusValue.Array("o", DbusValue.Path(Collection + "/item")), DbusValue.Array("o"));
            }
            if (method == "Get")
            {
                var property = (string)((DbusValue[])parameters.Data)[1].Data;
                if (property == "Locked") return DbusValue.Tuple(DbusValue.Variant(DbusValue.Boolean(false)));
                return DbusValue.Tuple(DbusValue.Variant(DbusValue.Array("{ss}",
                    DbusValue.Entry(DbusValue.Text("xdg:schema"), DbusValue.Text(Mismatch ? "pac-schema" : "com.microsoft.agenthost.cli.csharp")),
                    DbusValue.Entry(DbusValue.Text("Version"), DbusValue.Text("1")),
                    DbusValue.Entry(DbusValue.Text("CacheKind"), DbusValue.Text("MSAL_Token_Cache")))));
            }
            if (method == "OpenSession") return DbusValue.Tuple(DbusValue.Variant(DbusValue.Text("")), DbusValue.Path("/session"));
            if (method == "GetSecret") return DbusValue.Tuple(DbusValue.Tuple(DbusValue.Path("/session"), DbusValue.Bytes([]),
                DbusValue.Bytes(Encoding.UTF8.GetBytes(CorruptValue ? "!not-base64!" : Convert.ToBase64String(Encoding.UTF8.GetBytes("private-cache")))), DbusValue.Text("text/plain; charset=utf8")));
            if (method == "Delete") return DbusValue.Tuple(DbusValue.Path("/"));
            if (method == "CreateItem") return DbusValue.Tuple(DbusValue.Path(Collection + "/item"), DbusValue.Path("/"));
            if (method == "SetSecret")
            {
                if (WriteFailure is { } failure) throw failure;
                return DbusValue.Tuple();
            }
            throw new InvalidOperationException(method);
        }
        public void Dispose() { }
    }
}
