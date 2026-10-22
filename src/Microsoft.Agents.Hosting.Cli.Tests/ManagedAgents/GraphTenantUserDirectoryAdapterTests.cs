using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Identity;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class GraphTenantUserDirectoryAdapterTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Grace = "7541accf-ad35-46a7-b2c1-72148c66c078";
    private const string Guest = "8b0c1f6e-2d4a-4c55-9a1e-3f7d6b2c9e10";
    private const string Ada = "2c4f6a8e-0b1d-4e3f-8a5c-7d9e1f3a5b7c";
    private const string GuestPrincipalName = "grace_fabrikam.com#EXT#@contoso.com";
    private const string Projection = "$select=id,displayName,userPrincipalName";
    private const string SecondPage = "https://graph.microsoft.com/v1.0/users?$skiptoken=page-2";
    private const string ErrorBody = """{"error":{"code":"Denied"}}""";
    private const string WrongAudienceBody = """{"error":{"code":"InvalidAuthenticationToken","message":"Access token validation failure. Invalid audience."}}""";

    private static readonly IdentityRestResponse NotFound = new(404, """{"error":{"code":"Request_ResourceNotFound"}}""");

    private static readonly string[] ObjectIdsSpanningTwoRequests = [.. Enumerable.Range(1, 16).Select(number => $"00000000-0000-0000-0000-{number:D12}")];

    private static (GraphTenantUserDirectoryAdapter Adapter, ScriptedIdentityRestClient Client) Create(Func<IdentityRestCall, IdentityRestResponse> respond)
    {
        var client = new ScriptedIdentityRestClient(respond);

        return (new GraphTenantUserDirectoryAdapter(client), client);
    }

    private static (GraphTenantUserDirectoryAdapter Adapter, ScriptedIdentityRestClient Client) Respond(params IdentityRestResponse[] responses)
    {
        var remaining = new Queue<IdentityRestResponse>(responses);

        return Create(_ => remaining.Dequeue());
    }

    private static (GraphTenantUserDirectoryAdapter Adapter, ScriptedIdentityRestClient Client) RespondAfterPrincipalNameMiss(params IdentityRestResponse[] searchPages) =>
        Respond([NotFound, .. searchPages]);

    private static JsonObject User(string objectId, string userPrincipalName, string? displayName = "Grace Hopper") =>
        new() { ["id"] = objectId, ["displayName"] = displayName, ["userPrincipalName"] = userPrincipalName };

    private static IdentityRestResponse Found(JsonObject user) => new(200, user.ToJsonString());

    private static IdentityRestResponse Page(string? nextLink, params JsonObject[] users)
    {
        var page = new JsonObject { ["value"] = new JsonArray([.. users]) };

        if (nextLink is not null)
        {
            page["@odata.nextLink"] = nextLink;
        }

        return new IdentityRestResponse(200, page.ToJsonString());
    }

    private static string PrincipalNamePath(string encodedAddress) => $"/v1.0/users(%27{encodedAddress}%27)?{Projection}";

    private static string EmailSearchPath(string encodedAddress) => $"/v1.0/users?$filter=mail%20eq%20%27{encodedAddress}%27&{Projection}&$top=2";

    private static string NamePath(params string[] objectIds) =>
        $"/v1.0/users?$filter=id%20in%20%28{string.Join("%2C", objectIds.Select(objectId => $"%27{objectId}%27"))}%29&{Projection}";

    private static async Task<ManagedAgentException> LookupFailure(GraphTenantUserDirectoryAdapter adapter, string address = "grace@fabrikam.com")
    {
        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => adapter.FindUserAsync(Tenant, address, CancellationToken.None));

        Assert.Equal(ExitCodes.Failure, failure.ExitCode);
        Assert.Equal(11053, failure.ErrorCode);
        Assert.Equal("share-principal-lookup-failed", failure.ErrorKind);
        Assert.Contains($"'{address}'", failure.Message, StringComparison.Ordinal);

        return failure;
    }

    [Fact]
    public async Task FindUser_ReturnsTheUserGraphReportsForThePrincipalName()
    {
        var (adapter, client) = Respond(Found(User(Grace.ToUpperInvariant(), "grace@contoso.com")));

        Assert.Equal(new DirectoryPrincipal { ObjectId = Grace, DisplayName = "Grace Hopper", PrincipalType = "User", SignInAddress = "grace@contoso.com" }, await adapter.FindUserAsync(Tenant, "grace@contoso.com", CancellationToken.None));
        var call = Assert.Single(client.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal(PrincipalNamePath("grace%40contoso.com"), call.Path);
        Assert.Equal(["https://graph.microsoft.com/.default"], call.Scopes);
        Assert.Equal(Tenant, call.TenantId);
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task FindUser_ReportsTheSignInAddressGraphRecordsRatherThanTheOneSupplied()
    {
        var (adapter, _) = Respond(Found(User(Grace, "grace@contoso.com")));

        Assert.Equal("grace@contoso.com", (await adapter.FindUserAsync(Tenant, "GRACE@Contoso.com", CancellationToken.None))!.SignInAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task FindUser_NamesAUserWithoutADisplayNameByTheirSignInAddress(string? displayName)
    {
        var (adapter, _) = Respond(Found(User(Grace, "grace@contoso.com", displayName)));

        Assert.Equal("grace@contoso.com", (await adapter.FindUserAsync(Tenant, "grace@contoso.com", CancellationToken.None))!.DisplayName);
    }

    [Fact]
    public async Task FindUser_TrimsTheAddress()
    {
        var (adapter, client) = Respond(Found(User(Grace, "grace@contoso.com")));

        await adapter.FindUserAsync(Tenant, "  grace@contoso.com ", CancellationToken.None);

        Assert.Equal(PrincipalNamePath("grace%40contoso.com"), Assert.Single(client.Calls).Path);
    }

    [Theory]
    [InlineData("o'brien@contoso.com", "o%27%27brien%40contoso.com")]
    [InlineData("grace@contoso.com/manager", "grace%40contoso.com%2Fmanager")]
    [InlineData("x')/manager?a=('", "x%27%27%29%2Fmanager%3Fa%3D%28%27%27")]
    [InlineData("guest_fabrikam.com#EXT#@contoso.com", "guest_fabrikam.com%23EXT%23%40contoso.com")]
    [InlineData("$admin@contoso.com", "%24admin%40contoso.com")]
    public async Task FindUser_KeepsTheWholeAddressInsideOneKeyLiteral(string address, string encodedAddress)
    {
        var (adapter, client) = Respond(Found(User(Grace, "grace@contoso.com")));

        await adapter.FindUserAsync(Tenant, address, CancellationToken.None);

        Assert.Equal(PrincipalNamePath(encodedAddress), Assert.Single(client.Calls).Path);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    public async Task FindUser_FallsBackToTheEmailAddressWhenNoUserHasThatPrincipalName(int principalNameStatus)
    {
        var (adapter, client) = Respond(new IdentityRestResponse(principalNameStatus, ErrorBody), Page(null, User(Guest.ToUpperInvariant(), GuestPrincipalName)));

        Assert.Equal(new DirectoryPrincipal { ObjectId = Guest, DisplayName = "Grace Hopper", PrincipalType = "User", SignInAddress = GuestPrincipalName }, await adapter.FindUserAsync(Tenant, " grace@fabrikam.com ", CancellationToken.None));
        Assert.Collection(
            client.Calls,
            call => Assert.Equal(PrincipalNamePath("grace%40fabrikam.com"), call.Path),
            call =>
            {
                Assert.Equal("GET", call.Method);
                Assert.Equal(EmailSearchPath("grace%40fabrikam.com"), call.Path);
                Assert.Equal(["https://graph.microsoft.com/.default"], call.Scopes);
                Assert.Equal(Tenant, call.TenantId);
                Assert.Null(call.Body);
            });
    }

    [Theory]
    [InlineData("o'brien@contoso.com", "o%27%27brien%40contoso.com")]
    [InlineData("x' or startswith(mail,'a", "x%27%27%20or%20startswith%28mail%2C%27%27a")]
    [InlineData("a&$top=1@contoso.com", "a%26%24top%3D1%40contoso.com")]
    [InlineData("guest_fabrikam.com#EXT#@contoso.com", "guest_fabrikam.com%23EXT%23%40contoso.com")]
    public async Task FindUser_KeepsTheWholeAddressInsideOneFilterLiteral(string address, string encodedAddress)
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(null));

        await adapter.FindUserAsync(Tenant, address, CancellationToken.None);

        Assert.Equal(EmailSearchPath(encodedAddress), client.Calls[^1].Path);
    }

    [Fact]
    public async Task FindUser_ReturnsNullWhenNeitherThePrincipalNameNorTheEmailAddressMatches()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(null));

        Assert.Null(await adapter.FindUserAsync(Tenant, "nobody@contoso.com", CancellationToken.None));
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task FindUser_FollowsThePagingLinkPastAnEmptyPage()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(SecondPage), Page(null, User(Guest, GuestPrincipalName)));

        Assert.Equal(Guest, (await adapter.FindUserAsync(Tenant, "grace@fabrikam.com", CancellationToken.None))!.ObjectId);
        Assert.Equal([PrincipalNamePath("grace%40fabrikam.com"), EmailSearchPath("grace%40fabrikam.com"), SecondPage], client.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task FindUser_RejectsAnEmailAddressWhoseMatchesSpanPages()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(SecondPage, User(Guest, GuestPrincipalName)), Page(null, User(Ada, "ada@contoso.com")));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => adapter.FindUserAsync(Tenant, "grace@fabrikam.com", CancellationToken.None));

        Assert.Equal("share-principal-ambiguous", failure.ErrorKind);
        Assert.Equal(11052, failure.ErrorCode);
        Assert.Contains($"including {GuestPrincipalName}, ada@contoso.com", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task FindUser_StopsSearchingOnceTwoUsersShareTheEmailAddress()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(SecondPage, User(Guest, GuestPrincipalName), User(Grace, "grace@contoso.com")));

        var failure = await Assert.ThrowsAsync<ManagedAgentException>(() => adapter.FindUserAsync(Tenant, "grace@fabrikam.com", CancellationToken.None));

        Assert.Equal("share-principal-ambiguous", failure.ErrorKind);
        Assert.Equal(11052, failure.ErrorCode);
        Assert.Contains("'grace@fabrikam.com'", failure.Message, StringComparison.Ordinal);
        Assert.Contains(Tenant, failure.Message, StringComparison.Ordinal);
        Assert.Contains($"including {GuestPrincipalName}, grace@contoso.com", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task FindUser_CountsAUserListedOnSeveralPagesOnce()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(SecondPage, User(Guest, GuestPrincipalName)), Page(null, User(Guest.ToUpperInvariant(), GuestPrincipalName)));

        Assert.Equal(Guest, (await adapter.FindUserAsync(Tenant, "grace@fabrikam.com", CancellationToken.None))!.ObjectId);
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task FindUser_DoesNotSettleOnAMatchWhenALaterPageFails()
    {
        var (adapter, client) = RespondAfterPrincipalNameMiss(Page(SecondPage, User(Guest, GuestPrincipalName)), new IdentityRestResponse(500, ErrorBody));

        Assert.Equal(500, Assert.IsType<TargetRequestException>((await LookupFailure(adapter)).InnerException).Status);
        Assert.Equal(3, client.Calls.Count);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task FindUser_ReportsAnyOtherPrincipalNameFailureAsALookupFailure(int status)
    {
        var (adapter, client) = Respond(new IdentityRestResponse(status, ErrorBody));

        Assert.Equal(status, Assert.IsType<TargetRequestException>((await LookupFailure(adapter)).InnerException).Status);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task FindUser_ReportsAnEmailSearchFailureAsALookupFailure(int status) =>
        Assert.Equal(status, Assert.IsType<TargetRequestException>((await LookupFailure(RespondAfterPrincipalNameMiss(new IdentityRestResponse(status, ErrorBody)).Adapter)).InnerException).Status);

    [Theory]
    [InlineData(false, ErrorBody, ExitCodes.NotSignedIn, "auth-expired")]
    [InlineData(false, WrongAudienceBody, ExitCodes.Usage, "wrong-audience")]
    [InlineData(true, ErrorBody, ExitCodes.NotSignedIn, "auth-expired")]
    [InlineData(true, WrongAudienceBody, ExitCodes.Usage, "wrong-audience")]
    public async Task FindUser_KeepsTheExitCodeAndKindOfARejectedSignIn(bool duringEmailSearch, string body, int exitCode, string errorKind)
    {
        var rejected = new IdentityRestResponse(401, body);
        var (adapter, client) = duringEmailSearch ? RespondAfterPrincipalNameMiss(rejected) : Respond(rejected);

        var failure = await Assert.ThrowsAsync<TargetRequestException>(() => adapter.FindUserAsync(Tenant, "grace@contoso.com", CancellationToken.None));

        Assert.Equal(exitCode, failure.ExitCode);
        Assert.Equal(errorKind, failure.ErrorKind);
        Assert.Equal(401, failure.Status);
        Assert.Equal(duringEmailSearch ? 2 : 1, client.Calls.Count);
    }

    [Fact]
    public async Task FindUser_ReportsAnUnreachableDirectoryAsALookupFailure()
    {
        var unreachable = new TargetRequestException("The identity service request to 'graph.microsoft.com' could not reach the service.", ExitCodes.Failure, TargetErrorKind.RequestFailed, status: 0);

        var failure = await LookupFailure(Create(_ => throw unreachable).Adapter);

        Assert.Same(unreachable, failure.InnerException);
        Assert.Contains("could not reach the service", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"displayName":"Grace"}""")]
    [InlineData("""{"id":42,"userPrincipalName":"grace@contoso.com"}""")]
    [InlineData("""{"id":"not-a-guid","userPrincipalName":"grace@contoso.com"}""")]
    [InlineData("""{"id":"7541accf-ad35-46a7-b2c1-72148c66c078"}""")]
    [InlineData("""{"id":"7541accf-ad35-46a7-b2c1-72148c66c078","userPrincipalName":" "}""")]
    [InlineData("""{"id":"7541accf-ad35-46a7-b2c1-72148c66c078","userPrincipalName":42}""")]
    public async Task FindUser_RejectsAPrincipalNameResponseDescribingNoUsableUser(string? body) =>
        Assert.Contains("without a usable object id or sign-in address", (await LookupFailure(Respond(new IdentityRestResponse(200, body)).Adapter, "grace@contoso.com")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task FindUser_RejectsAPrincipalNameResponseThatIsNotAnObject(string body)
    {
        var failure = await LookupFailure(Respond(new IdentityRestResponse(200, body)).Adapter, "grace@contoso.com");

        Assert.IsType<TargetRequestException>(failure.InnerException);
        Assert.Contains("malformed JSON", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"userPrincipalName":"grace_fabrikam.com#EXT#@contoso.com"}""")]
    [InlineData("""{"id":"not-a-guid","userPrincipalName":"grace_fabrikam.com#EXT#@contoso.com"}""")]
    [InlineData("""{"id":"8b0c1f6e-2d4a-4c55-9a1e-3f7d6b2c9e10"}""")]
    [InlineData("""{"id":"8b0c1f6e-2d4a-4c55-9a1e-3f7d6b2c9e10","userPrincipalName":" "}""")]
    public async Task FindUser_RejectsAnEmailSearchListingAUserItCannotRead(string unreadable) =>
        Assert.Contains("without a usable object id or sign-in address", (await LookupFailure(RespondAfterPrincipalNameMiss(Page(null, User(Grace, "grace@contoso.com"), JsonNode.Parse(unreadable)!.AsObject())).Adapter)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"value":{}}""")]
    [InlineData("""{"value":[42]}""")]
    public async Task FindUser_RejectsAMalformedEmailSearchPage(string body) =>
        Assert.IsType<TargetRequestException>((await LookupFailure(RespondAfterPrincipalNameMiss(new IdentityRestResponse(200, body)).Adapter)).InnerException);

    [Theory]
    [InlineData(null, "grace@contoso.com")]
    [InlineData(" ", "grace@contoso.com")]
    [InlineData(Tenant, null)]
    [InlineData(Tenant, " ")]
    public async Task FindUser_RequiresATenantAndAnAddress(string? tenantId, string? address)
    {
        var (adapter, client) = Respond();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.FindUserAsync(tenantId!, address!, CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task ResolveMany_NamesEveryUserInOneRequest()
    {
        var (adapter, client) = Respond(Page(null, User(Grace, "grace@contoso.com"), User(Guest, GuestPrincipalName, "Guest Hopper")));

        Assert.Equal(
            [
                new DirectoryPrincipal { ObjectId = Grace, DisplayName = "Grace Hopper", PrincipalType = "User", SignInAddress = "grace@contoso.com" },
                new DirectoryPrincipal { ObjectId = Guest, DisplayName = "Guest Hopper", PrincipalType = "User", SignInAddress = GuestPrincipalName },
            ],
            await adapter.ResolveManyAsync(Tenant, [Grace, Guest], CancellationToken.None).ToListAsync());
        var call = Assert.Single(client.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal(NamePath(Grace, Guest), call.Path);
        Assert.Equal(["https://graph.microsoft.com/.default"], call.Scopes);
        Assert.Equal(Tenant, call.TenantId);
    }

    [Fact]
    public async Task ResolveMany_AsksForAtMostFifteenObjectIdsPerRequest()
    {
        var (adapter, client) = Respond(Page(null), Page(null));

        await adapter.ResolveManyAsync(Tenant, ObjectIdsSpanningTwoRequests, CancellationToken.None).ToListAsync();

        Assert.Equal([NamePath(ObjectIdsSpanningTwoRequests[..15]), NamePath(ObjectIdsSpanningTwoRequests[15])], client.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task ResolveMany_StreamsTheUsersOfAnEarlierRequestBeforeALaterOneFails()
    {
        var (adapter, client) = Respond(Page(null, User(ObjectIdsSpanningTwoRequests[0], "first@contoso.com")), new IdentityRestResponse(503, ErrorBody));
        var streamedObjectIds = new List<string>();

        Assert.Equal(503, (await Assert.ThrowsAsync<TargetRequestException>(async () =>
        {
            await foreach (var user in adapter.ResolveManyAsync(Tenant, ObjectIdsSpanningTwoRequests, CancellationToken.None))
            {
                streamedObjectIds.Add(user.ObjectId);
            }
        })).Status);
        Assert.Equal([ObjectIdsSpanningTwoRequests[0]], streamedObjectIds);
        Assert.Equal([NamePath(ObjectIdsSpanningTwoRequests[..15]), NamePath(ObjectIdsSpanningTwoRequests[15])], client.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task ResolveMany_AsksOnceForEachObjectIdAndSkipsEverythingElse()
    {
        var (adapter, client) = Respond(Page(null, User(Grace, "grace@contoso.com")));

        Assert.Equal(Grace, Assert.Single(await adapter.ResolveManyAsync(Tenant, ["not-a-guid", Grace.ToUpperInvariant(), $"{{{Grace}}}", Grace], CancellationToken.None).ToListAsync()).ObjectId);
        Assert.Equal(NamePath(Grace), Assert.Single(client.Calls).Path);
    }

    public static readonly TheoryData<string[]> WithoutObjectIds = new() { Array.Empty<string>(), new[] { "not-a-guid" } };

    [Theory]
    [MemberData(nameof(WithoutObjectIds))]
    public async Task ResolveMany_MakesNoRequestWithoutAnObjectId(string[] objectIds)
    {
        var (adapter, client) = Respond();

        Assert.Empty(await adapter.ResolveManyAsync(Tenant, objectIds, CancellationToken.None).ToListAsync());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task ResolveMany_FollowsThePagingLink()
    {
        var (adapter, client) = Respond(Page(SecondPage, User(Grace, "grace@contoso.com")), Page(null, User(Guest, GuestPrincipalName)));

        Assert.Equal([Grace, Guest], (await adapter.ResolveManyAsync(Tenant, [Grace, Guest], CancellationToken.None).ToListAsync()).Select(user => user.ObjectId));
        Assert.Equal([NamePath(Grace, Guest), SecondPage], client.Calls.Select(call => call.Path));
    }

    [Fact]
    public async Task ResolveMany_LeavesOutAUserItCannotRead()
    {
        var (adapter, _) = Respond(Page(null, User(Grace, "grace@contoso.com"), JsonNode.Parse($$"""{"id":"{{Guest}}"}""")!.AsObject(), JsonNode.Parse("""{"id":"not-a-guid","userPrincipalName":"x@contoso.com"}""")!.AsObject()));

        Assert.Equal(Grace, Assert.Single(await adapter.ResolveManyAsync(Tenant, [Grace, Guest], CancellationToken.None).ToListAsync()).ObjectId);
    }

    [Fact]
    public async Task ResolveMany_ReportsAFailedRequest() =>
        Assert.Equal(403, (await Assert.ThrowsAsync<TargetRequestException>(() => Respond(new IdentityRestResponse(403, ErrorBody)).Adapter.ResolveManyAsync(Tenant, [Grace], CancellationToken.None).ToListAsync().AsTask())).Status);

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void ResolveMany_RequiresATenantBeforeAnythingIsRead(string? tenantId)
    {
        var (adapter, client) = Respond();

        Assert.ThrowsAny<ArgumentException>(() => adapter.ResolveManyAsync(tenantId!, [Grace], CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public void ResolveMany_RequiresObjectIdsBeforeAnythingIsRead() =>
        Assert.Throws<ArgumentNullException>(() => Respond().Adapter.ResolveManyAsync(Tenant, null!, CancellationToken.None));

    [Fact]
    public void Constructor_RequiresAClient() => Assert.Throws<ArgumentNullException>(() => new GraphTenantUserDirectoryAdapter(null!));
}
