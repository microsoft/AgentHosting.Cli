using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsDataverseTests : IDisposable
{
    private const string BotId = "66666666-0000-4000-8000-000000000006";
    private const string FirstComponent = "77777777-0000-4000-8000-000000000007";
    private const string SecondComponent = "88888888-0000-4000-8000-000000000008";
    private const string ActiveFlow = "99999999-0000-4000-8000-000000000009";
    private const string DraftFlow = "aaaaaaaa-1111-4000-8000-00000000000a";
    private const string OtherFlow = "bbbbbbbb-1111-4000-8000-00000000000b";
    private const string ConnectorId = "cccccccc-1111-4000-8000-00000000000c";
    private const string ReferenceId = "dddddddd-1111-4000-8000-00000000000d";
    private const string Rows = "https://dev.example.invalid/api/data/v9.2/";

    private readonly ScratchDirectory _scratch = new();
    private readonly OracleStub _oracle = new();
    private readonly FakeTokens _tokens = new();
    private readonly DataverseProbe _dataverse;

    public AmsMcsDataverseTests() => _dataverse = new DataverseProbe(_tokens, new Transcript(_scratch.Folder("run")), AmsMcsTestKit.Config().OrganizationsByEnvironment(), new HttpMessageInvoker(_oracle)) { RetryDelay = TimeSpan.Zero };

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task BotsAreFoundBySchemaNameInTheOrganizationFromConfig()
    {
        _oracle.Answer(HttpMethod.Get, "/bots?", HttpStatusCode.OK, $$"""{"value":[{"botid":"{{BotId}}","name":"Contoso","schemaname":"ahe2e_it's","template":"default-2.1.0"},{"name":"no id"}]}""");

        var bots = await _dataverse.BotsNamedAsync(AmsMcsTestKit.Dev, "ahe2e_it's", CancellationToken.None);

        Assert.Equal([new DataverseBot { BotId = BotId, Name = "Contoso", SchemaName = "ahe2e_it's", Template = "default-2.1.0" }, new DataverseBot { BotId = string.Empty, Name = "no id" }], bots);
        Assert.Equal($"GET {Rows}bots?$select=botid,name,schemaname,template&$filter=schemaname eq 'ahe2e_it''s'", Assert.Single(_oracle.Calls).Target);
        Assert.Equal(["https://dev.example.invalid"], _tokens.Resources);
    }

    [Fact]
    public async Task AQueryWithoutRowsFindsNone()
    {
        _oracle.Answer(HttpMethod.Get, "/bots?", HttpStatusCode.OK, "{}");

        Assert.Empty(await _dataverse.BotsNamedAsync(AmsMcsTestKit.Test, "ahe2e_none", CancellationToken.None));
        Assert.Equal("test.example.invalid", Assert.Single(_oracle.Calls).Uri.Host);
    }

    [Fact]
    public async Task ARefusedQueryStops()
    {
        _oracle.Answer(HttpMethod.Get, "/bots?", HttpStatusCode.Forbidden, "denied");

        Assert.StartsWith("Dataverse refused the bot query: 403", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.BotsNamedAsync(AmsMcsTestKit.Dev, "ahe2e_x", CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingBotReadsAsNoneAndARefusedReadStops()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.NotFound).Answer(HttpMethod.Get, "/bots(", HttpStatusCode.InternalServerError);

        Assert.Null(await _dataverse.BotAsync(AmsMcsTestKit.Dev, BotId.ToUpperInvariant(), CancellationToken.None));
        Assert.StartsWith("Dataverse refused the bot read: 500", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.BotAsync(AmsMcsTestKit.Dev, BotId, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Equal($"GET {Rows}bots({BotId})?$select=botid,name,schemaname,template", _oracle.Calls[0].Target);
    }

    [Fact]
    public async Task ARenamePatchesTheNameWithIfMatchAndReturnsTheBotAsReadAgain()
    {
        _oracle.Answer(HttpMethod.Patch, $"/bots({BotId})", HttpStatusCode.NoContent).Answer(HttpMethod.Get, $"/bots({BotId})", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","name":"Renamed","schemaname":"ahe2e_mine"}""");

        var renamed = await _dataverse.RenameBotAsync(AmsMcsTestKit.Dev, BotId, "Renamed", CancellationToken.None);

        var patch = Assert.Single(_oracle.CallsTo(HttpMethod.Patch, "/bots("));
        Assert.Equal("Renamed", renamed!.Name);
        Assert.Equal("""{"name":"Renamed"}""", patch.Body);
        Assert.Equal("*", patch.IfMatch);
        Assert.Equal("Bearer fake-token", patch.Authorization!.ToString());
    }

    [Fact]
    public async Task ARefusedRenameStops()
    {
        _oracle.Answer(HttpMethod.Patch, "/bots(", HttpStatusCode.PreconditionFailed);

        Assert.StartsWith("Dataverse refused the bot rename: 412", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.RenameBotAsync(AmsMcsTestKit.Dev, BotId, "Renamed", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.CallsTo(HttpMethod.Get, "/bots("));
    }

    [Fact]
    public async Task AComponentEditPatchesTheDescriptionOfTheBotsOnlyComponentOfThatNameAndReturnsItAsReadAgain()
    {
        _oracle.Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, $$"""{"value":[{"botcomponentid":"{{FirstComponent}}","name":"Thank you","description":"before"}]}""")
            .Answer(HttpMethod.Patch, $"/botcomponents({FirstComponent})", HttpStatusCode.NoContent)
            .Answer(HttpMethod.Get, $"/botcomponents({FirstComponent})", HttpStatusCode.OK, $$"""{"botcomponentid":"{{FirstComponent}}","name":"Thank you","description":"after"}""");

        var edited = await _dataverse.EditComponentDescriptionAsync(AmsMcsTestKit.Dev, BotId.ToUpperInvariant(), "Thank you", "after", CancellationToken.None);

        Assert.Equal(new DataverseBotComponent { ComponentId = FirstComponent, Name = "Thank you", Description = "after" }, edited);
        Assert.Equal([$"GET {Rows}botcomponents?$select=botcomponentid,name,description&$filter=_parentbotid_value eq {BotId} and name eq 'Thank you'", $"PATCH {Rows}botcomponents({FirstComponent})", $"GET {Rows}botcomponents({FirstComponent})?$select=botcomponentid,name,description"], _oracle.Calls.Select(call => call.Target));
        Assert.Equal("""{"description":"after"}""", _oracle.Calls[1].Body);
    }

    [Theory]
    [InlineData("""{"value":[]}""", 0)]
    [InlineData("""{"value":[{"botcomponentid":"77777777-0000-4000-8000-000000000007"},{"name":"Greeting"}]}""", 2)]
    public async Task AComponentEditIsRefusedUnlessTheBotHoldsExactlyOneComponentOfThatName(string rows, int count)
    {
        _oracle.Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, rows);

        var refused = await Assert.ThrowsAsync<E2EException>(() => _dataverse.EditComponentDescriptionAsync(AmsMcsTestKit.Dev, BotId, "Greeting", "after", CancellationToken.None));

        Assert.Equal($"Bot {BotId} holds {count} components named Greeting, not one.", refused.Message);
        Assert.Single(_oracle.Calls);
    }

    [Fact]
    public async Task ABotWhoseSchemaNameDiffersIsNeverDeleted()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"cr123_realAgent"}""");

        var refused = await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None));

        Assert.Equal($"Refused to delete bot {BotId}: its schema name is cr123_realAgent, not ahe2e_mine.", refused.Message);
        Assert.DoesNotContain(_oracle.Calls, call => call.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task AnAbsentBotIsNotDeleted()
    {
        Assert.False(await _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None));
        Assert.Single(_oracle.Calls);
    }

    [Fact]
    public async Task ABotIsDeletedAfterItsComponentsWithARetryWhileItIsBusy()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"AHE2E_Mine"}""")
            .Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, $$"""{"value":[{"botcomponentid":"{{FirstComponent}}"},{"botcomponentid":"{{SecondComponent}}"}]}""")
            .Answer(HttpMethod.Delete, $"/botcomponents({FirstComponent})", HttpStatusCode.NoContent)
            .Answer(HttpMethod.Delete, $"/botcomponents({SecondComponent})", HttpStatusCode.NotFound)
            .Answer(HttpMethod.Delete, "/bots(", HttpStatusCode.Conflict).Answer(HttpMethod.Delete, "/bots(", HttpStatusCode.NoContent);

        Assert.True(await _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None));

        Assert.Equal([$"DELETE {Rows}botcomponents({FirstComponent})", $"DELETE {Rows}botcomponents({SecondComponent})", $"DELETE {Rows}bots({BotId})", $"DELETE {Rows}bots({BotId})"], _oracle.Calls.Where(call => call.Method == HttpMethod.Delete).Select(call => call.Target));
        Assert.Equal($"GET {Rows}botcomponents?$select=botcomponentid&$filter=_parentbotid_value eq {BotId}", _oracle.CallsTo(HttpMethod.Get, "/botcomponents?")[0].Target);
        Assert.All(_oracle.Calls.Where(call => call.Method == HttpMethod.Delete), call => Assert.Equal("*", call.IfMatch));
    }

    [Fact]
    public async Task ABotThatStaysBusyStopsAfterFiveAttempts()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"ahe2e_mine"}""").Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, """{"value":[]}""").Answer(HttpMethod.Delete, "/bots(", HttpStatusCode.Conflict);

        Assert.StartsWith("Dataverse refused the bot delete: 409", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Equal(5, _oracle.CallsTo(HttpMethod.Delete, "/bots(").Count);
    }

    [Fact]
    public async Task ARefusedComponentDeleteStopsBeforeTheBot()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"ahe2e_mine"}""").Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, $$"""{"value":[{"botcomponentid":"{{FirstComponent}}"}]}""").Answer(HttpMethod.Delete, "/botcomponents(", HttpStatusCode.Forbidden);

        Assert.StartsWith("Dataverse refused the bot component delete: 403", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.CallsTo(HttpMethod.Delete, "/bots("));
    }

    [Fact]
    public async Task AnIdThatIsNotAGuidIsRefusedBeforeAnyCall()
    {
        Assert.Equal("'bots' is not a bot id.", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, "bots", "ahe2e_mine", CancellationToken.None))).Message);
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task AComponentRowWithoutAnIdIsNeverDeleted()
    {
        _oracle.Answer(HttpMethod.Get, "/bots(", HttpStatusCode.OK, $$"""{"botid":"{{BotId}}","schemaname":"ahe2e_mine"}""").Answer(HttpMethod.Get, "/botcomponents?", HttpStatusCode.OK, """{"value":[{"name":"unnamed"}]}""");

        Assert.Equal("'' is not a bot component id.", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteBotAsync(AmsMcsTestKit.Dev, BotId, "ahe2e_mine", CancellationToken.None))).Message);
        Assert.DoesNotContain(_oracle.Calls, call => call.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task AFlowIsMatchedOnlyByItsExactName()
    {
        _oracle.Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flows());

        Assert.Equal([ActiveFlow, DraftFlow], (await _dataverse.FlowsNamedExactlyAsync(AmsMcsTestKit.Test, "Flow A", CancellationToken.None)).Select(flow => flow.WorkflowId));
    }

    [Fact]
    public async Task DeletingFlowsTurnsAnActiveOneOffAndDeletesOnlyTheExactName()
    {
        _oracle.Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flows()).Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flows(includeExact: false)).Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.NoContent).Answer(HttpMethod.Delete, $"/workflows({ActiveFlow})", HttpStatusCode.NoContent);

        var left = await _dataverse.DeleteFlowsNamedAsync(AmsMcsTestKit.Test, "Flow A", CancellationToken.None);

        var test = "https://test.example.invalid/api/data/v9.2/";
        Assert.Empty(left);
        Assert.Equal([$"PATCH {test}workflows({ActiveFlow})", $"DELETE {test}workflows({ActiveFlow})", $"DELETE {test}workflows({DraftFlow})"], _oracle.Calls.Where(call => call.Method != HttpMethod.Get).Select(call => call.Target));
        Assert.Equal("""{"statecode":0,"statuscode":1}""", _oracle.CallsTo(HttpMethod.Patch, "/workflows(")[0].Body);
    }

    [Fact]
    public async Task AFlowStillThereAfterTheDeleteIsReturned()
    {
        _oracle.Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flows()).Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.NoContent).Answer(HttpMethod.Delete, "/workflows(", HttpStatusCode.NoContent);

        Assert.Equal([ActiveFlow, DraftFlow], (await _dataverse.DeleteFlowsNamedAsync(AmsMcsTestKit.Test, "Flow A", CancellationToken.None)).Select(flow => flow.WorkflowId));
    }

    [Fact]
    public async Task ARefusedTurnOffStops()
    {
        _oracle.Answer(HttpMethod.Get, "/workflows?", HttpStatusCode.OK, Flows()).Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.BadRequest);

        Assert.StartsWith("Dataverse refused turning the flow off: 400", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteFlowsNamedAsync(AmsMcsTestKit.Test, "Flow A", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.CallsTo(HttpMethod.Delete, "/workflows("));
    }

    [Theory]
    [InlineData("GET", "https://dev.example.invalid/api/data/v9.2/bots(66666666-0000-4000-8000-000000000006)")]
    [InlineData("POST", "https://dev.example.invalid/api/data/v9.2/connectors(66666666-0000-4000-8000-000000000006)")]
    [InlineData("PATCH", "https://dev.example.invalid/api/data/v9.2/accounts(66666666-0000-4000-8000-000000000006)")]
    [InlineData("DELETE", "https://dev.example.invalid/api/data/v9.2/connectionreferences")]
    [InlineData("DELETE", "https://dev.example.invalid/api/data/v9.2/bots(66666666-0000-4000-8000-000000000006)/extra")]
    [InlineData("DELETE", "https://dev.example.invalid/api/data/v9.1/workflows(66666666-0000-4000-8000-000000000006)")]
    public async Task TheOracleRefusesAWriteThatIsNotOneOfItsRowsById(string method, string uri)
    {
        var refused = await Assert.ThrowsAsync<E2EException>(() => OracleHttp.WriteAsync(new HttpMethod(method), new Uri(uri), null, "token", null, CancellationToken.None, new HttpMessageInvoker(_oracle)));

        Assert.EndsWith("the oracle writes only a bot, a bot component, a flow, a connector or a connection reference, each by its id.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.Calls);
    }

    [Theory]
    [InlineData("PATCH", "connectors")]
    [InlineData("DELETE", "connectors")]
    [InlineData("PATCH", "connectionreferences")]
    [InlineData("DELETE", "connectionreferences")]
    public async Task TheOracleWritesAConnectorOrAConnectionReferenceById(string method, string table)
    {
        _oracle.Answer(new HttpMethod(method), $"/{table}(", HttpStatusCode.NoContent);

        var response = await OracleHttp.WriteAsync(new HttpMethod(method), new Uri($"{Rows}{table}({ConnectorId})"), "{}", "token", null, CancellationToken.None, new HttpMessageInvoker(_oracle));

        Assert.True(response.Ok);
        Assert.Equal($"{method} {Rows}{table}({ConnectorId})", Assert.Single(_oracle.Calls).Target);
    }

    [Fact]
    public async Task AFlowRenamePatchesItsNameAndReturnsTheFlowAsReadAgain()
    {
        _oracle.Answer(HttpMethod.Patch, $"/workflows({ActiveFlow})", HttpStatusCode.NoContent).Answer(HttpMethod.Get, $"/workflows({ActiveFlow})", HttpStatusCode.OK, $$"""{"workflowid":"{{ActiveFlow}}","name":"Renamed","statecode":1}""");

        var renamed = await _dataverse.RenameFlowAsync(AmsMcsTestKit.Dev, ActiveFlow.ToUpperInvariant(), "Renamed", CancellationToken.None);

        var patch = Assert.Single(_oracle.CallsTo(HttpMethod.Patch, "/workflows("));
        Assert.Equal("Renamed", renamed!.Name);
        Assert.Equal($"PATCH {Rows}workflows({ActiveFlow})", patch.Target);
        Assert.Equal("""{"name":"Renamed"}""", patch.Body);
        Assert.Equal("*", patch.IfMatch);
    }

    [Fact]
    public async Task ARefusedFlowRenameStopsBeforeTheRead()
    {
        _oracle.Answer(HttpMethod.Patch, "/workflows(", HttpStatusCode.Forbidden);

        Assert.StartsWith("Dataverse refused the flow rename: 403", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.RenameFlowAsync(AmsMcsTestKit.Dev, ActiveFlow, "Renamed", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.CallsTo(HttpMethod.Get, "/workflows("));
    }

    [Fact]
    public async Task ThePublisherPrefixIsTheFirstWritableOneThatIsNeitherEmptyNorTheDefault()
    {
        _oracle.Answer(HttpMethod.Get, "/publishers?", HttpStatusCode.OK, """{"value":[{"customizationprefix":"new"},{"customizationprefix":""},{"customizationprefix":null},{"customizationprefix":"cr150"},{"customizationprefix":"cr999"}]}""");

        Assert.Equal("cr150", await _dataverse.PublisherPrefixAsync(AmsMcsTestKit.Dev, CancellationToken.None));
        Assert.Equal($"GET {Rows}publishers?$select=customizationprefix&$filter=isreadonly eq false", Assert.Single(_oracle.Calls).Target);
    }

    [Fact]
    public async Task AnEnvironmentWithOnlyTheDefaultPublisherHasNoPrefix()
    {
        _oracle.Answer(HttpMethod.Get, "/publishers?", HttpStatusCode.OK, """{"value":[{"customizationprefix":"NEW"}]}""");

        Assert.Equal($"Environment {AmsMcsTestKit.Dev} has no writable publisher whose prefix isn't new, so a custom connector can't be named.", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.PublisherPrefixAsync(AmsMcsTestKit.Dev, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task AConnectorIsReadByIdOrFoundByNameAndAMissingOneReadsAsNone()
    {
        _oracle.Answer(HttpMethod.Get, $"/connectors({ConnectorId})", HttpStatusCode.OK, Connector("before")).Answer(HttpMethod.Get, $"/connectors({ConnectorId})", HttpStatusCode.NotFound).Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, $$"""{"value":[{{Connector("before")}}]}""");

        var read = await _dataverse.ConnectorAsync(AmsMcsTestKit.Dev, ConnectorId, CancellationToken.None);
        var gone = await _dataverse.ConnectorAsync(AmsMcsTestKit.Dev, ConnectorId, CancellationToken.None);
        var named = await _dataverse.ConnectorsNamedAsync(AmsMcsTestKit.Dev, "cr150_5Fit's", CancellationToken.None);

        Assert.Equal(new DataverseConnector { ConnectorId = ConnectorId, Name = "cr150_5Fx", DisplayName = "Connector x", Description = "before", InternalId = "shared_cr150-5fgenerated" }, read);
        Assert.Null(gone);
        Assert.Equal([read!], named);
        Assert.Equal($"GET {Rows}connectors({ConnectorId})?$select=connectorid,name,displayname,description,connectorinternalid", _oracle.Calls[0].Target);
        Assert.Equal($"GET {Rows}connectors?$select=connectorid,name,displayname,description,connectorinternalid&$filter=name eq 'cr150_5Fit''s'", _oracle.Calls[2].Target);
    }

    [Fact]
    public async Task AConnectorsDescriptionIsEditedWithItsWholeDefinitionAsRead()
    {
        _oracle.Answer(HttpMethod.Get, $"/connectors({ConnectorId})?$select=name,", HttpStatusCode.OK, """{"@odata.etag":"W/\"1\"","name":"cr150_5Fx","displayname":"Connector x","description":"before","openapidefinition":"{\"swagger\":\"2.0\"}","connectionparameters":"{}","connectionparametersets":null,"policytemplateinstances":"[]","iconbrandcolor":"#007ee5","iconblob":"iVBORw0KGgo"}""")
            .Answer(HttpMethod.Patch, $"/connectors({ConnectorId})", HttpStatusCode.NoContent)
            .Answer(HttpMethod.Get, $"/connectors({ConnectorId})?$select=connectorid,", HttpStatusCode.OK, Connector("after"));

        var edited = await _dataverse.EditConnectorDescriptionAsync(AmsMcsTestKit.Dev, ConnectorId, "after", CancellationToken.None);

        var patch = Assert.Single(_oracle.CallsTo(HttpMethod.Patch, "/connectors("));
        Assert.Equal("after", edited!.Description);
        Assert.Equal($"GET {Rows}connectors({ConnectorId})?$select=name,displayname,description,openapidefinition,connectionparameters,connectionparametersets,policytemplateinstances,iconbrandcolor,iconblob", _oracle.Calls[0].Target);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"name":"cr150_5Fx","displayname":"Connector x","description":"after","openapidefinition":"{\"swagger\":\"2.0\"}","connectionparameters":"{}","policytemplateinstances":"[]","iconbrandcolor":"#007ee5","iconblob":"iVBORw0KGgo"}"""), JsonNode.Parse(patch.Body!)), patch.Body);
        Assert.Equal("*", patch.IfMatch);
    }

    [Fact]
    public async Task ARefusedConnectorReadStopsTheEditBeforeAnyWrite()
    {
        _oracle.Answer(HttpMethod.Get, "/connectors(", HttpStatusCode.Forbidden);

        Assert.StartsWith("Dataverse refused the connector read: 403", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.EditConnectorDescriptionAsync(AmsMcsTestKit.Dev, ConnectorId, "after", CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_oracle.Calls, call => call.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task AConnectionReferenceIsPointedAtTheConnectorsGeneratedInternalId()
    {
        _oracle.Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, Reference("shared_cr150-5fproject")).Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, Reference("shared_cr150-5fgenerated")).Answer(HttpMethod.Patch, $"/connectionreferences({ReferenceId})", HttpStatusCode.NoContent);

        var pointed = await _dataverse.PointConnectionReferenceAsync(AmsMcsTestKit.Dev, "ahe2e_bot.shared_cr150-5fproject.0123", "shared_cr150-5fgenerated", CancellationToken.None);

        var patch = Assert.Single(_oracle.CallsTo(HttpMethod.Patch, "/connectionreferences("));
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_cr150-5fgenerated", pointed!.ConnectorId);
        Assert.Equal($"PATCH {Rows}connectionreferences({ReferenceId})", patch.Target);
        Assert.Equal("""{"connectorid":"/providers/Microsoft.PowerApps/apis/shared_cr150-5fgenerated"}""", patch.Body);
        Assert.Contains("$filter=connectionreferencelogicalname eq 'ahe2e_bot.shared_cr150-5fproject.0123'", _oracle.Calls[0].Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingConnectionReferenceIsNeverWritten()
    {
        _oracle.Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, """{"value":[]}""");

        Assert.Equal($"Environment {AmsMcsTestKit.Dev} holds no connection reference named cr_missing.", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.PointConnectionReferenceAsync(AmsMcsTestKit.Dev, "cr_missing", "shared_x", CancellationToken.None))).Message);
        Assert.DoesNotContain(_oracle.Calls, call => call.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ConnectorsAreDeletedByTheirExactNameAndThoseLeftAreReturned()
    {
        _oracle.Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, $$"""{"value":[{{Connector("before")}}]}""").Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, """{"value":[]}""").Answer(HttpMethod.Delete, $"/connectors({ConnectorId})", HttpStatusCode.NotFound);

        Assert.Empty(await _dataverse.DeleteConnectorsNamedAsync(AmsMcsTestKit.Dev, "cr150_5Fx", CancellationToken.None));
        Assert.Equal([$"DELETE {Rows}connectors({ConnectorId})"], _oracle.Calls.Where(call => call.Method == HttpMethod.Delete).Select(call => call.Target));
        Assert.All(_oracle.CallsTo(HttpMethod.Get, "/connectors?"), call => Assert.EndsWith("$filter=name eq 'cr150_5Fx'", call.Target, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedConnectorDeleteStops()
    {
        _oracle.Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, $$"""{"value":[{{Connector("before")}}]}""").Answer(HttpMethod.Delete, "/connectors(", HttpStatusCode.Conflict);

        Assert.StartsWith("Dataverse refused the connector delete: 409", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteConnectorsNamedAsync(AmsMcsTestKit.Dev, "cr150_5Fx", CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectorRowWithoutAnIdIsNeverDeleted()
    {
        _oracle.Answer(HttpMethod.Get, "/connectors?", HttpStatusCode.OK, """{"value":[{"name":"cr150_5Fx"}]}""");

        Assert.Equal("'' is not a connector id.", (await Assert.ThrowsAsync<E2EException>(() => _dataverse.DeleteConnectorsNamedAsync(AmsMcsTestKit.Dev, "cr150_5Fx", CancellationToken.None))).Message);
        Assert.DoesNotContain(_oracle.Calls, call => call.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ConnectionReferencesAreDeletedOnlyByTheirExactLogicalName()
    {
        var rows = $$"""{"value":[{"connectionreferenceid":"{{ReferenceId}}","connectionreferencelogicalname":"AHE2E_bot.ref"},{"connectionreferenceid":"{{ConnectorId}}","connectionreferencelogicalname":"ahe2e_bot.ref.longer"}]}""";
        _oracle.Answer(HttpMethod.Get, "/connectionreferences?", HttpStatusCode.OK, rows).Answer(HttpMethod.Delete, "/connectionreferences(", HttpStatusCode.NoContent);

        var left = await _dataverse.DeleteConnectionReferencesNamedAsync(AmsMcsTestKit.Dev, "ahe2e_bot.ref", CancellationToken.None);

        Assert.Equal([ReferenceId], left.Select(reference => reference.Id));
        Assert.Equal([$"DELETE {Rows}connectionreferences({ReferenceId})"], _oracle.Calls.Where(call => call.Method == HttpMethod.Delete).Select(call => call.Target));
        Assert.All(_oracle.CallsTo(HttpMethod.Get, "/connectionreferences?"), call => Assert.Contains("$filter=startswith(connectionreferencelogicalname,'ahe2e_bot.ref')", call.Target, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/r")]
    [InlineData("https://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/r/recover")]
    [InlineData("https://git.example.invalid/appframework/tenants/t/environments/e/git")]
    [InlineData("https://dev.example.invalid/api/data/v9.2/bots(66666666-0000-4000-8000-000000000006)")]
    public async Task TheOracleRefusesADeleteThatIsNotAGrsRepository(string uri)
    {
        var refused = await Assert.ThrowsAsync<E2EException>(() => OracleHttp.DeleteRepositoryAsync(new Uri(uri), "token", null, CancellationToken.None, new HttpMessageInvoker(_oracle)));

        Assert.EndsWith("the oracle deletes only a GRS repository, by its git URL.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task ARepositoryDeleteSendsTheTokenAsABearer()
    {
        const string gitUrl = "https://git.example.invalid/appframework/tenants/t/environments/e/git/repositories/r";
        _oracle.Answer(HttpMethod.Delete, "/git/repositories/r", HttpStatusCode.NoContent);

        var response = await OracleHttp.DeleteRepositoryAsync(new Uri(gitUrl), "grs-token", null, CancellationToken.None, new HttpMessageInvoker(_oracle));

        var call = Assert.Single(_oracle.Calls);
        Assert.True(response.Ok);
        Assert.Equal($"DELETE {gitUrl}", call.Target);
        Assert.Equal("Bearer grs-token", call.Authorization!.ToString());
    }

    private static string Flows(bool includeExact = true) => $$"""{"value":[{{(includeExact ? $$"""{"workflowid":"{{ActiveFlow}}","name":"Flow A","statecode":1,"statuscode":2},{"workflowid":"{{DraftFlow}}","name":"Flow A","statecode":0,"statuscode":1},""" : string.Empty)}}{"workflowid":"{{OtherFlow}}","name":"flow a","statecode":1,"statuscode":2}]}""";

    private static string Connector(string description) => $$"""{"connectorid":"{{ConnectorId}}","name":"cr150_5Fx","displayname":"Connector x","description":"{{description}}","connectorinternalid":"shared_cr150-5fgenerated"}""";

    private static string Reference(string internalId) => $$"""{"value":[{"connectionreferenceid":"{{ReferenceId}}","connectionreferencelogicalname":"ahe2e_bot.shared_cr150-5fproject.0123","connectorid":"/providers/Microsoft.PowerApps/apis/{{internalId}}"}]}""";
}
