using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;

namespace Microsoft.Agents.Hosting.Cli.Tests.Identity;

public sealed class IdentityGraphClientTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string FirstPage = "/v1.0/users?$filter=accountEnabled%20eq%20true";
    private const string SecondPage = "https://graph.microsoft.com/v1.0/users?$skiptoken=page-2";
    private const string ThirdPage = "https://graph.microsoft.com/v1.0/users?$skiptoken=page-3";

    private static readonly IReadOnlyList<string> Scopes = ["https://graph.microsoft.com/.default"];

    private static (IdentityGraphClient Graph, ScriptedIdentityRestClient RestClient) Create(params (string Path, IdentityRestResponse Response)[] pages)
    {
        var responses = pages.ToDictionary(page => page.Path, page => page.Response, StringComparer.Ordinal);
        var restClient = new ScriptedIdentityRestClient(call => responses.TryGetValue(call.Path, out var response) ? response : throw new Xunit.Sdk.XunitException($"Unexpected request: {call.Path}"));

        return (new IdentityGraphClient(restClient), restClient);
    }

    private static IdentityRestResponse Page(string? nextLink, params string[] objectIds)
    {
        var page = new JsonObject { ["value"] = new JsonArray([.. objectIds.Select(objectId => (JsonNode)new JsonObject { ["id"] = objectId })]) };

        if (nextLink is not null)
        {
            page["@odata.nextLink"] = nextLink;
        }

        return new IdentityRestResponse(200, page.ToJsonString());
    }

    private static IAsyncEnumerable<JsonObject> Enumerate(IdentityGraphClient graph) => graph.EnumerateCollectionAsync(FirstPage, Scopes, Tenant, CancellationToken.None);

    private static IEnumerable<string?> ObjectIdsOf(IEnumerable<JsonObject> values) => values.Select(value => (string?)value["id"]);

    [Fact]
    public async Task EnumerateCollection_FollowsEveryPagingLinkInOrder()
    {
        var (graph, restClient) = Create((FirstPage, Page(SecondPage, "a", "b")), (SecondPage, Page(ThirdPage, "c")), (ThirdPage, Page(null, "d")));

        Assert.Equal(["a", "b", "c", "d"], ObjectIdsOf(await Enumerate(graph).ToListAsync()));
        Assert.Equal([FirstPage, SecondPage, ThirdPage], restClient.Calls.Select(call => call.Path));
        Assert.All(restClient.Calls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal(Scopes, call.Scopes);
            Assert.Equal(Tenant, call.TenantId);
            Assert.Null(call.Body);
        });
    }

    [Fact]
    public async Task EnumerateCollection_RequestsNoFurtherPageOnceTheCallerStopsReading()
    {
        var (graph, restClient) = Create((FirstPage, Page(SecondPage, "a", "b")), (SecondPage, Page(null, "c")));

        Assert.Equal(["a", "b"], ObjectIdsOf(await Enumerate(graph).Take(2).ToListAsync()));
        Assert.Equal([FirstPage], restClient.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task EnumerateCollection_RequestsNothingUntilTheCallerStartsReading()
    {
        var (graph, restClient) = Create((FirstPage, Page(null, "a")));

        var values = Enumerate(graph);

        Assert.Empty(restClient.Calls);
        Assert.Equal(["a"], ObjectIdsOf(await values.ToListAsync()));
        Assert.Single(restClient.Calls);
    }

    [Fact]
    public async Task EnumerateCollection_FollowsAPagingLinkPastAnEmptyPage()
    {
        var (graph, _) = Create((FirstPage, Page(SecondPage)), (SecondPage, Page(null, "a")));

        Assert.Equal(["a"], ObjectIdsOf(await Enumerate(graph).ToListAsync()));
    }

    [Fact]
    public async Task EnumerateCollection_YieldsNothingForAnEmptyCollection()
    {
        var (graph, restClient) = Create((FirstPage, Page(null)));

        Assert.Empty(await Enumerate(graph).ToListAsync());
        Assert.Single(restClient.Calls);
    }

    [Fact]
    public async Task EnumerateCollection_YieldsTheEarlierPagesBeforeReportingAFailedOne()
    {
        var (graph, _) = Create((FirstPage, Page(SecondPage, "a")), (SecondPage, new IdentityRestResponse(503, """{"error":{"code":"ServiceUnavailable"}}""")));
        var objectIdsRead = new List<string?>();

        Assert.Equal(503, (await Assert.ThrowsAsync<TargetRequestException>(async () =>
        {
            await foreach (var value in Enumerate(graph))
            {
                objectIdsRead.Add((string?)value["id"]);
            }
        })).Status);
        Assert.Equal(["a"], objectIdsRead);
    }

    [Fact]
    public async Task EnumerateCollection_RejectsARepeatedPagingLink()
    {
        var (graph, restClient) = Create((FirstPage, Page(SecondPage, "a")), (SecondPage, Page(FirstPage, "b")));

        Assert.Contains("repeated paging link", (await Assert.ThrowsAsync<TargetRequestException>(() => Enumerate(graph).ToListAsync().AsTask())).Message, StringComparison.Ordinal);
        Assert.Equal(2, restClient.Calls.Count);
    }

    [Fact]
    public async Task EnumerateCollection_RejectsMoreThanAHundredPages()
    {
        var restClient = new ScriptedIdentityRestClient(call => Page($"{call.Path}&next", "a"));

        Assert.Contains("more than 100 collection pages", (await Assert.ThrowsAsync<TargetRequestException>(() => Enumerate(new IdentityGraphClient(restClient)).ToListAsync().AsTask())).Message, StringComparison.Ordinal);
        Assert.Equal(100, restClient.Calls.Count);
    }

    [Theory]
    [InlineData("{}", "without a valid value array")]
    [InlineData("""{"value":{}}""", "without a valid value array")]
    [InlineData("""{"value":[42]}""", "non-object value")]
    [InlineData("not json", "malformed JSON")]
    [InlineData("[]", "malformed JSON")]
    public async Task EnumerateCollection_RejectsAMalformedPage(string body, string expected) =>
        Assert.Contains(expected, (await Assert.ThrowsAsync<TargetRequestException>(() => Enumerate(Create((FirstPage, new IdentityRestResponse(200, body))).Graph).ToListAsync().AsTask())).Message, StringComparison.Ordinal);

    [Fact]
    public async Task ReadCollection_ReturnsEveryValueAcrossPages()
    {
        var (graph, _) = Create((FirstPage, Page(SecondPage, "a")), (SecondPage, Page(null, "b")));

        Assert.Equal(["a", "b"], ObjectIdsOf(await graph.ReadCollectionAsync(FirstPage, Scopes, Tenant, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadCollection_FailsInsteadOfReturningPartialValuesWhenALaterPageFails()
    {
        var (graph, _) = Create((FirstPage, Page(SecondPage, "a")), (SecondPage, new IdentityRestResponse(500, null)));

        Assert.Equal(500, (await Assert.ThrowsAsync<TargetRequestException>(() => graph.ReadCollectionAsync(FirstPage, Scopes, Tenant, CancellationToken.None))).Status);
    }
}
