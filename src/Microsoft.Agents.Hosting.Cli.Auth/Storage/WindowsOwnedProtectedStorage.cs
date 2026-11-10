using System.Security.AccessControl;
using System.Security.Principal;

namespace Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;

internal sealed class WindowsOwnedProtectedStorage : OwnedProtectedStorageBackend
{
    private readonly string _directory;
    private readonly IDataProtector _protector;
    private const int MaxProtectedBytes = Protection.MaxValueBytes + 1024 * 1024;

    internal WindowsOwnedProtectedStorage(string directory, IDataProtector protector) : base("Windows")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(Path.Combine(directory, "owned-protected-v1"));
        _protector = protector;
    }

    protected override async Task<byte[]?> ReadCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        byte[]? encrypted = null;
        byte[] entropy = reference.Entropy;
        try
        {
            await using var stream = OpenOwnedFile(reference);
            if (stream is null) return null;
            if (stream.Length is <= 0 or > MaxProtectedBytes)
            {
                throw Protection.Failure("Windows", "invalid-protected-file");
            }

            encrypted = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            byte[] result = _protector.Unprotect(encrypted, entropy);
            return ReferenceEquals(result, encrypted) ? result.ToArray() : result;
        }
        finally
        {
            Protection.Wipe(encrypted);
            Protection.Wipe(entropy);
        }
    }

    private FileStream? OpenOwnedFile(OwnedProtectedStorageReference reference)
    {
        try
        {
            CheckAncestors(_directory);
            CheckOwnedDirectory();
            string path = Path.Combine(_directory, reference.FileName);
            CheckFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    protected override async Task WriteCoreAsync(OwnedProtectedStorageReference reference, byte[] data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] entropy = reference.Entropy;
        byte[]? encrypted = null;
        string? temporary = null;
        bool failed = false;
        try
        {
            encrypted = _protector.Protect(data, entropy);
            ct.ThrowIfCancellationRequested();
            if (encrypted.Length is <= 0 or > MaxProtectedBytes)
            {
                throw Protection.Failure("Windows", "invalid-protected-value");
            }

            CheckAncestors(Path.GetDirectoryName(_directory)!);
            CreateOwnedDirectory();
            CheckAncestors(_directory);
            string path = Path.Combine(_directory, reference.FileName);
            CheckFileIfPresent(path);
            temporary = Path.Combine(_directory, $"{reference.FileName}.{Guid.NewGuid():N}.pending");
            await using (var stream = CreateOwnerOnlyFile(temporary))
            {
                await stream.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            CheckAncestors(_directory);
            CheckFileIfPresent(path);
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Protection.Wipe(encrypted);
            Protection.Wipe(entropy);
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (failed && ex is IOException or UnauthorizedAccessException)
                {
                    // Cleanup cannot replace the primary cancellation/protection/IO failure.
                }
            }
        }
    }

    protected override Task ClearCoreAsync(OwnedProtectedStorageReference reference, CancellationToken ct)
    {
        try
        {
            CheckAncestors(_directory);
            CheckOwnedDirectory();
            string path = Path.Combine(_directory, reference.FileName);
            CheckFile(path);
            ct.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only a genuinely missing owned file is an idempotent clear.
        }

        return Task.CompletedTask;
    }

    private void CreateOwnedDirectory()
    {
        try
        {
            CheckAncestors(_directory);
            CheckOwnedDirectory();
            return;
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CheckOwnedDirectory();
    }

    private void CheckOwnedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var security = new DirectoryInfo(_directory).GetAccessControl();
            if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw Protection.Failure("Windows", "unsafe-owned-directory-permissions");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference))
                    throw Protection.Failure("Windows", "unsafe-owned-directory-permissions");
            }
        }
        else
        {
            const UnixFileMode otherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(_directory) & otherAccess) != 0)
                throw Protection.Failure("Windows", "unsafe-owned-directory-permissions");
        }
    }

    private static FileStream CreateOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    private static void CheckAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            {
                throw Protection.Failure("Windows", "unsafe-owned-directory");
            }
        }
    }

    private static void CheckFileIfPresent(string path)
    {
        try { CheckFile(path); }
        catch (FileNotFoundException) { }
    }

    private static void CheckFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw Protection.Failure("Windows", "unsafe-owned-file");
        }

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var security = new FileInfo(path).GetAccessControl();
            if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            {
                throw Protection.Failure("Windows", "unsafe-owned-permissions");
            }
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference))
                    throw Protection.Failure("Windows", "unsafe-owned-permissions");
            }
        }
    }
}
