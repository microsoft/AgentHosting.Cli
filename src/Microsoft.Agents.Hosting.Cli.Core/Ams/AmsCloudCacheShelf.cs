using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal sealed record AmsCloudCacheCopy(string Commit, string? Branch, DateTimeOffset SavedAt);

internal sealed class AmsCloudCacheIndex
{
    public string Folder { get; set; } = string.Empty;

    public string? Identity { get; set; }

    public string? Live { get; set; }

    public List<AmsCloudCacheCopy> Copies { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AmsCloudCacheIndex))]
internal sealed partial class AmsCloudCacheJsonContext : JsonSerializerContext
{
}

internal sealed record AmsCloudCachePlace(string Root, string Folder, string Head, string? Branch, string KeyDirectory, string Live);

internal sealed record AmsCloudCacheGap(AmsCloudCachePlace Place, AmsCloudCacheIndex Index, bool Clean, AmsCloudCacheCopy? Behind, AmsCloudCacheCopy Newest);

/// <summary>Keeps a copy of each Copilot Studio folder's cloud cache for every git commit the folder synced at, under the working tree's own git folder, and puts back the copy that belongs to the commit the folder holds.</summary>
public sealed class AmsCloudCacheShelf
{
    private const int MaximumCopies = 20;

    private const string IndexFileName = "index.json";

    private readonly IGitPort _git;
    private readonly IClock _clock;

    /// <summary>Creates the shelf.</summary>
    /// <param name="git">Reads the working tree each folder is in.</param>
    /// <param name="clock">Stamps each copy with when it was saved.</param>
    public AmsCloudCacheShelf(IGitPort git, IClock clock)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal static string IdentityOf(CopilotWorkspaceInfo workspace) => $"{workspace.EnvironmentId}|{workspace.AgentId}";

    internal static async Task<string?> ReadIdentityAsync(ICopilotWorkspacePort workspaces, string folder, CancellationToken cancellationToken)
    {
        try
        {
            return IdentityOf(await workspaces.ReadWorkspaceInfoAsync(folder, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    internal async Task<AmsCloudCacheGap?> PrepareAsync(string folder, string? identity, CancellationToken cancellationToken)
    {
        var decision = await DecideAsync(folder, identity, cancellationToken).ConfigureAwait(false);
        if (decision is { Target: { } target })
        {
            PutInPlace(decision.Place, decision.Index, target);
        }

        return decision?.Gap;
    }

    internal async Task RestoreAsync(AmsCloudCacheGap gap, AmsCloudCacheCopy copy, CancellationToken cancellationToken)
    {
        PutInPlace(gap.Place, gap.Index, copy);
        await _git.RestoreAsync(gap.Place.Root, copy.Commit, [gap.Place.Folder], cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string?> SaveAsync(string folder, string? identity, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(LiveOf(folder)) || await LocateAsync(folder, cancellationToken).ConfigureAwait(false) is not { } place)
        {
            return null;
        }

        try
        {
            await StoreAsync(place, identity, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return $"msagent couldn't save a copy of {TerminalText.Sanitize(place.Folder)}'s cloud cache: {failure.Message}";
        }
    }

    internal async Task<string?> SaveConnectedAsync(ICopilotWorkspacePort workspaces, string folder, CancellationToken cancellationToken) =>
        await ReadIdentityAsync(workspaces, folder, cancellationToken).ConfigureAwait(false) is { } identity ? await SaveAsync(folder, identity, cancellationToken).ConfigureAwait(false) : null;

    internal async Task ForgetAsync(string folder, CancellationToken cancellationToken)
    {
        if (await LocateAsync(folder, cancellationToken).ConfigureAwait(false) is { } place)
        {
            AmsFileTree.Delete(place.KeyDirectory);
        }
    }

    private async Task<Decision?> DecideAsync(string folder, string? identity, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(LiveOf(folder)) || await LocateAsync(folder, cancellationToken).ConfigureAwait(false) is not { } place)
        {
            return null;
        }

        try
        {
            if (ReadIndex(place.KeyDirectory) is not { } index || !Fits(index, identity) || Saved(place, index) is not [var newest, ..] copies)
            {
                return null;
            }

            return (copies.Find(copy => Same(copy.Commit, place.Head)) ?? await NearestAncestorAsync(place, copies, cancellationToken).ConfigureAwait(false)) is { } target
                ? new(place, index, target, null)
                : new(place, index, null, new(place, index, await IsCleanAsync(place, cancellationToken).ConfigureAwait(false), await BehindAsync(place, copies, cancellationToken).ConfigureAwait(false), newest));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<AmsCloudCachePlace?> LocateAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (await _git.FindWorkingTreeAsync(NearestExisting(full), cancellationToken).ConfigureAwait(false) is not { HasCommits: true } tree
                || await _git.ResolveCommitAsync(tree.Root, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head)
            {
                return null;
            }

            var relative = Path.GetRelativePath(tree.Root, full).Replace(Path.DirectorySeparatorChar, '/');
            var gitDirectory = await _git.GitDirectoryAsync(tree.Root, cancellationToken).ConfigureAwait(false);
            return new(tree.Root, relative, head, tree.Branch, Path.Combine(gitDirectory, "msagent", "mcs", Key(relative)), Path.Combine(full, WorkflowStateCache.FolderName));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<AmsCloudCacheCopy?> NearestAncestorAsync(AmsCloudCachePlace place, List<AmsCloudCacheCopy> copies, CancellationToken cancellationToken)
    {
        AmsCloudCacheCopy? best = null;
        foreach (var copy in copies)
        {
            if (await IsAncestorAsync(place, copy.Commit, place.Head, cancellationToken).ConfigureAwait(false) && (best is null || await IsAncestorAsync(place, best.Commit, copy.Commit, cancellationToken).ConfigureAwait(false)))
            {
                best = copy;
            }
        }

        return best;
    }

    private async Task<AmsCloudCacheCopy?> BehindAsync(AmsCloudCachePlace place, List<AmsCloudCacheCopy> copies, CancellationToken cancellationToken)
    {
        foreach (var copy in copies)
        {
            if (await IsAncestorAsync(place, place.Head, copy.Commit, cancellationToken).ConfigureAwait(false))
            {
                return copy;
            }
        }

        return null;
    }

    private async Task<bool> IsAncestorAsync(AmsCloudCachePlace place, string ancestor, string descendant, CancellationToken cancellationToken) =>
        await _git.MergeBaseAsync(place.Root, ancestor, descendant, cancellationToken).ConfigureAwait(false) is { } mergeBase && Same(mergeBase, ancestor);

    private async Task<bool> IsCleanAsync(AmsCloudCachePlace place, CancellationToken cancellationToken) =>
        (await _git.StatusAsync(place.Root, [place.Folder], cancellationToken).ConfigureAwait(false)).Count == 0;

    private async Task StoreAsync(AmsCloudCachePlace place, string? identity, CancellationToken cancellationToken)
    {
        var index = ReadIndex(place.KeyDirectory);
        if (index is null || !Fits(index, identity))
        {
            AmsFileTree.Delete(place.KeyDirectory);
            index = new AmsCloudCacheIndex { Folder = place.Folder, Identity = identity };
        }

        var others = index.Copies.Where(saved => !Same(saved.Commit, place.Head)).OrderByDescending(saved => saved.SavedAt).ToList();
        var kept = await CopiesToKeepAsync(place, others, cancellationToken).ConfigureAwait(false);
        var copy = CopyOf(place, place.Head);
        var temporary = copy + ".tmp";
        AmsFileTree.Delete(temporary);
        Directory.CreateDirectory(temporary);
        foreach (var entry in Entries(place.Live))
        {
            AmsFileTree.Copy(entry, Path.Combine(temporary, Path.GetFileName(entry)));
        }

        AmsFileTree.Delete(copy);
        Directory.Move(temporary, copy);

        foreach (var dropped in others.Except(kept))
        {
            AmsFileTree.Delete(CopyOf(place, dropped.Commit));
        }

        index.Live = place.Head;
        index.Copies = [new(place.Head, place.Branch, _clock.UtcNow), .. kept];
        WriteIndex(place.KeyDirectory, index);
    }

    private async Task<List<AmsCloudCacheCopy>> CopiesToKeepAsync(AmsCloudCachePlace place, List<AmsCloudCacheCopy> others, CancellationToken cancellationToken)
    {
        if (others.Count < MaximumCopies)
        {
            return others;
        }

        var branches = await LocalBranchesOrNoneAsync(place.Root, cancellationToken).ConfigureAwait(false);
        var floors = others.Where(copy => copy.Branch is { } branch && branch != place.Branch && branches.Contains(branch)).DistinctBy(copy => copy.Branch).Take(MaximumCopies - 1).ToList();
        return others.FindAll(floors.Concat(others.Except(floors).Take(MaximumCopies - 1 - floors.Count)).ToHashSet().Contains);
    }

    private async Task<IReadOnlyList<string>> LocalBranchesOrNoneAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            return await _git.ListLocalBranchesAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return [];
        }
    }

    private static void PutInPlace(AmsCloudCachePlace place, AmsCloudCacheIndex index, AmsCloudCacheCopy copy)
    {
        if (Same(copy.Commit, index.Live))
        {
            return;
        }

        foreach (var entry in Entries(place.Live).ToList())
        {
            AmsFileTree.Delete(entry);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(CopyOf(place, copy.Commit)))
        {
            AmsFileTree.Copy(entry, Path.Combine(place.Live, Path.GetFileName(entry)));
        }

        index.Live = copy.Commit;
        WriteIndex(place.KeyDirectory, index);
    }

    private static AmsCloudCacheIndex? ReadIndex(string keyDirectory)
    {
        var path = Path.Combine(keyDirectory, IndexFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), AmsCloudCacheJsonContext.Default.AmsCloudCacheIndex) is { Copies: { } copies } index && copies.TrueForAll(copy => copy is not null && IsCommit(copy.Commit)) ? index : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteIndex(string keyDirectory, AmsCloudCacheIndex index)
    {
        Directory.CreateDirectory(keyDirectory);
        var path = Path.Combine(keyDirectory, IndexFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index, AmsCloudCacheJsonContext.Default.AmsCloudCacheIndex));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static List<AmsCloudCacheCopy> Saved(AmsCloudCachePlace place, AmsCloudCacheIndex index) =>
        [.. index.Copies.Where(copy => Directory.Exists(CopyOf(place, copy.Commit))).OrderByDescending(copy => copy.SavedAt)];

    private static IEnumerable<string> Entries(string live) =>
        Directory.EnumerateFileSystemEntries(live).Where(entry => !string.Equals(Path.GetFileName(entry), WorkflowStateCache.FileName, StringComparison.Ordinal));

    private static bool Fits(AmsCloudCacheIndex index, string? identity) => identity is null || string.Equals(identity, index.Identity, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string commit, string? other) => string.Equals(commit, other, StringComparison.OrdinalIgnoreCase);

    private static bool IsCommit(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static string CopyOf(AmsCloudCachePlace place, string commit) => Path.Combine(place.KeyDirectory, commit);

    private static string LiveOf(string folder) => Path.Combine(folder, WorkflowStateCache.FolderName);

    private static string NearestExisting(string path) => Directory.Exists(path) || Path.GetDirectoryName(path) is not { } parent ? path : NearestExisting(parent);

    private static string Key(string folder) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..16];

    private sealed record Decision(AmsCloudCachePlace Place, AmsCloudCacheIndex Index, AmsCloudCacheCopy? Target, AmsCloudCacheGap? Gap);
}
