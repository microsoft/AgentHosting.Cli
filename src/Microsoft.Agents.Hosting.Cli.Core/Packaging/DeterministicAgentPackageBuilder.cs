using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Packaging.Zip;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Packaging;

/// <summary>
/// The version-zero deterministic package builder.
///
/// <para>
/// Byte-identical output is the whole point, so every input that is not package content is removed
/// from the result: entries are stored rather than deflated, timestamps are fixed, host permissions
/// and platform markers are never written, entries are ordered by their ordinal normalized path, and
/// exclusions come from a fixed closed list instead of a <c>.gitignore</c> the working copy happens
/// to carry. Two contributors on different machines therefore produce the same bytes from the same
/// tracked source.
/// </para>
///
/// <para>
/// Building is not publishing. This type produces bytes and a digest; it holds no operation state and
/// contacts nothing.
/// </para>
/// </summary>
public sealed class DeterministicAgentPackageBuilder : IAgentPackageBuilder
{
    private readonly AgentPackageLimits _limits;

    public DeterministicAgentPackageBuilder()
        : this(AgentPackageLimits.V0)
    {
    }

    /// <summary>
    /// Builds against <paramref name="limits"/>. The composition root uses
    /// <see cref="AgentPackageLimits.V0"/>; a reduced table lets tests exercise limit enforcement
    /// without materializing hundreds of megabytes. Lowering the client's table cannot widen the
    /// contract — the service enforces version zero independently.
    /// </summary>
    public DeterministicAgentPackageBuilder(AgentPackageLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        if (_limits.MaxArchiveBytes is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                $"The archive limit must be between 1 and {uint.MaxValue} bytes.");
        }

        if (_limits.MaxEntryCount is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                $"The entry limit must be between 1 and {ushort.MaxValue}.");
        }
    }

    /// <inheritdoc />
    public async Task<AgentPackageBuildResult> BuildAsync(
        AgentPackageBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SourceDirectory))
        {
            throw new UsageException("A package source directory is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            throw new UsageException("A package destination path is required.");
        }

        var source = Path.GetFullPath(request.SourceDirectory);
        var destination = Path.GetFullPath(request.DestinationPath);

        if (!Directory.Exists(source))
        {
            throw new UsageException($"The package source directory '{source}' does not exist.");
        }

        var files = new List<SourceFile>();
        Collect(new DirectoryInfo(source), relativePrefix: string.Empty, destination, files, cancellationToken);

        RejectSourceEnvelope(files);
        EnsureArchiveFits(files);

        var entries = new List<PackageEntrySource>(files.Count + 1)
        {
            EnvelopeEntry(),
        };

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var crc = await ComputeCrcAsync(file, cancellationToken).ConfigureAwait(false);
            entries.Add(new PackageEntrySource
            {
                Name = file.Name,
                Length = file.Info.Length,
                Crc32 = crc,
                Open = () => OpenRead(file.Info.FullName),
            });
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the destination and moved into place, so an interrupted build cannot leave a
        // truncated archive where a complete one is expected.
        var staging = destination + ".building";
        try
        {
            await using (var output = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await DeterministicZipWriter.WriteAsync(output, entries, cancellationToken).ConfigureAwait(false);
            }

            string digest;
            long size;
            await using (var written = OpenRead(staging))
            {
                size = written.Length;
                if (size > _limits.MaxArchiveBytes)
                {
                    throw new AgentPackageException(
                        AgentPackageRejection.ArchiveSizeExceeded,
                        $"The package is {size} bytes; version zero accepts at most {_limits.MaxArchiveBytes}.");
                }

                digest = Convert
                    .ToHexString(await SHA256.HashDataAsync(written, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
            }

            File.Move(staging, destination, overwrite: true);

            return new AgentPackageBuildResult
            {
                PackagePath = destination,
                PackageVersion = AgentPackageContract.PackageVersion,
                Sha256 = digest,
                SizeInBytes = size,
                EntryCount = entries.Count,
            };
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static PackageEntrySource EnvelopeEntry()
    {
        var bytes = AgentPackageContract.EnvelopeBytes.ToArray();
        return new PackageEntrySource
        {
            Name = AgentPackageContract.EnvelopeFileName,
            Length = bytes.Length,
            Crc32 = Crc32.Compute(bytes),
            Open = () => new MemoryStream(bytes, writable: false),
        };
    }

    private void Collect(
        DirectoryInfo directory,
        string relativePrefix,
        string destination,
        List<SourceFile> files,
        CancellationToken cancellationToken)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentPackageException(
                AgentPackageRejection.SourceUnreadable,
                $"The package source directory '{directory.FullName}' could not be read.",
                innerException: ex);
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = relativePrefix.Length == 0 ? child.Name : $"{relativePrefix}/{child.Name}";

            // A link is rejected rather than followed or dereferenced. This is determinism, not
            // extraction safety: its target is resolved where the package is built, so following it
            // pulls in content from outside the source tree and makes the digest depend on the
            // machine, while skipping it silently drops a file the operator can see.
            if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AgentPackageException(
                    AgentPackageRejection.SourceLink,
                    $"Package source entry '{name}' is a link. Version zero packages regular files only, so the same source produces the same bytes on every machine.",
                    "Replace the link with the file or directory it refers to, or move it out of the project directory.");
            }

            if (child is DirectoryInfo childDirectory)
            {
                if (PackageExclusions.IsExcludedDirectory(child.Name))
                {
                    continue;
                }

                Collect(childDirectory, name, destination, files, cancellationToken);
                continue;
            }

            if (child is not FileInfo file)
            {
                throw new AgentPackageException(
                    AgentPackageRejection.SourceEntryNotAFile,
                    $"Package source entry '{name}' is not a regular file.");
            }

            if (PackageExclusions.IsExcludedFile(file.Name))
            {
                continue;
            }

            // The archive being written is never packaged into itself, whether or not it lives inside
            // the source tree.
            if (string.Equals(file.FullName, destination, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (files.Count >= _limits.MaxEntryCount - 1)
            {
                throw new AgentPackageException(
                    AgentPackageRejection.EntryCountExceeded,
                    $"The package source contains more entries than the version-zero limit of {_limits.MaxEntryCount}, including the generated root envelope.",
                    "Remove build output or vendored content from the project directory. Version zero excludes a fixed list of directories and never reads a .gitignore.");
            }

            files.Add(new SourceFile(PackagePathPolicy.NormalizeSourcePath(name), file));
        }
    }

    private void EnsureArchiveFits(IReadOnlyList<SourceFile> files)
    {
        long archiveBytes = 0;
        AddBytes(ref archiveBytes, DeterministicZipWriter.EndOfCentralDirectoryLength);
        AddEntry(
            ref archiveBytes,
            AgentPackageContract.EnvelopeFileName,
            AgentPackageContract.EnvelopeBytes.Length);

        foreach (var file in files)
        {
            AddEntry(ref archiveBytes, file.Name, file.Info.Length);
        }
    }

    private void AddEntry(ref long archiveBytes, string name, long contentBytes)
    {
        var nameBytes = Encoding.UTF8.GetByteCount(name);
        var metadataBytes =
            DeterministicZipWriter.LocalHeaderLength +
            DeterministicZipWriter.CentralHeaderLength +
            (2L * nameBytes);

        AddBytes(ref archiveBytes, metadataBytes);
        AddBytes(ref archiveBytes, contentBytes);
    }

    private void AddBytes(ref long archiveBytes, long additionalBytes)
    {
        if (additionalBytes < 0 || archiveBytes > _limits.MaxArchiveBytes - additionalBytes)
        {
            throw new AgentPackageException(
                AgentPackageRejection.ArchiveSizeExceeded,
                $"The package would exceed the version-zero limit of {_limits.MaxArchiveBytes} bytes.");
        }

        archiveBytes += additionalBytes;
    }

    private static void RejectSourceEnvelope(IReadOnlyList<SourceFile> files)
    {
        var conflict = files.FirstOrDefault(f =>
            f.Name.Equals(AgentPackageContract.EnvelopeFileName, StringComparison.OrdinalIgnoreCase));

        if (conflict is not null)
        {
            throw new AgentPackageException(
                AgentPackageRejection.EnvelopeSourceConflict,
                $"The package source already contains '{conflict.Name}'. The builder emits the root package envelope itself.",
                "Delete the file from the project directory. It is generated, and two copies would make the package's declared version ambiguous.");
        }
    }

    private static async Task<uint> ComputeCrcAsync(SourceFile file, CancellationToken cancellationToken)
    {
        await using var content = OpenRead(file.Info.FullName);
        return await Crc32.ComputeAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private static FileStream OpenRead(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentPackageException(
                AgentPackageRejection.SourceUnreadable,
                $"'{path}' could not be read while building the package.",
                innerException: ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The staging file is disposable; the original failure is the one worth reporting.
        }
    }

    private sealed record SourceFile(string Name, FileInfo Info);
}
