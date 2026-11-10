using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal sealed class UpdateCheckService
{
    private readonly NuGetUpdateClient _client;
    private readonly string _cachePath;
    private readonly string _informationalVersion;
    private readonly TimeProvider _time;

    internal UpdateCheckService(NuGetUpdateClient client, string configDirectory, string informationalVersion, TimeProvider time)
    {
        _client = client;
        _cachePath = Path.Combine(configDirectory, "updates", "version-check.json");
        _informationalVersion = informationalVersion;
        _time = time;
    }

    internal async Task NotifyAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseline = _informationalVersion.Split('+', 2)[0];
        if (!NuGetVersion.TryParse(baseline, out var current))
        {
            return;
        }

        var cache = await ReadCacheAsync(cancellationToken);
        AvailableVersions? versions;
        if (cache is not null
            && _time.GetUtcNow() - cache.CheckedAt < (cache.Versions is null ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24)))
        {
            versions = cache.Versions;
        }
        else
        {
            versions = await _client.GetAsync(cancellationToken);
            await WriteCacheAsync(new UpdateCheckCache(1, NuGetUpdateClient.PackageId, NuGetUpdateClient.Source, _time.GetUtcNow(), versions), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (versions is null)
        {
            return;
        }

        var stable = NuGetVersion.TryParse(versions.Stable, out var parsedStable) ? parsedStable : null;
        var preview = NuGetVersion.TryParse(versions.Prerelease, out var parsedPreview) ? parsedPreview : null;
        var notice = new List<string>();
        if (stable is not null && VersionComparer.VersionRelease.Compare(stable, current) > 0)
        {
            notice.Add($"A new msagent version is available: {stable.ToNormalizedString()} (installed: {baseline}).");
            notice.Add($"Update: dotnet tool update {NuGetUpdateClient.PackageId} --global");
        }

        if (preview is not null
            && VersionComparer.VersionRelease.Compare(preview, current) > 0
            && VersionComparer.VersionRelease.Compare(preview, stable) > 0)
        {
            notice.Add($"A new msagent version is available: {preview.ToNormalizedString()} (prerelease) (installed: {baseline}).");
            notice.Add($"Preview update: dotnet tool update {NuGetUpdateClient.PackageId} --global --prerelease");
        }

        if (notice.Count > 0)
        {
            try
            {
                await writer.WriteLineAsync(string.Join(Environment.NewLine, notice).AsMemory(), cancellationToken);
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    internal static bool ShouldCheck(bool outputRedirected, bool errorRedirected, string? ci, string? skip) =>
        !outputRedirected
        && ConsoleProgressPolicy.ShouldShowProgress(errorRedirected, ci)
        && !string.Equals(skip, "true", StringComparison.OrdinalIgnoreCase)
        && skip != "1";

    private async Task<UpdateCheckCache?> ReadCacheAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
            using var document = await NuGetUpdateClient.ReadDocumentAsync(stream, token);
            var cache = document?.Deserialize(UpdateCheckJsonContext.Default.UpdateCheckCache);
            if (cache is null || cache.SchemaVersion != 1
                || cache.PackageId != NuGetUpdateClient.PackageId || cache.Source != NuGetUpdateClient.Source
                || cache.CheckedAt < DateTimeOffset.UnixEpoch || cache.CheckedAt > _time.GetUtcNow())
            {
                return null;
            }

            if (cache.Versions is { } versions
                && ((versions.Stable is null && versions.Prerelease is null)
                    || (versions.Stable is not null && (!NuGetVersion.TryParse(versions.Stable, out var stable) || stable.IsPrerelease))
                    || (versions.Prerelease is not null && (!NuGetVersion.TryParse(versions.Prerelease, out var preview) || !preview.IsPrerelease))))
            {
                return null;
            }

            return cache;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task WriteCacheAsync(UpdateCheckCache cache, CancellationToken token)
    {
        var temporary = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(cache, UpdateCheckJsonContext.Default.UpdateCheckCache), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal sealed record UpdateCheckCache(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Source,
    [property: JsonRequired] DateTimeOffset CheckedAt,
    [property: JsonRequired] AvailableVersions? Versions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateCheckCache))]
internal sealed partial class UpdateCheckJsonContext : JsonSerializerContext;
