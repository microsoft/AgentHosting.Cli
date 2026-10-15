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

    public async Task<List<JsonObject>> ReadCollectionAsync(
        string path,
        IReadOnlyList<string> scopes,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var values = new List<JsonObject>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? page = path;
        for (var pageNumber = 0; page is not null && pageNumber < MaxCollectionPages; pageNumber++)
        {
            if (!visited.Add(page))
            {
                throw MalformedCollection("The identity service returned a repeated paging link.");
            }

            var response = await SendAsync(
                HttpMethod.Get,
                page,
                body: null,
                scopes,
                tenantId,
                cancellationToken).ConfigureAwait(false);
            var root = BlueprintIdentityJson.ParseObject(response.Body);
            if (!root.TryGetPropertyValue("value", out var valueNode) ||
                valueNode is not JsonArray pageValues)
            {
                throw MalformedCollection(
                    "The identity service returned a collection without a valid value array.");
            }

            foreach (var value in pageValues)
            {
                if (value is not JsonObject item)
                {
                    throw MalformedCollection(
                        "The identity service returned a collection with a non-object value.");
                }

                values.Add(item);
            }
            page = BlueprintIdentityJson.ReadString(root, "@odata.nextLink");
        }

        if (page is not null)
        {
            throw MalformedCollection(
                $"The identity service returned more than {MaxCollectionPages} collection pages.");
        }

        return values;
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
