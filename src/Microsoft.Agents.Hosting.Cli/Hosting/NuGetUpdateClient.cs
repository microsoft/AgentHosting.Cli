using System.Text.Json;
using NuGet.Versioning;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal sealed record AvailableVersions(string? Stable, string? Prerelease);

internal sealed class NuGetUpdateClient
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    internal const string PackageId = "Microsoft.Agents.Hosting.Cli";
    internal const string Source = "https://api.nuget.org/v3/index.json";

    internal NuGetUpdateClient(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    internal async Task<AvailableVersions?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var index = await GetDocumentAsync(new Uri(Source), linked.Token);
            if (index is null
                || index.RootElement.ValueKind != JsonValueKind.Object
                || !index.RootElement.TryGetProperty("resources", out var resources)
                || resources.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            Uri? search = null;
            foreach (var resource in resources.EnumerateArray())
            {
                var type = StringProperty(resource, "@type");
                if ((type == "SearchQueryService" || type?.StartsWith("SearchQueryService/", StringComparison.Ordinal) == true)
                    && Uri.TryCreate(StringProperty(resource, "@id"), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
                    && uri.Host is "azuresearch-usnc.nuget.org" or "azuresearch-ussc.nuget.org")
                {
                    search = uri;
                    break;
                }
            }

            if (search is null)
            {
                return null;
            }

            var query = new UriBuilder(search)
            {
                Query = $"q=packageid%3A{PackageId}&prerelease=true&semVerLevel=2.0.0&take=1",
            };
            using var metadata = await GetDocumentAsync(query.Uri, linked.Token);
            if (metadata is null
                || metadata.RootElement.ValueKind != JsonValueKind.Object
                || !metadata.RootElement.TryGetProperty("data", out var packages)
                || packages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            NuGetVersion? stable = null;
            NuGetVersion? preview = null;
            foreach (var package in packages.EnumerateArray())
            {
                if (!string.Equals(StringProperty(package, "id"), PackageId, StringComparison.OrdinalIgnoreCase)
                    || !package.TryGetProperty("versions", out var versions)
                    || versions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in versions.EnumerateArray())
                {
                    if (!NuGetVersion.TryParse(StringProperty(entry, "version"), out var version))
                    {
                        continue;
                    }

                    if (version.IsPrerelease)
                    {
                        if (VersionComparer.VersionRelease.Compare(version, preview) > 0)
                        {
                            preview = version;
                        }
                    }
                    else if (VersionComparer.VersionRelease.Compare(version, stable) > 0)
                    {
                        stable = version;
                    }
                }
            }

            linked.Token.ThrowIfCancellationRequested();
            return stable is null && preview is null
                ? null
                : new AvailableVersions(stable?.ToNormalizedString(), preview?.ToNormalizedString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<JsonDocument?> GetDocumentAsync(Uri uri, CancellationToken token)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadDocumentAsync(stream, token);
    }

    internal static async Task<JsonDocument?> ReadDocumentAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + read > 1024 * 1024)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static string? StringProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
