using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>Authenticated Graph request and collection-paging boundary for identity reconcilers.</summary>
internal sealed class IdentityGraphClient
{
    private const int MaxCollectionPages = 100;
    private readonly IIdentityRestClient _rest;

    public IdentityGraphClient(IIdentityRestClient rest)
    {
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
    }

    public async Task<IdentityRestResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyList<string> scopes,
        string tenantId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await _rest.SendGraphAsync(
            method,
            path,
            body,
            scopes,
            tenantId,
            cancellationToken,
            headers).ConfigureAwait(false);
        EnsureSuccess(response, method, path);
        return response;
    }

    public async Task<IdentityRestResponse?> TryGetAsync(
        string path,
        IReadOnlyList<string> scopes,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var response = await _rest.SendGraphAsync(
            HttpMethod.Get,
            path,
            body: null,
            scopes,
            tenantId,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == 404)
        {
            return null;
        }

        EnsureSuccess(response, HttpMethod.Get, path);
        return response;
    }

    public async Task<List<JsonObject>> ReadCollectionAsync(string path, IReadOnlyList<string> scopes, string tenantId, CancellationToken cancellationToken) => await EnumerateCollectionAsync(path, scopes, tenantId, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Streams a collection's values, requesting each further page only once the caller reads past the previous one.</summary>
    /// <param name="path">The collection to read.</param>
    /// <param name="scopes">The Graph scopes each page is requested with.</param>
    /// <param name="tenantId">The tenant to read from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The collection's values, in the order the service returns them.</returns>
    public async IAsyncEnumerable<JsonObject> EnumerateCollectionAsync(string path, IReadOnlyList<string> scopes, string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? page = path;
        for (var pageNumber = 0; page is not null && pageNumber < MaxCollectionPages; pageNumber++)
        {
            if (!visited.Add(page))
            {
                throw MalformedCollection("The identity service returned a repeated paging link.");
            }

            var root = BlueprintIdentityJson.ParseObject((await SendAsync(HttpMethod.Get, page, body: null, scopes, tenantId, cancellationToken).ConfigureAwait(false)).Body);
            if (!root.TryGetPropertyValue("value", out var valueNode) || valueNode is not JsonArray pageValues)
            {
                throw MalformedCollection("The identity service returned a collection without a valid value array.");
            }

            foreach (var value in pageValues)
            {
                yield return value as JsonObject ?? throw MalformedCollection("The identity service returned a collection with a non-object value.");
            }

            page = BlueprintIdentityJson.ReadString(root, "@odata.nextLink");
        }

        if (page is not null)
        {
            throw MalformedCollection($"The identity service returned more than {MaxCollectionPages} collection pages.");
        }
    }

    private static TargetRequestException MalformedCollection(string message) =>
        new(
            message,
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            status: 200);

    private static void EnsureSuccess(
        IdentityRestResponse response,
        HttpMethod method,
        string path)
    {
        if (!response.IsSuccess)
        {
            throw TargetRequestException.MapTargetResponseError(
                response.StatusCode,
                response.Body,
                method.Method,
                path,
                "Graph",
                "msagent");
        }
    }
}
