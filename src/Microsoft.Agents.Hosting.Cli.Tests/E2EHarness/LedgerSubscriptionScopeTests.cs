using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class LedgerSubscriptionScopeTests : IDisposable
{
    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), "msagent-ledger-" + Guid.NewGuid().ToString("N"));

    public LedgerSubscriptionScopeTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    private Ledger NewLedger() => Ledger.Create(_runDirectory, "test-run");

    [Fact]
    public void ABotRecordsTheSubscriptionItWasCreatedIn()
    {
        var entry = NewLedger().RecordAzureBot("bot-1", "rg-1", "11111111-1111-1111-1111-111111111111", "AB-01");

        Assert.Equal("11111111-1111-1111-1111-111111111111", entry.Cleanup.SubscriptionId);
        Assert.Equal("rg-1", entry.Cleanup.ResourceGroup);
    }

    [Fact]
    public void TheManualCommandTargetsThatSubscription()
    {
        var entry = NewLedger().RecordAzureBot("bot-1", "rg-1", "11111111-1111-1111-1111-111111111111", "AB-01");

        var command = Cleanup.ManualCommand(entry);

        Assert.Contains("--subscription 11111111-1111-1111-1111-111111111111", command, StringComparison.Ordinal);
        Assert.Contains("--resource-group rg-1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubscriptionSurvivesALedgerRoundTrip()
    {
        NewLedger().RecordAzureBot("bot-1", "rg-1", "11111111-1111-1111-1111-111111111111", "AB-01");

        var reopened = Ledger.Open(_runDirectory, "test-run");

        var entry = Assert.Single(reopened.Entries, candidate => candidate.Kind == ResourceKind.AzureBot);
        Assert.Equal("11111111-1111-1111-1111-111111111111", entry.Cleanup.SubscriptionId);
    }
}
