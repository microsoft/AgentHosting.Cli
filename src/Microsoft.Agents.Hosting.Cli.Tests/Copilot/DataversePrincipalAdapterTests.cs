using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataversePrincipalAdapterTests
{
    private const string Organization = "https://org.crm.dynamics.com/";
    private const string AdaObjectId = "11111111-1111-1111-1111-111111111111";
    private const string SupportObjectId = "22222222-2222-2222-2222-222222222222";

    private static CopilotEnvironmentContext Context() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static (DataversePrincipalAdapter Adapter, RecordingHandler Handler) Create(params string[] responses)
    {
        var handler = new RecordingHandler(responses);
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));

        return (new DataversePrincipalAdapter(new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor)), handler);
    }

    private static string Users(params (string ObjectId, string FullName, string DomainName)[] rows) =>
        JsonSerializer.Serialize(new { value = rows.Select(row => new { azureactivedirectoryobjectid = row.ObjectId, fullname = row.FullName, domainname = row.DomainName }) });

    private static string Teams(params (string ObjectId, string Name)[] rows) =>
        JsonSerializer.Serialize(new { value = rows.Select(row => new { azureactivedirectoryobjectid = row.ObjectId, name = row.Name }) });

    [Fact]
    public async Task ResolveUser_ByObjectId_FiltersOnTheDirectoryObjectId()
    {
        var (adapter, handler) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        var principal = await adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None);

        Assert.Equal(AdaObjectId, principal!.ObjectId);
        Assert.Equal("Ada Lovelace", principal.DisplayName);
        Assert.Equal("ada@contoso.com", principal.SignInAddress);
        Assert.Equal("User", principal.PrincipalType);
        Assert.Contains($"azureactivedirectoryobjectid eq {AdaObjectId}", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUser_BySignInAddress_FiltersOnBothAddressColumns()
    {
        var (adapter, handler) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        await adapter.ResolveUserAsync(Context(), "ada@contoso.com", CancellationToken.None);

        Assert.Contains("internalemailaddress eq 'ada@contoso.com'", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("domainname eq 'ada@contoso.com'", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUser_ExcludesDisabledAccounts()
    {
        var (adapter, handler) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        await adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None);

        Assert.Contains("isdisabled eq false", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUser_EscapesAnAddressContainingAQuote()
    {
        var (adapter, handler) = Create(Users());

        await adapter.ResolveUserAsync(Context(), "o'brien@contoso.com", CancellationToken.None);

        Assert.Contains("o''brien@contoso.com", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUser_WithNoMatch_ReturnsNull() =>
        Assert.Null(await Create(Users()).Adapter.ResolveUserAsync(Context(), "nobody@contoso.com", CancellationToken.None));

    [Fact]
    public async Task ResolveUser_SkipsARowCarryingNoDirectoryObjectId()
    {
        var (adapter, _) = Create(JsonSerializer.Serialize(new { value = new[] { new { fullname = "Ada Lovelace", domainname = "ada@contoso.com" } } }));

        Assert.Null(await adapter.ResolveUserAsync(Context(), "ada@contoso.com", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveUser_FallsBackToTheObjectIdWhenTheRowHasNoName()
    {
        var (adapter, _) = Create(JsonSerializer.Serialize(new { value = new[] { new { azureactivedirectoryobjectid = AdaObjectId } } }));

        var principal = await adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None);

        Assert.Equal(AdaObjectId, principal!.DisplayName);
    }

    [Fact]
    public async Task ResolveMany_NamesUsersWithoutQueryingTeams()
    {
        var (adapter, handler) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        var resolved = await adapter.ResolveManyAsync(Context(), [AdaObjectId], CancellationToken.None);

        Assert.Equal("Ada Lovelace", resolved[AdaObjectId].DisplayName);
        Assert.Single(handler.Urls);
        Assert.DoesNotContain(handler.Urls, url => url.Contains("/teams", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveMany_QueriesTeamsOnlyForWhatTheUserLookupMissed()
    {
        var (adapter, handler) = Create(
            Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")),
            Teams((SupportObjectId, "Support")));

        var resolved = await adapter.ResolveManyAsync(Context(), [AdaObjectId, SupportObjectId], CancellationToken.None);

        Assert.Equal("Group", resolved[SupportObjectId].PrincipalType);
        Assert.Equal("Support", resolved[SupportObjectId].DisplayName);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains(SupportObjectId, handler.Urls[1], StringComparison.Ordinal);
        Assert.DoesNotContain(AdaObjectId, handler.Urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveMany_IgnoresIdentifiersThatAreNotDirectoryObjects()
    {
        var (adapter, handler) = Create(Users());

        var resolved = await adapter.ResolveManyAsync(Context(), ["not-a-guid"], CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task ResolveMany_WithNothingToNameMakesNoRequest()
    {
        var (adapter, handler) = Create(Users());

        Assert.Empty(await adapter.ResolveManyAsync(Context(), [], CancellationToken.None));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task ResolveMany_DeduplicatesRepeatedIdentifiers()
    {
        var (adapter, handler) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        await adapter.ResolveManyAsync(Context(), [AdaObjectId, AdaObjectId.ToUpperInvariant()], CancellationToken.None);

        Assert.Single(handler.Urls);
        Assert.Single([.. handler.Urls[0].Split("azureactivedirectoryobjectid eq ", StringSplitOptions.RemoveEmptyEntries).Skip(1)]);
    }

    [Fact]
    public async Task ResolveUser_EncodesAGuestAddressRatherThanLettingItTruncateTheFilter()
    {
        var (adapter, handler) = Create(Users());

        await adapter.ResolveUserAsync(Context(), "ada_contoso.com#EXT#@fabrikam.onmicrosoft.com", CancellationToken.None);

        Assert.DoesNotContain("#", handler.Urls[^1], StringComparison.Ordinal);
        Assert.Contains("isdisabled eq false", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("ada_contoso.com#EXT#@fabrikam.onmicrosoft.com", handler.LastUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveUser_RefusesToChooseBetweenSeveralMatches()
    {
        var (adapter, _) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com"), (SupportObjectId, "Ada Byron", "ada@fabrikam.com")));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), "ada@contoso.com", CancellationToken.None));

        Assert.Equal("share-principal-ambiguous", failure.ErrorKind);
    }

    [Fact]
    public async Task ResolveUser_AcceptsTheSameAccountMatchedByBothAddressColumns()
    {
        var (adapter, _) = Create(Users((AdaObjectId, "Ada Lovelace", "ada@contoso.com"), (AdaObjectId, "Ada Lovelace", "ada@contoso.com")));

        Assert.Equal(AdaObjectId, (await adapter.ResolveUserAsync(Context(), "ada@contoso.com", CancellationToken.None))!.ObjectId);
    }

    [Fact]
    public async Task ResolveMany_FollowsThePagingLinkTheServiceReturns()
    {
        var firstPage = JsonSerializer.Serialize(new
        {
            value = new[] { new { azureactivedirectoryobjectid = AdaObjectId, fullname = "Ada Lovelace", domainname = "ada@contoso.com" } },
            odatanextLink = $"{Organization}api/data/v9.2/systemusers?$skiptoken=page-2",
        }).Replace("odatanextLink", "@odata.nextLink", StringComparison.Ordinal);

        var (adapter, handler) = Create(firstPage, Users((SupportObjectId, "Grace Hopper", "grace@contoso.com")));

        var resolved = await adapter.ResolveManyAsync(Context(), [AdaObjectId, SupportObjectId], CancellationToken.None);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("skiptoken=page-2", handler.Urls[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task APagingLinkThatIsNotAUrlIsAFailedReadRatherThanTheEndOfTheCollection(string link)
    {
        var (adapter, handler) = Create($$"""{"value":[{"azureactivedirectoryobjectid":"{{AdaObjectId}}","fullname":"Ada Lovelace"}],"@odata.nextLink":{{link}}}""");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), "ada@contoso.com", CancellationToken.None));

        Assert.Contains("pagination link", failure.Message, StringComparison.Ordinal);
        Assert.Single(handler.Urls);
    }

    [Fact]
    public async Task APagingLinkAlreadyFollowedEndsTheReadRatherThanCyclingForever()
    {
        var page = JsonSerializer.Serialize(new
        {
            value = Array.Empty<object>(),
            odatanextLink = $"{Organization}api/data/v9.2/systemusers?$skiptoken=page-1",
        }).Replace("odatanextLink", "@odata.nextLink", StringComparison.Ordinal);

        var (adapter, handler) = Create(page, page, page);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None));

        Assert.Contains("already been followed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task APagingLinkLeavingTheOrganizationIsRefused()
    {
        var page = JsonSerializer.Serialize(new
        {
            value = Array.Empty<object>(),
            odatanextLink = "https://attacker.example/api/data/v9.2/systemusers",
        }).Replace("odatanextLink", "@odata.nextLink", StringComparison.Ordinal);

        var (adapter, _) = Create(page);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None));

        Assert.Equal("untrusted-dataverse-origin", failure.ErrorKind);
    }

    [Fact]
    public async Task ACollectionPageThatIsNotAnObjectIsAFailedRead()
    {
        var (adapter, _) = Create("[]");

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None));

        Assert.Contains("'value' array", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":"unavailable"}""")]
    public async Task AnUnrecognisedFailureEnvelopeStillReportsTheHttpStatus(string body)
    {
        var handler = new RecordingHandler([body]) { Status = HttpStatusCode.Forbidden };
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));
        var adapter = new DataversePrincipalAdapter(new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None));

        Assert.Contains("HTTP 403", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedResponseSurfacesWithoutEchoingTheBody()
    {
        var handler = new RecordingHandler([@"{""error"":{""code"":""0x80040220"",""message"":""secret detail""}}"]) { Status = HttpStatusCode.Forbidden };
        var accessor = new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider(), () => handler);
        accessor.SetDataverseOrganization(new Uri(Organization));
        var adapter = new DataversePrincipalAdapter(new DataverseWebApiClient(new StubSolutionVersionsClient(), accessor));

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => adapter.ResolveUserAsync(Context(), AdaObjectId, CancellationToken.None));

        Assert.Contains("0x80040220", failure.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public RecordingHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public List<string> Urls { get; } = [];

        public string LastUrl => Uri.UnescapeDataString(Urls[^1]);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());

            var body = _responses.Count > 0 ? _responses.Dequeue() : """{"value":[]}""";

            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

}
