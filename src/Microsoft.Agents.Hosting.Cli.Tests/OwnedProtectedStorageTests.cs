using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class OwnedProtectedStorageTests
{
    private const string Id = "112233445566778899aabbccddeeff00";
    private static OwnedProtectedStorageReference Reference(OwnedProtectedStorageKind kind = OwnedProtectedStorageKind.Credential) => new(kind, Id);

    [Theory]
    [InlineData("unavailable", "service-unavailable", true)]
    [InlineData("locked", "store-locked", true)]
    [InlineData("prompt", "prompt-required", true)]
    [InlineData("malformed", "malformed-response", false)]
    [InlineData("unknown", "native-failure", false)]
    [InlineData("not-found", "native-object-not-found", false)]
    [InlineData("library", "native-library-unavailable", true)]
    [InlineData("entry-point", "native-entry-point-unavailable", true)]
    [InlineData("access", "access-denied", false)]
    [InlineData("io", "io-failure", false)]
    public async Task LinuxFailuresIdentifySafeCategoryWithoutChangingFallbackEligibility(string problem, string category, bool fallback)
    {
        Exception cause = problem switch
        {
            "unavailable" => new NativeStorageException(NativeStorageFailure.Unavailable),
            "locked" => new NativeStorageException(NativeStorageFailure.Locked),
            "prompt" => new NativeStorageException(NativeStorageFailure.PromptRequired),
            "malformed" => new NativeStorageException(NativeStorageFailure.Malformed),
            "unknown" => new NativeStorageException(NativeStorageFailure.Unknown),
            "not-found" => new NativeStorageException(NativeStorageFailure.NotFound),
            "library" => new DllNotFoundException("TOP_SECRET"),
            "entry-point" => new EntryPointNotFoundException("TOP_SECRET"),
            "access" => new UnauthorizedAccessException("TOP_SECRET"),
            _ => new IOException("TOP_SECRET"),
        };
        var bus = new FakeBus { Override = _ => throw cause };
        var failure = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default));
        Assert.Contains($"Linux: {category}; operation: read", failure.Message, StringComparison.Ordinal);
        Assert.Equal(fallback, Protection.AllowsTokenFallback(failure));
        AssertRedacted(failure);
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData("read", "GetSecret", "get-secret")]
    [InlineData("write", "SetSecret", "set-secret")]
    [InlineData("clear", "Delete", "delete")]
    public async Task LinuxNativeFailuresIdentifyTheProtocolStep(string operation, string method, string step)
    {
        var bus = new FakeBus
        {
            Override = request => request.Method == method ? throw new NativeStorageException(NativeStorageFailure.Unknown) : null,
        };
        var failure = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            Execute(new LinuxOwnedProtectedStorage(bus), operation));
        Assert.Contains($"operation: {operation}; step: {step}", failure.Message, StringComparison.Ordinal);
        AssertRedacted(failure);
    }

    [Fact]
    public async Task LinuxMalformedRepliesIdentifyTheProtocolStepWithoutPrintingReplyData()
    {
        var bus = new FakeBus { Override = request => request.Method == "OpenSession" ? DbusValue.Text("TOP_SECRET") : null };
        var failure = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default));
        Assert.Contains("Linux: malformed-response; operation: read; step: open-session", failure.Message, StringComparison.Ordinal);
        AssertRedacted(failure);
    }

    [Theory]
    [InlineData("read", "GetSecret")]
    [InlineData("write", "SetSecret")]
    [InlineData("clear", "Delete")]
    public async Task GioUnknownErrorsRemainRedactedProtectionFailures(string operation, string method)
    {
        var failure = GioSecretServiceTransport.ClassifyRemoteError("org.freedesktop.Secret.Error.TOP_SECRET");
        var bus = new FakeBus
        {
            Override = request => request.Method == method ? throw new NativeStorageException(failure) : null,
        };
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            Execute(new LinuxOwnedProtectedStorage(bus), operation)));
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("112233445566778899AABBCCDDEEFF00")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("112233445566778899aabbccddeeff0/")]
    [InlineData("112233445566778899aabbccddeeff0\0")]
    public void ReferencesRejectNoncanonicalOrPathInjectedIdsBeforeProviderWork(string id)
    {
        var native = new FakeMac();
        Assert.Throws<ArgumentException>(() => new OwnedProtectedStorageReference(OwnedProtectedStorageKind.Credential, id));
        Assert.Throws<ArgumentException>(() => new OwnedProtectedStorageReference((OwnedProtectedStorageKind)99, Id));
        Assert.Empty(native.Calls);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("mac")]
    [InlineData("linux")]
    public async Task NullAndCanceledStartsMakeZeroProviderCalls(string platform)
    {
        using var dir = new TempDir();
        var crypto = new FakeProtector();
        var mac = new FakeMac();
        var bus = new FakeBus();
        IOwnedProtectedStorageBackend backend = platform switch
        {
            "windows" => new WindowsOwnedProtectedStorage(dir.Path, crypto),
            "mac" => new MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy.EstablishForCliProcess(mac, default)),
            _ => new LinuxOwnedProtectedStorage(bus),
        };
        mac.Calls.Clear();
        await Assert.ThrowsAsync<ArgumentNullException>(() => backend.ReadAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => backend.WriteAsync(null!, new byte[] { 7 }, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => backend.ClearAsync(null!, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ReadAsync(Reference(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.WriteAsync(Reference(), new byte[] { 7 }, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ClearAsync(Reference(), canceled.Token));
        Assert.Equal(0, crypto.Calls);
        Assert.Empty(mac.Calls);
        Assert.Equal(0, bus.Opens);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void CanceledFactoryAndPolicyDoNotInitializeProviders()
    {
        var native = new FakeMac();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MacOsKeychainProcessPolicy.EstablishForCliProcess(native, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => OwnedProtectedStorageBackendFactory.Create("ignored", ct: canceled.Token));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task WindowsRoundTripUsesBoundProtectedAtomicOwnerOnlyFiles()
    {
        using var dir = new TempDir();
        var crypto = new FakeProtector();
        var backend = new WindowsOwnedProtectedStorage(dir.Path, crypto);
        byte[] input = [31, 41, 59, 26];
        await backend.WriteAsync(Reference(), input, default);
        await backend.WriteAsync(Reference(OwnedProtectedStorageKind.TokenCache), new byte[] { 23 }, default);
        Assert.Equal(input, await backend.ReadAsync(Reference(), default));
        Assert.Equal(new byte[] { 23 }, await backend.ReadAsync(Reference(OwnedProtectedStorageKind.TokenCache), default));
        Assert.Equal(new byte[] { 31, 41, 59, 26 }, input);
        Assert.All(crypto.OwnedInputs, b => Assert.All(b, value => Assert.Equal(0, value)));
        var files = Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.Contains(files, p => System.IO.Path.GetFileName(p) == $"credential-{Id}.bin");
        Assert.Contains(files, p => System.IO.Path.GetFileName(p) == $"token-cache-{Id}.bin");
        Assert.All(files, p => Assert.False(File.ReadAllBytes(p).AsSpan().IndexOf(input) >= 0));
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in files)
            {
                var acl = new FileInfo(file).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                Assert.Single(acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>());
            }
        }

        await backend.ClearAsync(Reference(), default);
        await backend.ClearAsync(Reference(), default);
        Assert.Null(await backend.ReadAsync(Reference(), default));
        Assert.Equal(new byte[] { 23 }, await backend.ReadAsync(Reference(OwnedProtectedStorageKind.TokenCache), default));
    }

    [Fact]
    public async Task WindowsProtectionFailurePreservesPreviouslyCommittedValueAndRedactsError()
    {
        using var dir = new TempDir();
        var crypto = new FakeProtector();
        var backend = new WindowsOwnedProtectedStorage(dir.Path, crypto);
        await backend.WriteAsync(Reference(), new byte[] { 7 }, default);
        crypto.FailProtect = true;
        var failure = await Assert.ThrowsAsync<CacheProtectionUnavailableException>(
            () => backend.WriteAsync(Reference(), new byte[] { 9 }, default));
        AssertRedacted(failure);
        crypto.FailProtect = false;
        Assert.Equal(new byte[] { 7 }, await backend.ReadAsync(Reference(), default));
        Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WindowsCorruptionAndIoFailureAreNotMissingCaches()
    {
        using var dir = new TempDir();
        var backend = new WindowsOwnedProtectedStorage(dir.Path, new FakeProtector());
        await backend.WriteAsync(Reference(), new byte[] { 7 }, default);
        string file = Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(Reference(), default)));
        File.Delete(file);
        Directory.CreateDirectory(file);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(Reference(), default));
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ClearAsync(Reference(), default));
    }

    [Fact]
    public async Task WindowsCancellationAfterCryptoDoesNotPublishOrEraseCallerInput()
    {
        using var dir = new TempDir();
        using var canceled = new CancellationTokenSource();
        var crypto = new FakeProtector { AfterProtect = canceled.Cancel };
        var backend = new WindowsOwnedProtectedStorage(dir.Path, crypto);
        byte[] input = [7];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.WriteAsync(Reference(), input, canceled.Token));
        Assert.Equal(new byte[] { 7 }, input);
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
        Assert.All(crypto.OwnedInputs, b => Assert.All(b, v => Assert.Equal(0, v)));
    }

    [Fact]
    public void MacPolicySetsFalseOnceAndNeverRepairsOrRestores()
    {
        var native = new FakeMac();
        var first = MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default);
        Assert.Same(first, MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        first.Ensure(default);
        Assert.Single(native.Calls, c => c == "set:false");
        Assert.DoesNotContain("set:true", native.Calls);
        native.UiAllowed = true;
        AssertRedacted(Assert.Throws<CacheProtectionUnavailableException>(() => first.Ensure(default)));
        Assert.Throws<CacheProtectionUnavailableException>(() => MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        Assert.Single(native.Calls, c => c == "set:false");
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, -50)]
    public void MacFailedPolicyEstablishmentIsRedactedAndNeverProducesACapability(int setStatus, int getStatus)
    {
        var native = new FakeMac { SetStatus = setStatus, PolicyReadStatus = getStatus };
        AssertRedacted(Assert.Throws<CacheProtectionUnavailableException>(
            () => MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default)));
        Assert.Throws<CacheProtectionUnavailableException>(() => MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        Assert.Single(native.Calls, c => c == "set:false");
    }

    [Fact]
    public void MacBackendWithoutOptInFailsBeforeNativeWork() =>
        AssertRedacted(Assert.Throws<CacheProtectionUnavailableException>(() => new MacOsOwnedProtectedStorage(null)));

    [Fact]
    public async Task MacReadWriteDeleteCheckPolicyAndOnlyAddressExactOwnedGenericPassword()
    {
        var native = new FakeMac();
        var backend = new MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        native.Calls.Clear();
        Assert.Equal(new byte[] { 7, 8 }, await backend.ReadAsync(Reference(), default));
        await backend.WriteAsync(Reference(OwnedProtectedStorageKind.TokenCache), new byte[] { 9 }, default);
        await backend.ClearAsync(Reference(), default);
        Assert.Contains($"find:com.microsoft.msagent.owned.v1.credential:{Id}", native.Calls);
        Assert.Contains($"find:com.microsoft.msagent.owned.v1.token-cache:{Id}", native.Calls);
        Assert.Equal(1, native.Modifies);
        Assert.Equal(1, native.Deletes);
        for (int i = 0; i < native.Calls.Count; i++)
        {
            if (native.Calls[i] is "default" or "status" or "modify" or "delete" || native.Calls[i].StartsWith("find:", StringComparison.Ordinal))
            {
                Assert.Equal("policy", native.Calls[i - 1]);
                Assert.Equal("policy", native.Calls[i + 1]);
            }
        }
        Assert.All(native.Handles, h => Assert.True(h.Disposed));
        Assert.All(native.WriteBuffer!, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task MacMissingItemIsNullOrIdempotentAndWriteUsesAddWithoutUnlock()
    {
        var native = new FakeMac { FindStatus = -25300 };
        var backend = new MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        Assert.Null(await backend.ReadAsync(Reference(), default));
        await backend.ClearAsync(Reference(), default);
        await backend.WriteAsync(Reference(), new byte[] { 9 }, default);
        Assert.Equal(1, native.Adds);
        Assert.Equal(0, native.Modifies);
        Assert.Equal(0, native.Deletes);
        Assert.All(native.Handles, h => Assert.True(h.Disposed));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("missing-default")]
    [InlineData("reenabled")]
    [InlineData("provider-error")]
    [InlineData("malformed")]
    public async Task MacReadinessPolicyErrorsAndMalformedSuccessNeverBecomeNull(string problem)
    {
        var native = new FakeMac();
        var policy = MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default);
        var backend = new MacOsOwnedProtectedStorage(policy);
        if (problem == "locked") native.KeychainStatus = 0;
        if (problem == "missing-default") native.DefaultStatus = -25300;
        if (problem == "reenabled") native.UiAllowed = true;
        if (problem == "provider-error") native.FindStatus = -25308;
        if (problem == "malformed") native.MalformedFind = true;
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(Reference(), default)));
        if (problem is "locked" or "missing-default" or "reenabled") Assert.DoesNotContain(native.Calls, c => c.StartsWith("find:", StringComparison.Ordinal));
        Assert.All(native.Handles, h => Assert.True(h.Disposed));
    }

    [Fact]
    public async Task MacCanceledNativeReadClearsReturnedBytesAndReleasesHandles()
    {
        using var canceled = new CancellationTokenSource();
        var native = new FakeMac { AfterFind = canceled.Cancel };
        var backend = new MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ReadAsync(Reference(), canceled.Token));
        Assert.All(native.ReturnBuffer!, b => Assert.Equal(0, b));
        Assert.All(native.Handles, h => Assert.True(h.Disposed));
    }

    [Theory]
    [InlineData("modify")]
    [InlineData("delete")]
    [InlineData("add")]
    public async Task MacMutationFailuresAreTypedAndRedacted(string operation)
    {
        var native = new FakeMac { MutationStatus = -25308, FindStatus = operation == "add" ? -25300 : 0 };
        var backend = new MacOsOwnedProtectedStorage(MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default));
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            operation == "delete" ? backend.ClearAsync(Reference(), default) : backend.WriteAsync(Reference(), new byte[] { 7 }, default)));
        Assert.All(native.Handles, h => Assert.True(h.Disposed));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("clear")]
    public async Task LinuxLockedItemNeverInvokesUnlockPromptOrMutation(string operation)
    {
        var bus = new FakeBus { LockedItem = true };
        var backend = new LinuxOwnedProtectedStorage(bus);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => Execute(backend, operation));
        Assert.DoesNotContain(bus.Calls, c => c.Method is "Unlock" or "Prompt" or "CreateItem" or "Delete" or "SetSecret");
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinuxLockedOrMissingDefaultCollectionFailsWithoutCreation(bool locked)
    {
        var bus = new FakeBus { LockedDefault = locked, MissingDefault = !locked };
        var backend = new LinuxOwnedProtectedStorage(bus);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.WriteAsync(Reference(), new byte[] { 7 }, default));
        Assert.DoesNotContain(bus.Calls, c => c.Method is "Unlock" or "Prompt" or "CreateCollection" or "CreateItem" or "SearchItems");
    }

    [Theory]
    [InlineData("CreateItem")]
    [InlineData("Delete")]
    public async Task LinuxPromptObjectIsRefusedWithoutInvokingPrompt(string method)
    {
        var bus = new FakeBus { MissingItem = method == "CreateItem", PromptReply = true };
        var backend = new LinuxOwnedProtectedStorage(bus);
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            method == "Delete" ? backend.ClearAsync(Reference(), default) : backend.WriteAsync(Reference(), new byte[] { 7 }, default));
        Assert.Contains(bus.Calls, c => c.Method == method);
        Assert.DoesNotContain(bus.Calls, c => c.Method is "Prompt" or "Unlock");
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task LinuxReadsAndWritesUseExactNamespacesTypedSecretAndDisposeTemporaryBuffers()
    {
        var bus = new FakeBus();
        var backend = new LinuxOwnedProtectedStorage(bus);
        Assert.Equal(new byte[] { 7, 8 }, await backend.ReadAsync(Reference(), default));
        Assert.All(bus.ReturnedSecret!, b => Assert.Equal(0, b));
        await backend.WriteAsync(Reference(OwnedProtectedStorageKind.TokenCache), new byte[] { 9 }, default);
        Assert.Equal(new byte[] { 9 }, bus.SavedSecret);
        var search = bus.Calls.Last(c => c.Method == "SearchItems");
        Assert.Equal("(a{ss})", search.Parameters.Signature);
        Assert.Equal(new Dictionary<string, string>
        {
            ["application"] = "com.microsoft.msagent.owned.v1",
            ["kind"] = "token-cache",
            ["id"] = Id,
        }, ToMap(Children(search.Parameters)[0]));
        var set = bus.Calls.Last(c => c.Method == "SetSecret");
        Assert.Equal("((oayays))", set.Parameters.Signature);
        Assert.All((byte[])Children(Children(set.Parameters)[0])[2].Data, b => Assert.Equal(0, b));
        Assert.DoesNotContain(bus.Calls, c => c.Method is "Unlock" or "Prompt" or "CreateCollection");
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task LinuxCreationAndIdempotentMissingUseOnlyOwnedItem()
    {
        var bus = new FakeBus { MissingItem = true };
        var backend = new LinuxOwnedProtectedStorage(bus);
        Assert.Null(await backend.ReadAsync(Reference(), default));
        await backend.ClearAsync(Reference(), default);
        Assert.DoesNotContain(bus.Calls, c => c.Method == "Delete");
        await backend.WriteAsync(Reference(), new byte[] { 7 }, default);
        var create = bus.Calls.Single(c => c.Method == "CreateItem");
        Assert.Equal(FakeBus.Collection, create.Path);
        Assert.Equal("(a{sv}(oayays)b)", create.Parameters.Signature);
        Assert.Equal(new byte[] { 7 }, bus.SavedSecret);
    }

    [Theory]
    [InlineData(OwnedProtectedStorageKind.Credential, "read")]
    [InlineData(OwnedProtectedStorageKind.Credential, "write")]
    [InlineData(OwnedProtectedStorageKind.Credential, "clear")]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "read")]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "write")]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "clear")]
    public async Task LinuxAcceptsGnomeGenericSchemaWithoutChangingOwnedCoordinates(
        OwnedProtectedStorageKind kind, string operation)
    {
        var bus = new FakeBus { AddGenericSchema = true };
        var backend = new LinuxOwnedProtectedStorage(bus);
        if (operation == "read")
        {
            Assert.Equal(new byte[] { 7, 8 }, await backend.ReadAsync(Reference(kind), default));
            Assert.All(bus.ReturnedSecret!, value => Assert.Equal(0, value));
        }
        else if (operation == "write")
        {
            await backend.WriteAsync(Reference(kind), new byte[] { 7 }, default);
            Assert.Equal(new byte[] { 7 }, bus.SavedSecret);
        }
        else
        {
            await backend.ClearAsync(Reference(kind), default);
            Assert.Contains(bus.Calls, call => call.Method == "Delete");
        }
        var search = bus.Calls.Single(call => call.Method == "SearchItems");
        Assert.Equal(new Dictionary<string, string>
        {
            ["application"] = "com.microsoft.msagent.owned.v1",
            ["kind"] = Reference(kind).KindName,
            ["id"] = Id,
        }, ToMap(Children(search.Parameters)[0]));
        Assert.DoesNotContain(bus.Calls, call => call.Method is "Unlock" or "Prompt" or "CreateItem");
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData("wrong", "application")]
    [InlineData("wrong", "kind")]
    [InlineData("wrong", "id")]
    [InlineData("wrong", "xdg:schema")]
    [InlineData("missing", "application")]
    [InlineData("missing", "kind")]
    [InlineData("missing", "id")]
    [InlineData("duplicate", "application")]
    [InlineData("duplicate", "kind")]
    [InlineData("duplicate", "id")]
    [InlineData("duplicate", "xdg:schema")]
    [InlineData("unknown", "xdg:schema")]
    [InlineData("extra", "unexpected")]
    public async Task LinuxGenericSchemaCannotBypassOwnedAttributeValidation(string malformed, string key)
    {
        foreach (string operation in new[] { "read", "write", "clear" })
        {
            var attributes = new List<KeyValuePair<string, string>>
            {
                new("application", "com.microsoft.msagent.owned.v1"),
                new("kind", "credential"),
                new("id", Id),
                new("xdg:schema", "org.freedesktop.Secret.Generic"),
            };
            int index = attributes.FindIndex(pair => pair.Key == key);
            if (malformed == "wrong") attributes[index] = new(key, "TOP_SECRET");
            else if (malformed == "missing") attributes.RemoveAt(index);
            else if (malformed == "duplicate") attributes[index == attributes.Count - 1 ? 0 : attributes.Count - 1] = attributes[index];
            else if (malformed == "unknown") attributes[index] = new("unexpected", attributes[index].Value);
            else attributes.Add(new(key, "TOP_SECRET"));
            var bus = new FakeBus
            {
                Override = request => request.Method == "Get" && (string)Children(request.Parameters)[1].Data == "Attributes"
                    ? DbusValue.Tuple(DbusValue.Variant(DbusValue.Array("{ss}", attributes.Select(pair =>
                        DbusValue.Entry(DbusValue.Text(pair.Key), DbusValue.Text(pair.Value))).ToArray())))
                    : null,
            };
            AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
                Execute(new LinuxOwnedProtectedStorage(bus), operation)));
            Assert.DoesNotContain(bus.Calls, call => call.Method is
                "OpenSession" or "GetSecret" or "SetSecret" or "CreateItem" or "Delete" or "Unlock" or "Prompt");
            Assert.True(bus.Disposed);
        }
    }

    [Theory]
    [InlineData(OwnedProtectedStorageKind.Credential, "text/plain", false)]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "text/plain", false)]
    [InlineData(OwnedProtectedStorageKind.Credential, "application/octet-stream", false)]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "application/octet-stream", false)]
    [InlineData(OwnedProtectedStorageKind.Credential, "text/plain", true)]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "text/plain", true)]
    [InlineData(OwnedProtectedStorageKind.Credential, "application/octet-stream", true)]
    [InlineData(OwnedProtectedStorageKind.TokenCache, "application/octet-stream", true)]
    public async Task LinuxReadsBinarySecretBytesUnchangedWhenGnomeKeyringReportsTextPlain(
        OwnedProtectedStorageKind kind, string contentType, bool addGenericSchema)
    {
        byte[] expected = [0, 0xff, 0x80, 0xc3, 0x28, 65, 61, 0];
        byte[] providerBytes = expected.ToArray();
        var bus = new FakeBus
        {
            AddGenericSchema = addGenericSchema,
            Override = request => request.Method == "GetSecret"
                ? DbusValue.Tuple(DbusValue.Tuple(DbusValue.Path(FakeBus.Session), DbusValue.Bytes([]),
                    DbusValue.Bytes(providerBytes), DbusValue.Text(contentType)))
                : null,
        };
        var result = await new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(kind), default);
        Assert.Equal(expected, result);
        Assert.All(providerBytes, value => Assert.Equal(0, value));
        Assert.DoesNotContain(bus.Calls, call => call.Method is "Unlock" or "Prompt" or "CreateItem" or "SetSecret" or "Delete");
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData("ReadAlias")]
    [InlineData("Get")]
    [InlineData("SearchItems")]
    [InlineData("OpenSession")]
    [InlineData("GetSecret")]
    public async Task LinuxMalformedRepliesCannotBecomeEmptyCache(string method)
    {
        var bus = new FakeBus { Override = request => request.Method == method ? DbusValue.Text("TOP_SECRET") : null };
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(
            () => new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default)));
        Assert.True(bus.Disposed);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("clear")]
    public async Task LinuxRawProviderFailuresStayRedacted(string operation)
    {
        var bus = new FakeBus { Override = _ => throw new IOException("TOP_SECRET") };
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            Execute(new LinuxOwnedProtectedStorage(bus), operation)));
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task LinuxNativeNotFoundOnReadIsNullButLockedOrOtherFailuresAreNot()
    {
        var bus = new FakeBus { Override = r => r.Method == "GetSecret" ? throw new NativeStorageException(NativeStorageFailure.NotFound) : null };
        Assert.Null(await new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default));
        bus.Override = r => r.Method == "GetSecret" ? throw new NativeStorageException(NativeStorageFailure.Locked) : null;
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default));
    }

    [Fact]
    public async Task LinuxCanceledReadReleasesRepliesAndConnectionWithoutReturningSecret()
    {
        using var canceled = new CancellationTokenSource();
        var bus = new FakeBus { AfterSecretRead = canceled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), canceled.Token));
        Assert.All(bus.ReturnedSecret!, b => Assert.Equal(0, b));
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task LinuxDuplicateOrMismatchedOwnedItemsAreRefusedBeforeMutation()
    {
        var bus = new FakeBus { Override = r => r.Method == "SearchItems"
            ? DbusValue.Tuple(DbusValue.Array("o", DbusValue.Path(FakeBus.Item), DbusValue.Path(FakeBus.Item)), DbusValue.Array("o"))
            : null };
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => new LinuxOwnedProtectedStorage(bus).ClearAsync(Reference(), default));
        Assert.DoesNotContain(bus.Calls, c => c.Method == "Delete");
        bus.Override = r => r.Method == "Get" && (string)Children(r.Parameters)[1].Data == "Attributes"
            ? DbusValue.Tuple(DbusValue.Variant(DbusValue.Array("{ss}")))
            : null;
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => new LinuxOwnedProtectedStorage(bus).ClearAsync(Reference(), default));
        Assert.DoesNotContain(bus.Calls, c => c.Method == "Delete");
    }

    private static async Task Execute(LinuxOwnedProtectedStorage backend, string operation)
    {
        if (operation == "read") _ = await backend.ReadAsync(Reference(), default);
        else if (operation == "write") await backend.WriteAsync(Reference(), new byte[] { 7 }, default);
        else await backend.ClearAsync(Reference(), default);
    }

    [Theory]
    [InlineData("tcp:host=example.invalid,port=1234")]
    [InlineData("autolaunch:")]
    [InlineData("unix:path=/run/user/1000/bus;tcp:host=example.invalid,port=1234")]
    [InlineData(null)]
    public void LinuxConnectionConfigurationNeverAutolaunchesOrUsesRemoteTransport(string? address)
    {
        int connections = 0;
        var factory = new GioSecretServiceTransportFactory(
            name => name == "DBUS_SESSION_BUS_ADDRESS" ? address : null,
            (_, _) => { connections++; return new FakeBus(); });
        AssertRedacted(Assert.Throws<CacheProtectionUnavailableException>(() => factory.Open(default)));
        Assert.Equal(0, connections);
    }

    [Fact]
    public void LinuxNativeDiagnosticsAreRejectedAndCanceledStartsDoNotReadConfiguration()
    {
        int configurationReads = 0;
        int connections = 0;
        var factory = new GioSecretServiceTransportFactory(name =>
        {
            configurationReads++;
            return name == "G_DBUS_DEBUG" ? "message" : "unix:path=/run/user/1000/bus";
        }, (_, _) => { connections++; return new FakeBus(); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => factory.Open(cancellation.Token));
        Assert.Equal(0, configurationReads);
        AssertRedacted(Assert.Throws<CacheProtectionUnavailableException>(() => factory.Open(default)));
        Assert.Equal(0, connections);
    }

    [Fact]
    public void LinuxConnectionUsesTheExactResolvedLocalAddress()
    {
        const string address = "unix:path=/run/user/1000/bus,guid=11111111111111111111111111111111";
        string? connectedAddress = null;
        var factory = new GioSecretServiceTransportFactory(
            name => name == "DBUS_SESSION_BUS_ADDRESS" ? address : null,
            (value, _) => { connectedAddress = value; return new FakeBus(); });
        using var transport = factory.Open(default);
        Assert.Equal(address, connectedAddress);
    }

    [Fact]
    public async Task MacPolicyReenabledDuringFindStopsMutationAndReleasesOwnedHandles()
    {
        var native = new FakeMac();
        var policy = MacOsKeychainProcessPolicy.EstablishForCliProcess(native, default);
        native.AfterFind = () => native.UiAllowed = true;
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            new MacOsOwnedProtectedStorage(policy).WriteAsync(Reference(), new byte[] { 7 }, default));
        Assert.Equal(0, native.Modifies);
        Assert.All(native.Handles, handle => Assert.True(handle.Disposed));
        Assert.Single(native.Calls, call => call == "set:false");
    }

    [Fact]
    public async Task WindowsUnsafePreexistingOwnedDirectoryCannotReadOrPublishProtectedData()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        string owned = Path.Combine(dir.Path, "owned-protected-v1");
        Directory.CreateDirectory(owned);
        var backend = new WindowsOwnedProtectedStorage(dir.Path, new FakeProtector());
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(Reference(), default));
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.WriteAsync(Reference(), new byte[] { 7 }, default));
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ClearAsync(Reference(), default));
        Assert.Empty(Directory.GetFiles(owned));
    }

    [Fact]
    public async Task WindowsMissingCryptoResourceIsProtectionFailureNotMissingOwnedFile()
    {
        using var dir = new TempDir();
        var crypto = new FakeProtector();
        var backend = new WindowsOwnedProtectedStorage(dir.Path, crypto);
        await backend.WriteAsync(Reference(), new byte[] { 7 }, default);
        crypto.UnprotectFailure = new FileNotFoundException("TOP_SECRET");
        AssertRedacted(await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() => backend.ReadAsync(Reference(), default)));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("parameters")]
    [InlineData("content-type")]
    [InlineData("tuple")]
    public async Task LinuxMalformedTypedSecretDoesNotReturnData(string malformed)
    {
        byte[] secret = [7, 8];
        var bus = new FakeBus
        {
            Override = request => request.Method != "GetSecret" ? null :
                DbusValue.Tuple(new DbusValue("(oayays)", new[]
                {
                    DbusValue.Path(malformed == "session" ? "/other/session" : FakeBus.Session),
                    DbusValue.Bytes(malformed == "parameters" ? [1] : []),
                    DbusValue.Bytes(secret),
                    malformed == "tuple" ? DbusValue.Boolean(true) :
                        DbusValue.Text(malformed == "content-type" ? "TOP_SECRET" : "application/octet-stream"),
                })),
        };
        await Assert.ThrowsAsync<CacheProtectionUnavailableException>(() =>
            new LinuxOwnedProtectedStorage(bus).ReadAsync(Reference(), default));
        Assert.All(secret, value => Assert.Equal(0, value));
        Assert.True(bus.Disposed);
    }

    [Fact]
    public void OwnedNativeBuffersCopyInsteadOfErasingCallerDataAndClearOnRelease()
    {
        byte[] caller = [7, 8];
        using var buffer = new SensitiveNativeBuffer(caller);
        Assert.Equal(7, System.Runtime.InteropServices.Marshal.ReadByte(buffer.DangerousGetHandle()));
        SensitiveNativeBuffer.Zero(buffer.DangerousGetHandle(), buffer.Length);
        Assert.Equal(0, System.Runtime.InteropServices.Marshal.ReadByte(buffer.DangerousGetHandle()));
        Assert.Equal(new byte[] { 7, 8 }, caller);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing-entry-point")]
    [InlineData("allocation-failure")]
    public void NativeSecretBufferOwnershipIsReleasedOnAllocationFailureOrNativeCallback(string outcome)
    {
        byte[] caller = [7, 8];
        SensitiveNativeBuffer? owner = null;
        SensitiveNativeBuffer.ReleaseNativeOwner? release = null;
        IntPtr state = IntPtr.Zero;
        IntPtr Create(IntPtr pointer, nuint size, SensitiveNativeBuffer.ReleaseNativeOwner callback, IntPtr root)
        {
            Assert.Equal((nuint)2, size);
            Assert.Equal(7, System.Runtime.InteropServices.Marshal.ReadByte(pointer));
            owner = (SensitiveNativeBuffer)System.Runtime.InteropServices.GCHandle.FromIntPtr(root).Target!;
            release = callback;
            state = root;
            if (outcome == "missing-entry-point") throw new EntryPointNotFoundException("TOP_SECRET");
            return outcome == "allocation-failure" ? IntPtr.Zero : new IntPtr(1);
        }
        if (outcome == "success")
        {
            Assert.Equal(new IntPtr(1), SensitiveNativeBuffer.CreateNativeOwner(caller, Create));
            Assert.False(owner!.IsClosed);
            release!(state);
        }
        else if (outcome == "missing-entry-point")
            Assert.Throws<EntryPointNotFoundException>(() => SensitiveNativeBuffer.CreateNativeOwner(caller, Create));
        else
            Assert.Throws<NativeStorageException>(() => SensitiveNativeBuffer.CreateNativeOwner(caller, Create));
        Assert.True(owner!.IsClosed);
        Assert.Equal(new byte[] { 7, 8 }, caller);
    }

    private static void AssertRedacted(CacheProtectionUnavailableException failure)
    {
        Assert.Equal(12006, failure.ErrorCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("TOP_SECRET", failure.ToString(), StringComparison.Ordinal);
    }

    private static DbusValue[] Children(DbusValue value) => (DbusValue[])value.Data;
    private static Dictionary<string, string> ToMap(DbusValue value) => Children(value).ToDictionary(
        e => (string)Children(e)[0].Data, e => (string)Children(e)[1].Data, StringComparer.Ordinal);

    private sealed class FakeProtector : IDataProtector
    {
        internal int Calls;
        internal bool FailProtect;
        internal Exception? UnprotectFailure;
        internal Action? AfterProtect;
        internal List<byte[]> OwnedInputs { get; } = [];
        public byte[] Protect(byte[] data, byte[] entropy)
        {
            Calls++;
            OwnedInputs.Add(data);
            if (FailProtect) throw new CryptographicException("TOP_SECRET");
            byte[] result = SHA256.HashData(entropy).Concat(data.Select(b => (byte)(b ^ 0xa5))).ToArray();
            AfterProtect?.Invoke();
            return result;
        }
        public byte[] Unprotect(byte[] data, byte[] entropy)
        {
            Calls++;
            if (UnprotectFailure is not null) throw UnprotectFailure;
            if (data.Length < 32 || !data.AsSpan(0, 32).SequenceEqual(SHA256.HashData(entropy))) throw new CryptographicException("TOP_SECRET");
            return data[32..].Select(b => (byte)(b ^ 0xa5)).ToArray();
        }
    }

    private sealed class FakeHandle : IMacOwnedHandle
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeMac : IMacOsKeychainApi
    {
        internal List<string> Calls { get; } = [];
        internal List<FakeHandle> Handles { get; } = [];
        internal bool UiAllowed = true;
        internal int SetStatus;
        internal int PolicyReadStatus;
        internal int DefaultStatus;
        internal uint KeychainStatus = 1;
        internal int FindStatus;
        internal int MutationStatus;
        internal bool MalformedFind;
        internal int Modifies;
        internal int Adds;
        internal int Deletes;
        internal byte[]? ReturnBuffer;
        internal byte[]? WriteBuffer;
        internal Action? AfterFind;
        public int SetInteractionAllowed(bool allowed) { Calls.Add($"set:{allowed.ToString().ToLowerInvariant()}"); UiAllowed = allowed; return SetStatus; }
        public int GetInteractionAllowed(out bool allowed) { Calls.Add("policy"); allowed = UiAllowed; return PolicyReadStatus; }
        public int CopyDefaultKeychain(out IMacOwnedHandle? keychain) { Calls.Add("default"); keychain = Handle(); return DefaultStatus; }
        public int GetStatus(IMacOwnedHandle keychain, out uint status) { Calls.Add("status"); status = KeychainStatus; return 0; }
        public int Find(IMacOwnedHandle keychain, string service, string account, bool readData, out byte[]? data, out IMacOwnedHandle? item)
        {
            Calls.Add($"find:{service}:{account}");
            item = MalformedFind ? null : Handle();
            data = readData && !MalformedFind ? ReturnBuffer = [7, 8] : null;
            AfterFind?.Invoke();
            return FindStatus;
        }
        public int Add(IMacOwnedHandle keychain, string service, string account, byte[] data) { Calls.Add("add"); Adds++; WriteBuffer = data; return MutationStatus; }
        public int Modify(IMacOwnedHandle item, byte[] data) { Calls.Add("modify"); Modifies++; WriteBuffer = data; return MutationStatus; }
        public int Delete(IMacOwnedHandle item) { Calls.Add("delete"); Deletes++; return MutationStatus; }
        private FakeHandle Handle() { var result = new FakeHandle(); Handles.Add(result); return result; }
    }

    private sealed record Request(string Path, string InterfaceName, string Method, DbusValue Parameters, string ReplySignature);

    private sealed class FakeBus : ISecretServiceTransportFactory, ISecretServiceTransport
    {
        internal const string Collection = "/org/freedesktop/secrets/collection/login";
        internal const string Item = Collection + "/item1";
        internal const string Session = "/org/freedesktop/secrets/session/s1";
        internal List<Request> Calls { get; } = [];
        internal int Opens;
        internal bool Disposed;
        internal bool LockedItem;
        internal bool LockedDefault;
        internal bool MissingDefault;
        internal bool MissingItem;
        internal bool PromptReply;
        internal bool AddGenericSchema;
        internal byte[]? ReturnedSecret;
        internal byte[]? SavedSecret;
        internal Action? AfterSecretRead;
        internal Func<Request, DbusValue?>? Override;
        private DbusValue? _attributes;

        public ISecretServiceTransport Open(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Opens++; Disposed = false; return this; }
        public DbusValue Call(string path, string interfaceName, string method, DbusValue parameters, string replySignature, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var request = new Request(path, interfaceName, method, parameters, replySignature);
            Calls.Add(request);
            var overridden = Override?.Invoke(request);
            if (overridden is not null) return overridden;
            if (method == "ReadAlias") return DbusValue.Tuple(DbusValue.Path(MissingDefault ? "/" : Collection));
            if (method == "Get")
            {
                string property = (string)Children(parameters)[1].Data;
                if (property == "Locked") return DbusValue.Tuple(DbusValue.Variant(DbusValue.Boolean(path == Collection ? LockedDefault : LockedItem)));
                if (property == "Attributes")
                {
                    var copy = ToMap(_attributes!).Select(p => DbusValue.Entry(DbusValue.Text(p.Key), DbusValue.Text(p.Value))).ToArray();
                    if (AddGenericSchema)
                        copy = [.. copy, DbusValue.Entry(DbusValue.Text("xdg:schema"), DbusValue.Text("org.freedesktop.Secret.Generic"))];
                    return DbusValue.Tuple(DbusValue.Variant(DbusValue.Array("{ss}", copy)));
                }
            }
            if (method == "SearchItems")
            {
                _attributes = Children(parameters)[0];
                return DbusValue.Tuple(DbusValue.Array("o", MissingItem || LockedItem ? [] : [DbusValue.Path(Item)]),
                    DbusValue.Array("o", LockedItem ? [DbusValue.Path(Item)] : []));
            }
            if (method == "OpenSession") return DbusValue.Tuple(DbusValue.Variant(DbusValue.Text("")), DbusValue.Path(Session));
            if (method == "GetSecret")
            {
                ReturnedSecret = [7, 8];
                var result = DbusValue.Tuple(DbusValue.Tuple(DbusValue.Path(Session), DbusValue.Bytes([]),
                    DbusValue.Bytes(ReturnedSecret), DbusValue.Text("application/octet-stream")));
                AfterSecretRead?.Invoke();
                return result;
            }
            if (method == "SetSecret")
            {
                SavedSecret = ((byte[])Children(Children(parameters)[0])[2].Data).ToArray();
                return DbusValue.Tuple();
            }
            if (method == "CreateItem")
            {
                SavedSecret = ((byte[])Children(Children(parameters)[1])[2].Data).ToArray();
                return DbusValue.Tuple(DbusValue.Path(PromptReply ? "/" : Item), DbusValue.Path(PromptReply ? "/org/freedesktop/secrets/prompt/p1" : "/"));
            }
            if (method == "Delete") return DbusValue.Tuple(DbusValue.Path(PromptReply ? "/org/freedesktop/secrets/prompt/p1" : "/"));
            if (method == "Close") return DbusValue.Tuple();
            throw new InvalidOperationException($"Unexpected fake protocol operation: {method}");
        }
        public void Dispose() => Disposed = true;
    }
}
