using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal sealed class FileTelemetryCollectorCache
{
    private const int SchemaVersion = 1;
    private const int PolicyVersion = 1;
    private const int MaximumEntries = 128;
    private const int MaximumBytes = 64 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly string _cachePath;
    private readonly TimeProvider _time;
    private readonly TelemetryRoutingDiagnostics? _diagnostics;

    internal FileTelemetryCollectorCache(string configDirectory, TimeProvider time,
        TelemetryRoutingDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _cachePath = Path.Combine(configDirectory, "telemetry", "collector-cache.json");
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _diagnostics = diagnostics;
    }

    internal async Task<TelemetryLocation?> ReadAsync(PowerPlatformApiTargetConfig target,
        ResolvedEnvironmentTarget environment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        string key = CacheKey(target, environment);
        var entry = entries.Find(candidate => candidate.Key == key);
        cancellationToken.ThrowIfCancellationRequested();
        if (entry is null)
        {
            _diagnostics?.Write("cache-miss");
            return null;
        }
        if (_time.GetUtcNow() - entry.CheckedAt >= Lifetime)
        {
            _diagnostics?.Write("cache-expired");
            return null;
        }

        _diagnostics?.Write("cache-hit", "collector=" + entry.Collector);
        return entry.Collector == "EU" ? TelemetryLocation.Europe : TelemetryLocation.Global;
    }

    internal async Task WriteAsync(PowerPlatformApiTargetConfig target, ResolvedEnvironmentTarget environment,
        TelemetryLocation location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string collector = location switch
        {
            TelemetryLocation.Europe => "EU",
            TelemetryLocation.Global => "Global",
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        var entries = await ReadEntriesAsync(cancellationToken).ConfigureAwait(false);
        string key = CacheKey(target, environment);
        DateTimeOffset now = _time.GetUtcNow();
        entries.RemoveAll(entry => entry.Key == key || now - entry.CheckedAt >= Lifetime);
        entries = entries.OrderByDescending(entry => entry.CheckedAt).Take(MaximumEntries - 1).ToList();
        entries.Add(new(key, collector, now));
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new TelemetryCollectorCacheDocument(SchemaVersion, PolicyVersion, entries),
            TelemetryCollectorCacheJsonContext.Default.TelemetryCollectorCacheDocument);
        string temporary = _cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllBytesAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _cachePath, overwrite: true);
            _diagnostics?.Write("cache-written", "collector=" + collector);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportFailure("cache-write-failed");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                ReportFailure("cache-cleanup-failed");
            }
        }
    }

    private async Task<List<TelemetryCollectorCacheEntry>> ReadEntriesAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, useAsync: true);
            byte[] buffer = new byte[MaximumBytes + 1];
            int length = 0;
            int read;
            while (length < buffer.Length
                && (read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false)) != 0)
            {
                length += read;
            }
            token.ThrowIfCancellationRequested();
            if (length > MaximumBytes)
                return InvalidCache();

            var document = JsonSerializer.Deserialize(buffer.AsSpan(0, length),
                TelemetryCollectorCacheJsonContext.Default.TelemetryCollectorCacheDocument);
            DateTimeOffset now = _time.GetUtcNow();
            if (document is null || document.SchemaVersion != SchemaVersion
                || document.PolicyVersion != PolicyVersion || document.Entries is null
                || document.Entries.Count > MaximumEntries)
                return InvalidCache();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry is null || entry.Key is null || entry.Key.Length != 64
                    || !entry.Key.All(char.IsAsciiHexDigit) || !keys.Add(entry.Key)
                    || entry.Collector is not ("EU" or "Global")
                    || entry.CheckedAt < DateTimeOffset.UnixEpoch || entry.CheckedAt > now)
                    return InvalidCache();
            }
            return document.Entries;
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (JsonException)
        {
            token.ThrowIfCancellationRequested();
            return InvalidCache();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            ReportFailure("cache-read-failed");
            return [];
        }
    }

    private List<TelemetryCollectorCacheEntry> InvalidCache()
    {
        ReportFailure("cache-invalid");
        return [];
    }

    private void ReportFailure(string stage)
    {
        _diagnostics?.Write(stage);
        OptionalMeasurementDiagnostics.Report(OptionalMeasurementFailureCategory.TelemetryCollectorCache);
    }

    private static string CacheKey(PowerPlatformApiTargetConfig target, ResolvedEnvironmentTarget environment)
    {
        string tenant = environment.TenantId.ToUpperInvariant();
        string id = environment.EnvironmentId.ToUpperInvariant();
        string key = FormattableString.Invariant($"{target.Cloud}:{target.Ring}:{tenant.Length}:{tenant}:{id.Length}:{id}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

internal sealed record TelemetryCollectorCacheEntry(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string Collector,
    [property: JsonRequired] DateTimeOffset CheckedAt);

internal sealed record TelemetryCollectorCacheDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] int PolicyVersion,
    [property: JsonRequired] List<TelemetryCollectorCacheEntry>? Entries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 16)]
[JsonSerializable(typeof(TelemetryCollectorCacheDocument))]
internal sealed partial class TelemetryCollectorCacheJsonContext : JsonSerializerContext;
