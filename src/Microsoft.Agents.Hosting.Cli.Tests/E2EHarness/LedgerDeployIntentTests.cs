using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class LedgerDeployIntentTests
{
    private const string SchemaName = "ahe2e_MCSCli1234";
    private const string EnvironmentId = "a7975ddd-87fe-e986-9280-bfe47a2e7b10";
    private const string AgentId = "b202ea40-60b0-f111-aaab-7ced8d71328f";

    private static Ledger NewLedger(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"ahledger-{Guid.NewGuid():N}");
        return Ledger.Create(directory, "20260914-165017-322f");
    }

    [Fact]
    public void AnIntentIsOutstandingBeforeTheDeployReturns()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            ledger.RecordIntendedMcsAgent(SchemaName, EnvironmentId, @"C:\ws\project", "e2e", "MA-59");

            var entry = Assert.Single(ledger.Outstanding);
            Assert.Equal(SchemaName, entry.Id);
            Assert.Equal(SchemaName, entry.Cleanup.SchemaName);
            Assert.Equal(@"C:\ws\project", entry.Cleanup.ProjectDirectory);
            Assert.Equal("e2e", entry.Cleanup.DeploymentName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolvingNamesTheAgentWithoutAddingAnEntry()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            ledger.RecordIntendedMcsAgent(SchemaName, EnvironmentId, @"C:\ws\project", "e2e", "MA-59");
            ledger.ResolveMcsAgent(SchemaName, AgentId);
            ledger.RecordMcsAgent(AgentId, EnvironmentId, SchemaName, @"C:\ws\project", "e2e", "MA-59");

            var entry = Assert.Single(ledger.Entries);
            Assert.Equal(AgentId, entry.Id);
            Assert.Equal(SchemaName, entry.Cleanup.SchemaName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void APromotionOfTheSameWorkspaceDoesNotAddASecondEntry()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            ledger.RecordIntendedMcsAgent(SchemaName, EnvironmentId, @"C:\ws\project", "e2e", "MA-19");
            ledger.ResolveMcsAgent(SchemaName, AgentId);
            ledger.RecordIntendedMcsAgent(SchemaName, "other-environment", @"C:\ws\project", "e2e-test", "MA-52");

            Assert.Single(ledger.Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnIntentSurvivesReopeningTheLedger()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            ledger.RecordIntendedMcsAgent(SchemaName, EnvironmentId, @"C:\ws\project", "e2e", "MA-59");

            var reopened = Ledger.Open(directory, "20260914-165017-322f");
            var entry = Assert.Single(reopened.Outstanding);

            Assert.Equal(SchemaName, entry.Id);
            Assert.Equal(SchemaName, entry.Cleanup.SchemaName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
