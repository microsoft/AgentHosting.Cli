using System.Net;
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

    /// <summary>Sends one request against the environment's organization.</summary>
    /// <param name="environment">The environment whose organization is addressed.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="relativeUrl">The url, relative to the organization root, or an absolute page link.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The parsed response, or <see langword="null"/> for an empty body or a 404.</returns>
    public async Task<JsonDocument?> SendAsync(CopilotEnvironmentContext environment, HttpMethod method, string relativeUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);

        Connect(environment);

        using var client = _httpClientAccessor.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(environment.DataverseOrganization, relativeUrl));
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("Accept", "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CopilotStudioException.ServiceError($"{method.Method} {relativeUrl}", new DataverseRequestException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Describe());
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

        try
        {
            return await DataverseCollectionPager.ReadAsync(environment.DataverseOrganization, relativeUrl, (page, token) => SendAsync(environment, HttpMethod.Get, page, token), map, maximumCount, cancellationToken).ConfigureAwait(false);
        }
        catch (DataverseCollectionPageException failure)
        {
            throw failure.Problem switch
            {
                DataverseCollectionPageProblem.TooManyPages => CopilotStudioException.ServiceError($"GET {relativeUrl}", $"The service returned more than {DataverseCollectionPager.MaximumPages} pages."),
                DataverseCollectionPageProblem.NotACollection => CopilotStudioException.ServiceError($"GET {failure.Url}", "The service did not return a collection page carrying a 'value' array. An empty collection is reported as an empty array, so this is a failed read rather than an empty result."),
                DataverseCollectionPageProblem.NextLinkNotAUrl => CopilotStudioException.ServiceError("GET collection page", "The service supplied a pagination link that is not a url, so the collection cannot be read to its end."),
                DataverseCollectionPageProblem.NextLinkLeavesOrganization => CopilotStudioException.UntrustedDataverseOrigin(failure.LinkOrigin!, failure.OrganizationOrigin!),
                _ => CopilotStudioException.ServiceError($"GET {failure.Url}", "The service returned a pagination link that had already been followed."),
            };
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
