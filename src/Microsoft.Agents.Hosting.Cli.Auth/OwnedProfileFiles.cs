using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal sealed class OwnedProfileFiles
{
    private readonly string _directory;
    internal OwnedProfileFiles(string root) => _directory = Path.GetFullPath(Path.Combine(root, "owned-profile-references-v1"));

    internal string PathFor(string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new AuthProfileStateException("Invalid owned reference inventory filename.");
        return Path.Combine(_directory, fileName);
    }

    internal void EnsureDirectory()
    {
        EnsureDirectory(_directory);
        CheckPermissions(_directory, directory: true);
    }

    internal static void EnsureDirectory(string path)
    {
        var missing = new Stack<DirectoryInfo>();
        var current = new DirectoryInfo(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.Directory) == 0)
                    throw new IOException("The credential storage path has a non-directory ancestor.");
                CheckAncestors(current.FullName);
                break;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
            {
                missing.Push(current);
                current = current.Parent ?? throw new DirectoryNotFoundException();
            }
        }
        while (missing.TryPop(out var directory))
        {
            CheckAncestors(directory.Parent!.FullName);
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var owner = identity.User ?? throw new UnauthorizedAccessException();
                var acl = new DirectorySecurity();
                acl.SetOwner(owner);
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                directory.Create(acl);
            }
            else Directory.CreateDirectory(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CheckAncestors(directory.FullName);
            CheckPermissions(directory.FullName, directory: true);
        }
    }

    internal FileStream OpenLease(string name)
    {
        EnsureDirectory();
        var path = PathFor(name);
        try { return Create(path); }
        catch (IOException) when (File.Exists(path))
        {
            CheckFile(path);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    internal async Task WriteAsync(string name, byte[] data, CancellationToken ct)
    {
        EnsureDirectory();
        var path = PathFor(name);
        var pending = PathFor($"{name}.{Guid.NewGuid():N}.pending");
        try
        {
            await using (var stream = Create(pending))
            {
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            CheckAncestors(_directory);
            if (File.Exists(path)) CheckFile(path);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            // Pending files contain only metadata. They are not reference markers or native values.
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    internal async Task<byte[]?> ReadAsync(string name, CancellationToken ct)
    {
        EnsureDirectory();
        var path = PathFor(name);
        try
        {
            CheckFile(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) throw new AuthProfileStateException("Owned reference metadata exceeds its size limit.");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    internal string[] Markers()
    {
        if (!Directory.Exists(_directory)) return [];
        EnsureDirectory();
        return Directory.GetFiles(_directory, "*.marker").Select(p => Path.GetFileName(p)!).ToArray();
    }

    internal void Delete(string name)
    {
        var path = PathFor(name);
        try { CheckFile(path); File.Delete(path); }
        catch (FileNotFoundException) { }
    }

    internal static FileStream Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            var acl = new FileSecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough, acl);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    internal static void CheckAncestors(string path)
    {
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
            if ((File.GetAttributes(dir.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new AuthProfileStateException("Owned reference paths cannot traverse symbolic links.");
    }

    internal static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new AuthProfileStateException("Owned reference metadata is not a regular file.");
        CheckPermissions(path, directory: false);
    }

    internal static void CheckPermissions(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException();
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw new AuthProfileStateException("Owned reference metadata permissions are unsafe.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference))
                    throw new AuthProfileStateException("Owned reference metadata permissions are unsafe.");
        }
        else
        {
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & others) != 0)
                throw new AuthProfileStateException("Owned reference metadata permissions are unsafe.");
        }
    }
}
