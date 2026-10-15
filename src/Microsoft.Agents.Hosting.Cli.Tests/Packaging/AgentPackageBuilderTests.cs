using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Packaging;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Packaging;

/// <summary>
/// Deterministic version-zero package building. Everything here runs offline against temporary
/// directories: no credential, no network, and nothing left on disk when a test finishes.
/// </summary>
public sealed class AgentPackageBuilderTests
{
    [Fact]
    public void V0Bounds_AreTheVersionZeroTable()
    {
        var limits = AgentPackageLimits.V0;

        Assert.Equal(128L * 1024 * 1024, limits.MaxArchiveBytes);
        Assert.Equal(10_000, limits.MaxEntryCount);

        // Nothing in this path expands a package, so the local builder needs only archive and source
        // enumeration bounds.
        Assert.Equal(
            ["MaxArchiveBytes", "MaxEntryCount"],
            typeof(AgentPackageLimits)
                .GetProperties()
                .Select(property => property.Name)
                .Where(name => name.StartsWith("Max", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Build_FromIndependentSourceTrees_ProducesByteIdenticalPackages()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        var one = await BuildAsync(TestProjectSource.Create(first.Combine("project")), first);
        var two = await BuildAsync(TestProjectSource.Create(second.Combine("elsewhere", "project")), second);

        Assert.Equal(one.Result.Sha256, two.Result.Sha256);
        Assert.Equal(one.Bytes, two.Bytes);
        Assert.Equal(one.Result.SizeInBytes, two.Result.SizeInBytes);
    }

    [Fact]
    public async Task Build_RepeatedIntoTheSameDestination_ProducesTheSameBytes()
    {
        using var temp = new TempDirectory();
        var source = TestProjectSource.Create(temp.Combine("project"));

        var first = await BuildAsync(source, temp);
        var second = await BuildAsync(source, temp);

        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Result.Sha256, second.Result.Sha256);
    }

    [Fact]
    public async Task Build_EmitsTheVersionZeroEnvelope_AtTheArchiveRoot()
    {
        using var temp = new TempDirectory();
        var built = await BuildAsync(TestProjectSource.Create(temp.Combine("project")), temp);

        var envelope = Assert.Single(
            TestZipReader.Read(built.Bytes),
            entry => entry.Name == AgentPackageContract.EnvelopeFileName);

        Assert.Equal("{\"packageVersion\":\"0\",\"packageType\":\"code\"}", Encoding.UTF8.GetString(envelope.Content));
        Assert.Equal("0", built.Result.PackageVersion);

        // The two names version zero must not adopt: an incompatible manifest contract and a Managed
        // App build marker.
        Assert.DoesNotContain(TestZipReader.Read(built.Bytes), entry =>
            entry.Name.Equals("agent.manifest.json", StringComparison.OrdinalIgnoreCase) ||
            entry.Name.Equals("BUILDMARKER.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_PackagesTheFixedClosedSet_InOrdinalOrder()
    {
        using var temp = new TempDirectory();
        var built = await BuildAsync(TestProjectSource.Create(temp.Combine("project")), temp);

        var names = TestZipReader.Read(built.Bytes).Select(entry => entry.Name).ToArray();

        Assert.Equal(TestProjectSource.ExpectedEntries, names);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal).ToArray(), names);
        Assert.Equal(names.Length, built.Result.EntryCount);
    }

    [Fact]
    public async Task Build_NeverReadsAnEnvironmentIgnoreSet()
    {
        using var temp = new TempDirectory();
        var built = await BuildAsync(TestProjectSource.Create(temp.Combine("project")), temp);

        var names = TestZipReader.Read(built.Bytes).Select(entry => entry.Name).ToArray();

        // The test source's .gitignore names ignored-by-gitignore.txt and src/**/*.map. Both are
        // packaged, and .gitignore itself is packaged, because the exclusion list is fixed and closed:
        // the digest is a property of the content, not of the working copy.
        Assert.Contains(".gitignore", names);
        Assert.Contains("ignored-by-gitignore.txt", names);
        Assert.Contains("src/lib/util.js.map", names);
    }

    [Fact]
    public async Task Build_ExcludesTheFixedList_WithoutChangingTheDigest()
    {
        using var withExcluded = new TempDirectory();
        using var withoutExcluded = new TempDirectory();

        var full = await BuildAsync(TestProjectSource.Create(withExcluded.Combine("project")), withExcluded);
        var clean = await BuildAsync(
            TestProjectSource.CreateWithoutExcludedContent(withoutExcluded.Combine("project")),
            withoutExcluded);

        Assert.Equal(clean.Result.Sha256, full.Result.Sha256);
        Assert.Equal(clean.Bytes, full.Bytes);

        var names = TestZipReader.Read(full.Bytes).Select(entry => entry.Name).ToArray();
        foreach (var excluded in PackageExclusions.Directories)
        {
            Assert.DoesNotContain(names, name => name.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("/" + excluded + "/", StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(names, name =>
            PackageExclusions.IsExcludedFile(name[(name.LastIndexOf('/') + 1)..]));
    }

    [Fact]
    public async Task Build_ExcludesLinkedWorktreeGitFile_WithoutLeakingItsGitDir()
    {
        using var linkedWorktree = new TempDirectory();
        using var ordinaryCheckout = new TempDirectory();

        var linkedSource = TestProjectSource.CreateWithoutExcludedContent(
            linkedWorktree.Combine("project"));
        var ordinarySource = TestProjectSource.CreateWithoutExcludedContent(
            ordinaryCheckout.Combine("project"));
        var gitDirectory = linkedWorktree.Combine("durable-clone", ".git", "worktrees", "project");
        await File.WriteAllTextAsync(
            Path.Combine(linkedSource, ".git"),
            $"gitdir: {gitDirectory}{Environment.NewLine}");

        var linked = await BuildAsync(linkedSource, linkedWorktree);
        var ordinary = await BuildAsync(ordinarySource, ordinaryCheckout);

        Assert.Equal(ordinary.Result.Sha256, linked.Result.Sha256);
        Assert.Equal(ordinary.Bytes, linked.Bytes);
        Assert.DoesNotContain(
            gitDirectory,
            Encoding.UTF8.GetString(linked.Bytes),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_StoresEveryEntry_WithHostNeutralFixedMetadata()
    {
        using var temp = new TempDirectory();
        var built = await BuildAsync(TestProjectSource.Create(temp.Combine("project")), temp);

        foreach (var entry in TestZipReader.Read(built.Bytes))
        {
            Assert.Equal(0, entry.Method);
            Assert.Equal(0, entry.LocalMethod);

            // Bit 11 (UTF-8 name) only: not encrypted, no data descriptor, no deflate level hints.
            Assert.Equal(0x0800, entry.Flags);
            Assert.Equal(0x0800, entry.LocalFlags);

            // 1980-01-01T00:00:00, in both headers.
            Assert.Equal(0x0000, entry.DosTime);
            Assert.Equal(0x0021, entry.DosDate);
            Assert.Equal(0x0000, entry.LocalDosTime);
            Assert.Equal(0x0021, entry.LocalDosDate);

            // Host 0 (MS-DOS/FAT) and no external attributes: no owner, permission, or executable bit
            // from the machine that built the package.
            Assert.Equal(20, entry.VersionMadeBy);
            Assert.Equal(0u, entry.ExternalAttributes);
            Assert.Equal(0, entry.LocalExtraLength);
        }
    }

    [Fact]
    public async Task Build_WritesNoLocalPath_IntoThePackage()
    {
        using var temp = new TempDirectory();
        var built = await BuildAsync(TestProjectSource.Create(temp.Combine("project")), temp);

        var text = Encoding.UTF8.GetString(built.Bytes);

        Assert.DoesNotContain(Path.GetFileName(temp.Path), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_RejectsASourceThatAlreadyCarriesTheEnvelope()
    {
        using var temp = new TempDirectory();
        var source = TestProjectSource.Create(temp.Combine("project"));
        await File.WriteAllTextAsync(
            Path.Combine(source, AgentPackageContract.EnvelopeFileName),
            "{\"packageVersion\":\"0\",\"packageType\":\"code\"}");

        var error = await Assert.ThrowsAsync<AgentPackageException>(() => BuildAsync(source, temp));

        Assert.Equal(AgentPackageRejection.EnvelopeSourceConflict, error.Rejection);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
    }

    [Fact]
    public async Task Build_RejectsALinkInTheSourceTree()
    {
        using var temp = new TempDirectory();
        var source = TestProjectSource.Create(temp.Combine("project"));

        var link = Path.Combine(source, "src", "linked.js");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(source, "src", "agent.js"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating a symbolic link needs Developer Mode or elevation on Windows, and this suite
            // never elevates. There is no archive-side counterpart to fall back on: version zero
            // does not classify entry file types, because nothing here expands a package.
            return;
        }

        var error = await Assert.ThrowsAsync<AgentPackageException>(() => BuildAsync(source, temp));

        Assert.Equal(AgentPackageRejection.SourceLink, error.Rejection);
    }

    [Fact]
    public async Task Build_RejectsMoreEntriesThanTheLimitAllows()
    {
        using var temp = new TempDirectory();
        var source = temp.Combine("project");
        Directory.CreateDirectory(source);
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{i}.txt"), "x");
        }

        var error = await Assert.ThrowsAsync<AgentPackageException>(
            () => BuildAsync(source, temp, AgentPackageLimits.V0 with { MaxEntryCount = 3 }));

        Assert.Equal(AgentPackageRejection.EntryCountExceeded, error.Rejection);
    }

    [Fact]
    public async Task Build_RejectsAnArchiveLargerThanTheLimitAllows_AndLeavesNothingBehind()
    {
        using var temp = new TempDirectory();
        var source = TestProjectSource.Create(temp.Combine("project"));

        var error = await Assert.ThrowsAsync<AgentPackageException>(
            () => BuildAsync(source, temp, AgentPackageLimits.V0 with { MaxArchiveBytes = 256 }));

        Assert.Equal(AgentPackageRejection.ArchiveSizeExceeded, error.Rejection);
        Assert.False(File.Exists(temp.Combine("out", AgentPackageContract.ArchiveFileName)));
    }

    [Fact]
    public async Task Build_RequiresAnExistingSourceDirectory()
    {
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<UsageException>(
            () => BuildAsync(temp.Combine("missing"), temp));

        Assert.Equal(ExitCodes.Usage, error.ExitCode);
    }

    private static async Task<BuiltPackage> BuildAsync(
        string source,
        TempDirectory temp,
        AgentPackageLimits? limits = null)
    {
        var destination = temp.Combine("out", AgentPackageContract.ArchiveFileName);
        var builder = limits is null
            ? new DeterministicAgentPackageBuilder()
            : new DeterministicAgentPackageBuilder(limits);

        var result = await builder.BuildAsync(
            new AgentPackageBuildRequest { SourceDirectory = source, DestinationPath = destination },
            CancellationToken.None);

        return new BuiltPackage(result, await File.ReadAllBytesAsync(destination));
    }

    private sealed record BuiltPackage(AgentPackageBuildResult Result, byte[] Bytes);
}
