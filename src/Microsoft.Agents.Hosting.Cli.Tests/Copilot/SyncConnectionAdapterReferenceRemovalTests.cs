using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncConnectionAdapterReferenceRemovalTests : IDisposable
{
    private const string LogicalName = "crf9a_nb1.shared_onedriveforbusiness.20cc67c27fb74e77979fc130f181469d";

    private const string OtherLogicalName = "crf9a_nb1.shared_teams.7a3f0c1e9b2d4f5a8c6b1d0e2f3a4b5c";

    private static readonly Guid RecordId = Guid.Parse("8f14e45f-ceea-467a-9ae9-1b2c3d4e5f60");

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ah-reference-removal-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private static CopilotEnvironmentContext Environment() => new()
    {
        EnvironmentId = "env-1",
        TenantId = "11111111-2222-3333-4444-555555555555",
        DataverseOrganization = new Uri("https://contoso.crm.dynamics.com/"),
    };

    private static RecordingConnectionManagementService Removing() =>
        new() { RemovalReturns = new ConnectionReferenceRemovalResult { Removed = true } };

    private static RecordingConnectionReferenceClient Holding(string logicalName = LogicalName)
    {
        var dataverse = new RecordingConnectionReferenceClient();

        dataverse.References[logicalName] = new SyncDataverseClient.ConnectionReferenceInfo
        {
            ConnectionReferenceId = RecordId,
            ConnectionReferenceLogicalName = logicalName,
            ConnectorId = "shared_onedriveforbusiness",
        };

        return dataverse;
    }

    private string BaselinePath => Path.Combine(_workspace, ".mcs", "botdefinition.json");

    private string ReadBaseline() => File.ReadAllText(BaselinePath);

    /// <summary>Seeds the snapshot both local edits and remote drift are measured against.</summary>
    private BotDefinition SeedBaseline(params string[] logicalNames)
    {
        var seeded = BotDefinition.Empty
            .WithFlows([new CloudFlowDefinition.Builder { DisplayName = "NB Workflow1" }.Build()])
            .WithConnectionReferences([.. logicalNames.Select(Reference)]);

        Directory.CreateDirectory(Path.Combine(_workspace, ".mcs"));
        File.WriteAllText(BaselinePath, CopilotTestFixtures.CloudCacheJson(seeded));

        return seeded;
    }

    private static ConnectionReference Reference(string logicalName) =>
        new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName(logicalName),
            ConnectorId = new ConnectorId("shared_onedriveforbusiness"),
        }.Build();

    private static IReadOnlyList<string> BaselineNames(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("connectionReferences", out var references)
            ? [.. references.EnumerateArray().Select(reference => reference.GetProperty("connectionReferenceLogicalName").GetString()!)]
            : [];
    }

    private async Task<(CopilotConnectionReferenceRemoval Removal, CapturingHttpMessageHandler Handler)> RemoveAsync(
        RecordingConnectionManagementService connections,
        RecordingConnectionReferenceClient dataverse,
        HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var handler = new CapturingHttpMessageHandler { Status = status };

        var removal = await CopilotTestFixtures
            .ConnectionAdapter(connections, dataverseClient: dataverse, handlerFactory: () => handler)
            .RemoveConnectionReferenceAsync(Environment(), _workspace, LogicalName, confirmed: false, CancellationToken.None);

        return (removal, handler);
    }

    private static int DeleteCount(CapturingHttpMessageHandler handler) =>
        handler.Requests.Count(request => request.Method == HttpMethod.Delete);

    [Fact]
    public async Task RemovingAReferenceDropsOnlyItFromTheBaseline()
    {
        SeedBaseline(LogicalName, OtherLogicalName);

        await RemoveAsync(Removing(), Holding());

        Assert.Equal([OtherLogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task RemovingAReferenceLeavesEveryOtherComponentsLastSyncedStateAlone()
    {
        var seeded = SeedBaseline(LogicalName, OtherLogicalName);

        await RemoveAsync(Removing(), Holding());

        Assert.Equal(CopilotTestFixtures.CloudCacheJson(seeded.WithConnectionReferences([Reference(OtherLogicalName)])), ReadBaseline());
    }

    [Fact]
    public async Task RemovingAReferenceNeverRereadsTheAgentFromTheEnvironment()
    {
        SeedBaseline(LogicalName);

        var synchronizer = new RecordingWorkspaceSynchronizer();

        await CopilotTestFixtures
            .ConnectionAdapter(Removing(), synchronizer: synchronizer, dataverseClient: Holding(), handlerFactory: () => new CapturingHttpMessageHandler { Status = HttpStatusCode.NoContent })
            .RemoveConnectionReferenceAsync(Environment(), _workspace, LogicalName, confirmed: false, CancellationToken.None);

        Assert.Equal(0, synchronizer.GetWorkflowsCalls);
        Assert.Equal(0, synchronizer.GetAIPromptsCalls);
    }

    [Fact]
    public async Task AReferenceTheEnvironmentDoesNotHoldIsStillDroppedFromTheBaseline()
    {
        SeedBaseline(LogicalName, OtherLogicalName);

        var (removal, handler) = await RemoveAsync(Removing(), new RecordingConnectionReferenceClient());

        Assert.True(removal.Removed);
        Assert.False(removal.RecordDeleted);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Equal([OtherLogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task AWorkspaceWithNoBaselineYetIsLeftAsItIs()
    {
        Directory.CreateDirectory(_workspace);

        var (removal, _) = await RemoveAsync(Removing(), Holding());

        Assert.True(removal.Removed);
        Assert.False(File.Exists(BaselinePath));
    }

    [Fact]
    public async Task ABaselineStillInTheLegacyYamlFormatIsReadAndRewrittenAsJson()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".mcs"));

        File.WriteAllText(
            Path.Combine(_workspace, ".mcs", "botdefinition.yml"),
            YamlSerializer.Serialize(BotDefinition.Empty.WithConnectionReferences([Reference(LogicalName), Reference(OtherLogicalName)])));

        await RemoveAsync(Removing(), Holding());

        Assert.Equal([OtherLogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task ABaselineThatCannotBeReadNamesThePullThatFixesIt()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, ".mcs"));
        File.WriteAllText(BaselinePath, "{ not json");

        var handler = new CapturingHttpMessageHandler { Status = HttpStatusCode.NoContent };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => CopilotTestFixtures
                .ConnectionAdapter(Removing(), dataverseClient: Holding(), handlerFactory: () => handler)
                .RemoveConnectionReferenceAsync(Environment(), _workspace, LogicalName, confirmed: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectionReferenceCacheStale, failure.ErrorKind);
        Assert.Equal(1, DeleteCount(handler));
        Assert.Contains("msagent agent pull", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAReferenceDeletesItsConnectionReferenceRecord()
    {
        SeedBaseline(LogicalName);

        var (removal, handler) = await RemoveAsync(Removing(), Holding());

        Assert.True(removal.Removed);
        Assert.True(removal.RecordDeleted);

        var deleted = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);

        Assert.Equal($"/api/data/v9.2/connectionreferences({RecordId})", deleted.Uri.AbsolutePath);
    }

    [Fact]
    public async Task ARemovalTheLibraryRefusedDeletesNothing()
    {
        SeedBaseline(LogicalName);

        var (removal, handler) = await RemoveAsync(new RecordingConnectionManagementService(), Holding());

        Assert.False(removal.Removed);
        Assert.False(removal.RecordDeleted);
        Assert.Equal(0, DeleteCount(handler));
        Assert.Equal([LogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task ARecordThatCannotBeDeletedIsReportedAgainstTheEnvironmentAndKeepsTheBaseline()
    {
        SeedBaseline(LogicalName);

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => RemoveAsync(Removing(), Holding(), HttpStatusCode.Forbidden));

        Assert.Equal(CopilotErrorKinds.ConnectionReferenceNotDeleted, failure.ErrorKind);
        Assert.Contains(LogicalName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("refuses to delete", failure.Remediation!, StringComparison.Ordinal);
        Assert.Equal([LogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task ARecordAnotherComponentDependsOnReportsWhatTheEnvironmentSaid()
    {
        SeedBaseline(LogicalName);

        var handler = new CapturingHttpMessageHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":{"code":"0x80060465","message":"connectionreference With Id = 8f14e45f-ceea-467a-9ae9-1b2c3d4e5f60 cannot be deleted because it is referenced by Workflow Expense Approval."}}""",
        };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(
            () => CopilotTestFixtures
                .ConnectionAdapter(Removing(), dataverseClient: Holding(), handlerFactory: () => handler)
                .RemoveConnectionReferenceAsync(Environment(), _workspace, LogicalName, confirmed: false, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.ConnectionReferenceNotDeleted, failure.ErrorKind);
        Assert.Contains("Expense Approval", failure.Message, StringComparison.Ordinal);
        Assert.Contains("another component still depends on", failure.Remediation!, StringComparison.Ordinal);
        Assert.Equal([LogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task ARecordLookupThatFailsIsReportedRatherThanDeletingNothingQuietly()
    {
        SeedBaseline(LogicalName);

        var dataverse = new RecordingConnectionReferenceClient { QueryThrows = new InvalidOperationException("the query failed") };

        var failure = await Assert.ThrowsAsync<CopilotStudioException>(() => RemoveAsync(Removing(), dataverse));

        Assert.Equal(CopilotErrorKinds.ConnectionReferenceNotDeleted, failure.ErrorKind);
        Assert.Equal([LogicalName], BaselineNames(ReadBaseline()));
    }

    [Fact]
    public async Task ACancelledRemovalDeletesNothing()
    {
        SeedBaseline(LogicalName);

        using var cancellation = new CancellationTokenSource();
        var handler = new CapturingHttpMessageHandler();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CopilotTestFixtures
                .ConnectionAdapter(Removing(), dataverseClient: Holding(), handlerFactory: () => handler)
                .RemoveConnectionReferenceAsync(Environment(), _workspace, LogicalName, confirmed: false, cancellation.Token));

        Assert.Equal(0, DeleteCount(handler));
        Assert.Equal([LogicalName], BaselineNames(ReadBaseline()));
    }
}
