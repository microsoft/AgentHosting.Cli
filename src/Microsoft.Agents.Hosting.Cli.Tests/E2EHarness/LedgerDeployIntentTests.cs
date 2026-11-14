using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class LedgerDeployIntentTests
{
    private const string SchemaName = "ahe2e_MCSCli1234";
    private const string EnvironmentId = "a7975ddd-87fe-e986-9280-bfe47a2e7b10";
    private const string AgentId = "b202ea40-60b0-f111-aaab-7ced8d71328f";
    private const string RecordId = "5d0c3f7e-2b8a-4c61-9e47-1f3a6b8d2c90";
    private const string ReregisteredRecordId = "9a4e1b62-7c3d-4f85-8b06-3e5d7f9a1c24";
    private const string FirstWorkflowId = "0b6f2c1e-4d8a-4e57-9c3b-7a1e5d2f8c46";
    private const string SecondWorkflowId = "6e3a9d4b-1f7c-4b28-8d5e-2c9b7f1a3e60";

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

    [Fact]
    public void ACopyRecordedBeforeItsFirstDeployIsResolvedToTheAgentTheDeployCreated()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            RecordCopy(ledger, RecordId, "MC-35");
            ledger.ResolveMcsAgent(SchemaName, AgentId);
            RecordCopy(ledger, RecordId, "MC-39");

            var entry = Assert.Single(ledger.Entries);
            Assert.Equal(ResourceKind.McsCopy, entry.Kind);
            Assert.Equal(AgentId, entry.Id);
            Assert.Equal(RecordId, entry.Cleanup.AgentId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACopyRegisteredAgainUnderAnotherRecordGetsAnEntryOfItsOwn()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            RecordCopy(ledger, RecordId, "MC-93");
            ledger.ResolveMcsAgent(SchemaName, AgentId);
            RecordCopy(ledger, ReregisteredRecordId, "MC-93");

            Assert.Collection(
                ledger.Entries,
                first => Assert.Equal(AgentId, first.Id),
                second =>
                {
                    Assert.Equal(SchemaName, second.Id);
                    Assert.Equal(ReregisteredRecordId, second.Cleanup.AgentId);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACopysWorkflowsRecordedBeforeItsDeployAreOutstandingOnceEachAfterReopeningTheLedger()
    {
        var ledger = NewLedger(out var directory);

        try
        {
            ledger.RecordCopyWorkflows([FirstWorkflowId, SecondWorkflowId], "ahe2e copy", EnvironmentId, "MC-35");
            ledger.RecordCopyWorkflows([FirstWorkflowId], "ahe2e copy", EnvironmentId, "MC-39");
            ledger.RecordCopyWorkflows([], "ahe2e plain copy", EnvironmentId, "MC-36");

            Assert.Collection(
                Ledger.Open(directory, "20260914-165017-322f").Outstanding,
                first => AssertCopyWorkflow(first, FirstWorkflowId),
                second => AssertCopyWorkflow(second, SecondWorkflowId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertCopyWorkflow(LedgerEntry entry, string workflowId)
    {
        Assert.Equal(ResourceKind.McsCopyWorkflow, entry.Kind);
        Assert.Equal(workflowId, entry.Id);
        Assert.Equal(workflowId, entry.Cleanup.WorkflowId);
        Assert.Equal(EnvironmentId, entry.Cleanup.EnvironmentId);
        Assert.Equal("MC-35", entry.CreatedBy);
    }

    private static void RecordCopy(Ledger ledger, string recordId, string scenarioId) =>
        ledger.RecordCopy(SchemaName, "ahe2e copy", EnvironmentId, @"C:\ws\copy", @"C:\ws\copy\ahe2e-copy", recordId, "copy-dev", scenarioId);
}
