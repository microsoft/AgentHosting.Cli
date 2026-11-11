using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// The Dataverse Web API request plumbing every Copilot Studio adapter shares.
/// </summary>
public sealed class DataverseWebApiClient
{
    private const int MaximumCollectionPages = 1000;

    private readonly ISyncDataverseClient _dataverseClient;
    private readonly AgentHostDataverseHttpClientAccessor _httpClientAccessor;

    /// <summary>Creates the client.</summary>
    /// <param name="dataverseClient">The sync library's Dataverse client, targeted before each request.</param>
    /// <param name="httpClientAccessor">Creates authenticated clients for the target organization.</param>
    public DataverseWebApiClient(ISyncDataverseClient dataverseClient, AgentHostDataverseHttpClientAccessor httpClientAccessor)
    {
        _dataverseClient = dataverseClient ?? throw new ArgumentNullException(nameof(dataverseClient));
        _httpClientAccessor = httpClientAccessor ?? throw new ArgumentNullException(nameof(httpClientAccessor));
    }

    /// <summary>Targets the organization behind an environment without issuing a request.</summary>
    /// <param name="environment">The environment to target.</param>
    public void Connect(CopilotEnvironmentContext environment) => DataverseTargeting.Connect(_dataverseClient, _httpClientAccessor, environment);

    /// <summary>Deletes the connection reference with a logical name, reporting whether the environment held one.</summary>
    /// <param name="environment">The environment whose organization is addressed.</param>
    /// <param name="logicalName">The connection reference's logical name.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns><see langword="true"/> when a connection reference was deleted.</returns>
    public async Task<bool> DeleteConnectionReferenceAsync(CopilotEnvironmentContext environment, string logicalName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logicalName);

        Connect(environment);

        var recordId = (await _dataverseClient.GetConnectionReferencesByLogicalNamesAsync([logicalName], cancellationToken).ConfigureAwait(false)).Where(row => logicalName.Equals(row.ConnectionReferenceLogicalName, StringComparison.OrdinalIgnoreCase)).Select(row => row.ConnectionReferenceId).FirstOrDefault();

        if (recordId == Guid.Empty)
        {
            return false;
        }

        (await SendAsync(environment, HttpMethod.Delete, $"api/data/v9.2/connectionreferences({recordId})", cancellationToken).ConfigureAwait(false))?.Dispose();

        return true;
    }

    /// <summary>Sends one request against the environment's organization.</summary>
    /// <param name="environment">The environment whose organization is addressed.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="relativeUrl">The url, relative to the organization root, or an absolute page link.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="jsonBody">The request body, when the method carries one.</param>
    /// <returns>The parsed response, or <see langword="null"/> for an empty body or a 404.</returns>
    public async Task<JsonDocument?> SendAsync(CopilotEnvironmentContext environment, HttpMethod method, string relativeUrl, CancellationToken cancellationToken, string? jsonBody = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        Connect(environment);

        using var client = _httpClientAccessor.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(environment.DataverseOrganization, relativeUrl));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Accept", "application/json");

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CopilotStudioException.ServiceError($"{method.Method} {relativeUrl}", await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
    }

    /// <summary>Reads every row of a collection, following the service's own paging links.</summary>
    /// <typeparam name="T">The projected row type.</typeparam>
    /// <param name="environment">The environment whose organization is addressed.</param>
    /// <param name="relativeUrl">The collection url.</param>
    /// <param name="map">Projects one row.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="maximumCount">Stops once this many rows have been read, or <see langword="null"/> to read them all.</param>
    /// <returns>The projected rows.</returns>
    public async Task<IReadOnlyList<T>> ReadCollectionAsync<T>(CopilotEnvironmentContext environment, string relativeUrl, Func<JsonElement, T> map, CancellationToken cancellationToken, int? maximumCount = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(map);

        var rows = new List<T>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? page = relativeUrl;

        for (var pages = 0; page is not null; pages++)
        {
            if (pages >= MaximumCollectionPages)
            {
                throw CopilotStudioException.ServiceError($"GET {relativeUrl}", $"The service returned more than {MaximumCollectionPages} pages.");
            }

            using var document = await SendAsync(environment, HttpMethod.Get, page, cancellationToken).ConfigureAwait(false);

            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                throw CopilotStudioException.ServiceError($"GET {page}", "The service did not return a collection page carrying a 'value' array. An empty collection is reported as an empty array, so this is a failed read rather than an empty result.");
            }

            rows.AddRange(values.EnumerateArray().Select(map));

            if (maximumCount > 0 && rows.Count >= maximumCount)
            {
                return [.. rows.Take(maximumCount.Value)];
            }

            page = NextPageOrNull(environment, document.RootElement, visited);
        }

        return rows;
    }

    /// <summary>
    /// The next page's url, when the service supplied one this client may follow.
    /// </summary>
    private static string? NextPageOrNull(CopilotEnvironmentContext environment, JsonElement document, HashSet<string> visited)
    {
        if (!document.TryGetProperty("@odata.nextLink", out var link))
        {
            return null;
        }

        if (link.ValueKind != JsonValueKind.String || link.GetString() is not { Length: > 0 } next)
        {
            throw CopilotStudioException.ServiceError("GET collection page", "The service supplied a pagination link that is not a url, so the collection cannot be read to its end.");
        }

        RequireSameOrigin(environment, next);

        return visited.Add(Canonical(environment, next))
            ? next
            : throw CopilotStudioException.ServiceError($"GET {next}", "The service returned a pagination link that had already been followed.");
    }

    private static void RequireSameOrigin(CopilotEnvironmentContext environment, string next)
    {
        if (!Uri.TryCreate(next, UriKind.Absolute, out var absolute))
        {
            return;
        }

        var organization = environment.DataverseOrganization;

        if (!absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(absolute.UserInfo)
            || !absolute.Host.Equals(organization.Host, StringComparison.OrdinalIgnoreCase)
            || absolute.Port != organization.Port)
        {
            throw CopilotStudioException.UntrustedDataverseOrigin(absolute.GetLeftPart(UriPartial.Authority), organization.GetLeftPart(UriPartial.Authority));
        }
    }

    /// <summary>
    /// A page url reduced to the form two links may be compared in.
    /// </summary>
    private static string Canonical(CopilotEnvironmentContext environment, string url) =>
        Uri.TryCreate(environment.DataverseOrganization, url, out var absolute)
            ? $"{absolute.Scheme.ToLowerInvariant()}://{absolute.Host.ToLowerInvariant()}:{absolute.Port}{absolute.PathAndQuery}"
            : url;

    /// <summary>Describes a failed response without echoing the bearer token or the raw body.</summary>
    /// <param name="response">The failed response.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The redacted description.</returns>
    public static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? response.StatusCode.ToString()}.";
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {
            return status;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return status;
            }

            var message = ReadString(error, "message");
            var code = ReadString(error, "code");

            return string.IsNullOrWhiteSpace(message) ? status : string.IsNullOrWhiteSpace(code) ? $"{status} {message}" : $"{status} {message} ({code})";
        }
        catch (JsonException)
        {
            return status;
        }
    }

    /// <summary>Reads a string property, or <see langword="null"/> when it is absent or not a string.</summary>
    /// <param name="element">The object to read.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Renders a completed OData filter as a query-string value.
    /// </summary>
    /// <param name="filter">The completed filter expression.</param>
    /// <returns>The encoded <c>$filter</c> query-string segment, without a leading separator.</returns>
    public static string ToFilterQuery(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return $"$filter={Uri.EscapeDataString(filter)}";
    }
}
