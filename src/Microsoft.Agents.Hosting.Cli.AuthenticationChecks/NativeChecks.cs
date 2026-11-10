using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.AuthenticationChecks;

public static class NativeChecks
{
    public static string DeniedBusConfiguration(string configuration, string address)
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(configuration),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var document = System.Xml.Linq.XDocument.Load(reader);
        var bus = document.Root;
        if (bus?.Name != "busconfig" || bus.Elements().Any(element => element.Name.LocalName is not ("type" or "listen" or "auth" or "policy")) ||
            bus.Elements("listen").Count() != 1 || bus.Element("listen")!.Value != address)
            throw new InvalidOperationException("Only the exact prepared private bus without includes or activation directories may be denied.");
        var policy = bus.Elements("policy").Single();
        if (policy.Attribute("context")?.Value != "default")
            throw new InvalidOperationException("Prepared bus policy unavailable.");
        policy.Add(new System.Xml.Linq.XElement("deny", new System.Xml.Linq.XAttribute("send_destination", "org.freedesktop.secrets")));
        return document.ToString();
    }

    public static void RequirePreparedBus(string root, string? address, string? diagnostics)
    {
        if (address != "unix:abstract=" + Path.GetFileName(root) || !string.IsNullOrEmpty(diagnostics))
            throw new InvalidOperationException("Only the prepared private local bus with diagnostics disabled is permitted.");
    }

    public static void RequirePreparedRoot(string root)
        => RequireOwnedRoot(root, prepared: true);

    private static void RequireOwnedRoot(string root, bool prepared)
    {
        var path = Path.GetFullPath(root);
        var parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        var leaf = Path.GetFileName(path);
        if (Path.GetDirectoryName(path) != parent || !leaf.StartsWith("msagent-native-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf["msagent-native-".Length..], "N", out _) ||
            !Directory.Exists(path) || prepared && !File.Exists(Path.Combine(path, "prepared")) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A prepared, test-owned native leaf under the checkout artifacts directory is required.");
    }

    [SupportedOSPlatform("linux")]
    public static async Task CheckDaemonControlDirectoryAsync(string root, bool publication)
    {
        RequireOwnedRoot(root, prepared: false);
        var control = Path.Combine(root, "control");
        var attributes = File.GetAttributes(control);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The exact owned control directory must not be redirected.");
        var uid = (await FixtureControl(root, "id", "-u")).Trim();
        var owner = (await FixtureControl(root, "stat", "-c", "%u", "--", "control")).Trim();
        bool owned = uint.TryParse(uid, out _) && owner == uid;
        if (publication)
        {
            AcceptanceChecks.RequireDaemonControlDirectory("control", owned, File.GetUnixFileMode(control));
            var selected = await Console.In.ReadToEndAsync();
            if (selected.EndsWith('\n')) selected = selected[..^1];
            AcceptanceChecks.RequireDaemonControlPublication(selected);
        }
        else
        {
            AcceptanceChecks.PrepareDaemonControlDirectory("control", owned,
                mode => File.SetUnixFileMode(control, mode), () => File.GetUnixFileMode(control));
        }
        Console.WriteLine($"tier=native-nonsecret check=daemon-control-{(publication ? "publication" : "preparation")} status=PASS mode=0700 socketBytes=15 owned=true");
    }

    public static async Task RunAsync(string root, string rid, string packageDirectory)
    {
        RequirePreparedRoot(root);
        MacOsKeychainProcessPolicy? policy = null;
        var nativeEnvironment = new Dictionary<string, string?>();
        if (OperatingSystem.IsMacOS())
        {
            policy = MacOsKeychainProcessPolicy.EstablishForCliProcess();
            RequireDefaultKeychain(Path.Combine(root, "test.keychain-db"));
            Console.WriteLine("tier=native-nonsecret check=keychain-process-noUI status=PASS");
        }
        if (OperatingSystem.IsLinux())
        {
            RequireGlib();
            var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            RequirePreparedBus(root, address, Environment.GetEnvironmentVariable("G_DBUS_DEBUG"));
            nativeEnvironment["DBUS_SESSION_BUS_ADDRESS"] = address;
            nativeEnvironment["G_DBUS_DEBUG"] = null;
        }
        var backend = OwnedProtectedStorageBackendFactory.Create(root, policy, nativeEnvironment.GetValueOrDefault);
        await AcceptanceChecks.CheckOwnedStorageAsync(backend, default);
        Console.WriteLine("tier=native-nonsecret check=owned-protect-read-delete-ABI status=PASS kinds=credential+cache cleanup=exact-references");
        await CheckDeniedStorage(backend, root, policy, nativeEnvironment);
        Console.WriteLine("tier=native-nonsecret check=existing-store-denied-read-write-delete status=PASS kinds=credential+cache noUI=true autoUnlock=false fallback=none retained=true cleanup=exact-references");
        CheckCertificate(root);
        CheckClientConfiguration(rid);
        await InstalledToolChecks.RunAsync(packageDirectory, rid, installed =>
        {
            var library = PackageChecks.BrokerLibrary(rid);
            if (library is null)
            {
                Console.WriteLine("tier=native-nonsecret check=broker-library-loader status=UNVERIFIED reason=no-linux-arm64-runtime-asset");
            }
            else
            {
                var handle = NativeLibrary.Load(Path.Combine(installed, library));
                NativeLibrary.Free(handle);
                Console.WriteLine("tier=native-nonsecret check=packaged-broker-library-loader status=PASS readiness=UNVERIFIED");
            }
            return Task.CompletedTask;
        });
        Console.WriteLine("tier=live check=auth-service-browser-broker-signin status=UNVERIFIED reason=separate-authorization-required");
    }

    private static void CheckCertificate(string root)
    {
        string? scratch = null;
        if (OperatingSystem.IsMacOS())
        {
            scratch = Path.Combine(root, "scratch");
            if (Path.TrimEndingDirectorySeparator(Path.GetTempPath()) != scratch)
                throw new InvalidOperationException("macOS framework temporary Keychains must stay in the prepared owned scratch directory.");
        }
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=msagent-native-test-only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        var password = Guid.NewGuid().ToString("N");
        var fixtures = new Dictionary<string, byte[]>();
        var issued = new List<string>();
        var before = scratch is null ? [] : Directory.GetFileSystemEntries(scratch).ToHashSet(StringComparer.Ordinal);
        ExceptionDispatchInfo? failure = null;
        bool cleanupFailed = false;
        try
        {
            fixtures.Add("protected.pfx", certificate.Export(X509ContentType.Pfx, password));
            fixtures.Add("passwordless.pfx", certificate.Export(X509ContentType.Pfx, (string?)null));
            fixtures.Add("invalid.pfx", [1, 2, 3]);
            fixtures.Add("public-only.pfx", publicOnly.Export(X509ContentType.Pfx, password));
            foreach (var (name, bytes) in fixtures)
            {
                var path = Path.Combine(root, name);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                issued.Add(path);
                file.Write(bytes);
            }
            AcceptanceChecks.CheckCertificateCases((name, secret) => ProfileCertificateLoader.Load(Path.Combine(root, name), secret),
                loaded =>
                {
                    using var privateKey = loaded.GetRSAPrivateKey() ?? throw new InvalidOperationException("Portable PFX private key missing.");
                    byte[] data = RandomNumberGenerator.GetBytes(32);
                    var signature = privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        throw new InvalidOperationException("Generated portable private-key signing failed.");
                    if (scratch is not null && Directory.GetFileSystemEntries(scratch).All(before.Contains))
                        throw new InvalidOperationException("Expected framework temporary Keychain was not observed during the owned import.");
                }, password);
            foreach (var (name, bytes) in fixtures)
            {
                var retained = File.ReadAllBytes(Path.Combine(root, name));
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(bytes, retained))
                        throw new InvalidOperationException("Generated source PFX was changed.");
                }
                finally { CryptographicOperations.ZeroMemory(retained); }
            }
            if (scratch is not null && !before.SetEquals(Directory.GetFileSystemEntries(scratch)))
                throw new InvalidOperationException("Framework temporary Keychain cleanup was not observed after disposing owned certificate/key handles.");
        }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            foreach (var bytes in fixtures.Values) CryptographicOperations.ZeroMemory(bytes);
            cleanupFailed = !AcceptanceChecks.DeleteIssuedFixtures(issued, File.Delete);
        }
        if (cleanupFailed) throw new InvalidOperationException("Exact generated PFX fixture cleanup failed.", failure?.SourceException);
        failure?.Throw();
        Console.WriteLine("tier=native-nonsecret check=test-generated-PFX-private-key-and-negatives status=PASS valid=protected+passwordless-null+passwordless-empty rejected=missing-password+wrong-password+passwordless-wrong-password+malformed+keyless source=retained");
        Console.WriteLine($"tier=native-nonsecret check=PFX-key-lifetime status=PASS persistence={(scratch is null ? "ephemeral" : "framework-temporary-keychain")} cleanup=observed-owned-fixture-only");
    }

    private static async Task CheckDeniedStorage(IOwnedProtectedStorageBackend backend, string root,
        MacOsKeychainProcessPolicy? policy, Dictionary<string, string?> environment)
    {
        if (OperatingSystem.IsWindows())
        {
            await CheckWindowsDeniedStorage(backend, root);
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var password = Console.ReadLine();
            if (password is not { Length: 32 } || password.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidOperationException("Prepared generated fixture password must be supplied on the dedicated child stdin.");
            IntPtr keychain = IntPtr.Zero;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
            try
            {
                RequireDefaultKeychain(Path.Combine(root, "test.keychain-db"));
                if (SecKeychainOpen(System.Text.Encoding.UTF8.GetBytes(Path.Combine(root, "test.keychain-db") + "\0"),
                    out keychain) != 0 || keychain == IntPtr.Zero)
                    throw new InvalidOperationException("Exact owned Keychain could not be opened.");
                await AcceptanceChecks.CheckDeniedStorageAsync(backend, _ =>
                {
                    if (SecKeychainLock(keychain) != 0) throw new InvalidOperationException("Owned Keychain lock failed.");
                    return Task.CompletedTask;
                }, () =>
                {
                    RequireDefaultKeychain(Path.Combine(root, "test.keychain-db"));
                    policy!.Ensure(CancellationToken.None);
                    if (SecKeychainGetStatus(keychain, out uint status) != 0 || (status & 1) != 0)
                        throw new InvalidOperationException("Owned Keychain did not remain locked without UI.");
                    return Task.CompletedTask;
                }, () =>
                {
                    // Explicit fixture restoration only; production never unlocks Keychains.
                    RequireDefaultKeychain(Path.Combine(root, "test.keychain-db"));
                    if (SecKeychainUnlock(keychain, (uint)bytes.Length, bytes, 1) != 0)
                        throw new InvalidOperationException("Owned Keychain fixture restoration failed.");
                    return Task.CompletedTask;
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (keychain != IntPtr.Zero) CFRelease(keychain);
            }
            return;
        }
        var config = Path.Combine(root, "bus.conf");
        if ((File.GetAttributes(config) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Private bus configuration must not redirect outside the owned leaf.");
        var original = File.ReadAllText(config);
        var denied = DeniedBusConfiguration(original, environment["DBUS_SESSION_BUS_ADDRESS"]!);
        var serviceOwner = (await PrivateBusControl(root, "GetNameOwner", "org.freedesktop.secrets")).Trim();
        if (serviceOwner.Length == 0) throw new InvalidOperationException("Existing private Secret Service owner unavailable.");
        await AcceptanceChecks.CheckDeniedStorageAsync(backend, async references =>
        {
            File.WriteAllText(config, denied);
            _ = await PrivateBusControl(root, "ReloadConfig");
        }, async () =>
        {
            if (File.ReadAllText(config) != denied ||
                (await PrivateBusControl(root, "GetNameOwner", "org.freedesktop.secrets")).Trim() != serviceOwner)
                throw new InvalidOperationException("Store denial policy or the existing private Secret Service owner was lost.");
        }, async () =>
        {
            File.WriteAllText(config, original);
            _ = await PrivateBusControl(root, "ReloadConfig");
            if ((await PrivateBusControl(root, "GetNameOwner", "org.freedesktop.secrets")).Trim() != serviceOwner)
                throw new InvalidOperationException("The private Secret Service owner changed during fixture restoration.");
        });
    }

    [SupportedOSPlatform("windows")]
    private static async Task CheckWindowsDeniedStorage(IOwnedProtectedStorageBackend backend, string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Dedicated fixture process owner unavailable.");
        var directory = new DirectoryInfo(Path.Combine(root, "owned-protected-v1"));
        DirectorySecurity? directorySecurity = null;
        var files = new Dictionary<FileInfo, FileSecurity>();
        const FileSystemRights deniedFile = FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.Delete;
        const FileSystemRights deniedDirectory = FileSystemRights.CreateFiles | FileSystemRights.DeleteSubdirectoriesAndFiles;
        await AcceptanceChecks.CheckDeniedStorageAsync(backend, references =>
        {
            foreach (var reference in references)
            {
                var file = new FileInfo(Path.Combine(directory.FullName, reference.FileName));
                var security = file.GetAccessControl();
                files.Add(file, security);
                var denied = file.GetAccessControl();
                denied.AddAccessRule(new FileSystemAccessRule(owner, deniedFile, AccessControlType.Deny));
                file.SetAccessControl(denied);
            }
            // Parent DELETE_CHILD otherwise permits deletion despite an item's DELETE denial.
            directorySecurity = directory.GetAccessControl();
            var deniedParent = directory.GetAccessControl();
            deniedParent.AddAccessRule(new FileSystemAccessRule(owner, deniedDirectory, AccessControlType.Deny));
            directory.SetAccessControl(deniedParent);
            return Task.CompletedTask;
        }, () =>
        {
            RequireDeny(directory.GetAccessControl(), deniedDirectory);
            foreach (var file in files.Keys) RequireDeny(file.GetAccessControl(), deniedFile);
            return Task.CompletedTask;
        }, () =>
        {
            if (directorySecurity is not null) directory.SetAccessControl(directorySecurity);
            foreach (var (file, security) in files) file.SetAccessControl(security);
            return Task.CompletedTask;
        });

        void RequireDeny(FileSystemSecurity security, FileSystemRights rights)
        {
            if (!security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => owner.Equals(rule.IdentityReference) && rule.AccessControlType == AccessControlType.Deny &&
                    (rule.FileSystemRights & rights) == rights))
                throw new InvalidOperationException("Exact owned fixture ACL denial did not remain applied.");
        }
    }

    private static Task<string> PrivateBusControl(string root, string method, params string[] arguments)
        => FixtureControl(root, "gdbus", new[] { "call", "--session", "--dest", "org.freedesktop.DBus",
            "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus." + method }.Concat(arguments).ToArray());

    private static async Task<string> FixtureControl(string root, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:abstract=" + Path.GetFileName(root);
        start.Environment.Remove("G_DBUS_DEBUG");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Owned fixture restoration process did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ExceptionDispatchInfo? failure = null;
        bool cleanupFailed = false;
        try
        {
            child.StandardInput.Close();
            await child.WaitForExitAsync(bound.Token);
            await Task.WhenAll(output, error).WaitAsync(bound.Token);
            if (child.ExitCode != 0) throw new InvalidOperationException("Private bus fixture control failed.");
        }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                    cleanupFailed = !child.WaitForExit(5000);
                }
                catch { cleanupFailed = true; }
            }
        }
        if (cleanupFailed) throw new InvalidOperationException("Owned fixture restoration process cleanup failed.", failure?.SourceException);
        failure?.Throw();
        return output.Result;
    }

    private static void CheckClientConfiguration(string rid)
    {
        var platform = new RuntimePlatformInfo();
        var runtime = new ProfileCredentialRuntimeOptions();
        using var federation = new ProfileFederationAssertionProvider(runtime, new DenyNetwork());
        using var msalNetwork = new DenyMsalNetwork();
        var factory = new ProfileMsalClientFactory(platform, runtime, federation, msalNetwork);
        var request = new ProfileTokenRequest
        {
            Method = AuthCredentialMethod.Browser, ClientId = AgentHostClientAppConfiguration.DefaultClientId,
            Authority = AgentHostAuthorityResolver.Resolve(CloudInstance.Public),
            Scopes = ["https://api.powerplatform.com/.default"], CacheReference = "never-opened",
            RedirectUri = "http://localhost", InteractionAllowed = false,
        };
        var browser = factory.CreatePublic(request, broker: false);
        if (browser.AppConfig.RedirectUri != "http://localhost")
            throw new InvalidOperationException("System-browser loopback configuration changed.");
        if (PackageChecks.BrokerLibrary(rid) is not null)
        {
            var options = MsalPublicClientProvider.CreateBrokerOptions(platform, listAccounts: false);
            if (options.ListOperatingSystemAccounts) throw new InvalidOperationException("Broker configuration must not enumerate OS accounts.");
            _ = factory.CreatePublic(request, broker: true);
            Console.WriteLine("tier=native-nonsecret check=MSAL-broker-configuration status=PASS readiness=UNVERIFIED account-enumeration=none");
        }
        else
        {
            Console.WriteLine("tier=native-nonsecret check=MSAL-broker-configuration status=UNVERIFIED reason=no-linux-arm64-runtime-asset");
        }
        Console.WriteLine("tier=native-nonsecret check=MSAL-browser-configuration status=PASS readiness=UNVERIFIED account-enumeration=none");
    }

    private static void RequireGlib()
    {
        var handles = new List<IntPtr>();
        try
        {
            foreach (var library in new[] { "libglib-2.0.so.0", "libgobject-2.0.so.0", "libgio-2.0.so.0" })
                handles.Add(NativeLibrary.Load(library));
            var major = Marshal.ReadInt32(NativeLibrary.GetExport(handles[0], "glib_major_version"));
            var minor = Marshal.ReadInt32(NativeLibrary.GetExport(handles[0], "glib_minor_version"));
            if (major < 2 || major == 2 && minor < 36)
                throw new InvalidOperationException("GLib/GIO 2.36 or newer is required.");
            Console.WriteLine($"tier=native-nonsecret check=GLib-GIO-library-version status=PASS version={major}.{minor}");
        }
        finally { foreach (var handle in handles) NativeLibrary.Free(handle); }
    }

    private static void RequireDefaultKeychain(string expectedPath)
    {
        IntPtr keychain = IntPtr.Zero;
        try
        {
            if (SecKeychainCopyDefault(out keychain) != 0 || keychain == IntPtr.Zero)
                throw new InvalidOperationException("Prepared default keychain unavailable.");
            var buffer = new byte[4096];
            uint length = (uint)buffer.Length;
            if (SecKeychainGetPath(keychain, ref length, buffer) != 0 || length >= buffer.Length ||
                System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0') != expectedPath)
                throw new InvalidOperationException("Default keychain is not the exact prepared test-owned keychain.");
        }
        finally { if (keychain != IntPtr.Zero) CFRelease(keychain); }
    }

#pragma warning disable SYSLIB1054
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainCopyDefault(out IntPtr keychain);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainGetPath(IntPtr keychain, ref uint length, byte[] path);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainOpen(byte[] path, out IntPtr keychain);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainLock(IntPtr keychain);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainGetStatus(IntPtr keychain, out uint status);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainUnlock(IntPtr keychain, uint length, byte[] password, byte usePassword);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
#pragma warning restore SYSLIB1054

    private sealed class DenyNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network acquisition is forbidden in native nonsecret checks.");
    }
    private sealed class DenyMsalNetwork : IMsalHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new DenyNetwork());
        public HttpClient GetHttpClient() => _client;
        public void Dispose() => _client.Dispose();
    }
}
