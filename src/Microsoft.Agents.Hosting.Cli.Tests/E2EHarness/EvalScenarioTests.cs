using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

/// <summary>
/// A scored evaluation is paid for in Copilot Credits and takes minutes to come back, so the
/// evaluation scenarios are scheduled only when a run opts into the <c>eval</c> pack. Nothing about
/// that separation is visible until a live run either skips them or pays for them, so it is pinned
/// here instead.
/// </summary>
public sealed class EvalScenarioTests
{
    private static readonly RunFilters Default = new();

    private static readonly RunFilters WithEvalPack = new() { Packs = ["core", "eval"] };

    /// <summary>The scenarios that read the service and store nothing.</summary>
    private static readonly string[] ReadOnlyIds = ["EV-16", "EV-17", "EV-27", "EV-28"];

    private static Scenario Scenario(string id) =>
        McsEvalScenarios.All().SingleOrDefault(scenario => scenario.Id == id)
            ?? throw new InvalidOperationException($"{id} is not an evaluation scenario.");

    [Fact]
    public void NoEvaluationScenarioIsScheduledWithTheAgentSuite()
    {
        var agentSuite = McsAgentScenarios.All().Select(scenario => scenario.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(McsEvalScenarios.All(), scenario => Assert.DoesNotContain(scenario.Id, agentSuite));
    }

    [Fact]
    public void EveryEvaluationScenarioCarriesItsOwnFeature() =>
        Assert.All(McsEvalScenarios.All(), scenario => Assert.Equal("mcs-eval", scenario.Feature));

    [Fact]
    public void TheEvaluationFeatureCanBeScopedTo() => Assert.Contains("mcs-eval", ScenarioCatalogue.Features);

    [Fact]
    public void TheCatalogueSchedulesEveryEvaluationScenario()
    {
        var catalogue = ScenarioCatalogue.All().Select(scenario => scenario.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(McsEvalScenarios.All(), scenario => Assert.Contains(scenario.Id, catalogue));
    }

    [Fact]
    public void ADefaultRunPaysForNoEvaluation() =>
        Assert.DoesNotContain(ScenarioCatalogue.Select(Default), scenario => scenario.Feature == "mcs-eval");

    [Fact]
    public void TheEvalPackSchedulesEveryEvaluationScenario() =>
        Assert.Equal(
            McsEvalScenarios.All().Select(scenario => scenario.Id).Order(StringComparer.Ordinal),
            ScenarioCatalogue.Select(WithEvalPack).Where(scenario => scenario.Feature == "mcs-eval").Select(scenario => scenario.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void TheEvalPackCreatesNoAzureResources() =>
        Assert.DoesNotContain(
            ScenarioCatalogue.Select(WithEvalPack),
            scenario => ScenarioCatalogue.AzurePack.Contains(scenario.Feature, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void TheAzurePackPaysForNoEvaluation() =>
        Assert.DoesNotContain(
            ScenarioCatalogue.Select(new RunFilters { Packs = ["core", "azure"] }),
            scenario => scenario.Feature == "mcs-eval");

    [Fact]
    public void EveryEvaluationScenarioHasItsOwnId()
    {
        var ids = McsEvalScenarios.All().Select(scenario => scenario.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoEvaluationIdCollidesWithAnotherFeatures()
    {
        var ids = ScenarioCatalogue.All().Select(scenario => scenario.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("EV-01")]
    [InlineData("EV-02")]
    [InlineData("EV-03")]
    [InlineData("EV-04")]
    [InlineData("EV-05")]
    [InlineData("EV-06")]
    [InlineData("EV-07")]
    [InlineData("EV-08")]
    public void ACaseTheCommandLineRejectsOnItsOwnNeedsNoEnvironment(string id) => Assert.Empty(Scenario(id).Requires);

    [Fact]
    public void EveryScenarioThatReachesTheServiceRequiresTheDevEnvironment() =>
        Assert.All(
            McsEvalScenarios.All().Where(scenario => scenario.Requires.Count > 0),
            scenario => Assert.Contains(Capability.McsDev, scenario.Requires));

    [Fact]
    public void EveryScenarioThatStoresOrScoresSomethingIsTieredAsCreating() =>
        Assert.All(
            McsEvalScenarios.All().Where(scenario => scenario.Tier == SafetyTier.Creating),
            scenario => Assert.Contains(Capability.McsDev, scenario.Requires));

    [Fact]
    public void NoScenarioThatOnlyReadsIsTieredAsCreating() =>
        Assert.All(
            ReadOnlyIds.Select(Scenario),
            scenario => Assert.Equal(SafetyTier.ReadOnly, scenario.Tier));

    [Fact]
    public void TheSmokeSubsetSchedulesNeitherAScoredRunNorAProjectResolution() =>
        Assert.DoesNotContain(
            ScenarioCatalogue.Select(SmokeWithEvalPack),
            scenario => scenario.Id is "EV-25" or "EV-26" or "EV-27" or "EV-28");

    [Fact]
    public void BothWorkspaceShapesResolveAnAgentFromItsProject()
    {
        Assert.DoesNotContain(Capability.WorkspaceClassic, Scenario("EV-27").Requires);
        Assert.Contains(Capability.WorkspaceClassic, Scenario("EV-28").Requires);
    }

    /// <summary>
    /// Every other evaluation scenario names the agent with <c>--agent-id</c>, which makes the
    /// workspace shape irrelevant. Only the classic resolution needs a bundled workspace on disk, so
    /// it is the one that would be silently dropped if its capability were lost.
    /// </summary>
    [Fact]
    public void OnlyTheClassicProjectScenarioNeedsABundledWorkspace() =>
        Assert.Equal(
            ClassicProjectScenario,
            McsEvalScenarios.All()
                .Where(scenario => scenario.Requires.Contains(Capability.WorkspaceCli) || scenario.Requires.Contains(Capability.WorkspaceClassic))
                .Select(scenario => scenario.Id));

    private static readonly RunFilters SmokeWithEvalPack = new() { Packs = ["core", "eval"], Smoke = true };

    private static readonly string[] ClassicProjectScenario = ["EV-28"];

    [Fact]
    public void AnOrphanedRunTellsTheOperatorHowToStopIt()
    {
        var ledger = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-manual-" + Guid.NewGuid().ToString()[..8]), "run-id");
        var entry = ledger.RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");
        ledger.ResolveEvaluationRun("EV-25:Nightly", "run-9");

        var command = Cleanup.ManualCommand(entry);

        Assert.Contains("msagent agent eval cancel", command, StringComparison.Ordinal);
        Assert.Contains("--run-id run-9", command, StringComparison.Ordinal);
        Assert.Contains("--agent-id bot-1", command, StringComparison.Ordinal);
        Assert.Contains("--environment-id env-1", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrphanedRunThatWasNeverNamedPointsAtTheNameInsteadOfTheIntent()
    {
        var entry = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-manual-" + Guid.NewGuid().ToString()[..8]), "run-id")
            .RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        var command = Cleanup.ManualCommand(entry);

        Assert.Contains("msagent agent eval list", command, StringComparison.Ordinal);
        Assert.Contains("named 'Nightly'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--run-id EV-25:Nightly", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunIntentIsMarkedUnresolvedUntilTheServiceNamesTheRun()
    {
        var ledger = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-intent-" + Guid.NewGuid().ToString()[..8]), "run-id");

        var entry = ledger.RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        Assert.False(entry.Resolved);
        Assert.Equal("Nightly", entry.Cleanup.Correlation);

        ledger.ResolveEvaluationRun("EV-25:Nightly", "run-9");

        Assert.True(entry.Resolved);
        Assert.Equal("run-9", entry.Id);
    }

    [Fact]
    public void AResourceRecordedByItsServiceAssignedIdIsResolvedFromTheStart()
    {
        var ledger = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-resolved-" + Guid.NewGuid().ToString()[..8]), "run-id");

        Assert.True(ledger.RecordEvaluationTestSet("Nightly", "env-1", "bot-1", "EV-09").Resolved);
        Assert.False(ledger.RecordIntendedMcsAgent("schema1", "env-1", "C:\\src", "dev", "MCS-01").Resolved);
    }

    [Fact]
    public void AnUnresolvedRunIntentSurvivesReopeningTheLedger()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ahe2e-reopen-" + Guid.NewGuid().ToString()[..8]);
        Ledger.Create(directory, "run-id").RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        var reopened = Ledger.Open(directory, "run-id").Outstanding.Single(entry => entry.Kind == ResourceKind.EvaluationRun);

        Assert.False(reopened.Resolved);
        Assert.Equal("Nightly", reopened.Cleanup.Correlation);
    }

    [Fact]
    public void ALedgerWrittenBeforeIntentsWereTrackedStillReadsBackAsUnresolved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ahe2e-legacy-" + Guid.NewGuid().ToString()[..8]);
        Ledger.Create(directory, "run-id").RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        var path = Path.Combine(directory, "ledger.json");
        File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), ",\\s*\"resolved\":\\s*(?:true|false)", string.Empty, RegexOptions.IgnoreCase));

        Assert.DoesNotContain("resolved", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.False(Ledger.Open(directory, "run-id").Outstanding.Single(entry => entry.Kind == ResourceKind.EvaluationRun).Resolved);
    }

    [Fact]
    public void ALedgerReopenedAfterTheRunWasNamedStillReadsBackAsResolved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ahe2e-named-" + Guid.NewGuid().ToString()[..8]);
        var ledger = Ledger.Create(directory, "run-id");
        ledger.RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");
        ledger.ResolveEvaluationRun("EV-25:Nightly", "run-9");

        var reopened = Ledger.Open(directory, "run-id").Outstanding.Single(entry => entry.Kind == ResourceKind.EvaluationRun);

        Assert.True(reopened.Resolved);
        Assert.Equal("run-9", reopened.Id);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-4, true)]
    [InlineData(4, true)]
    [InlineData(-30, false)]
    [InlineData(30, false)]
    public void OnlyARunStartedNearTheIntentIsTakenToBeTheOneItStarted(int minutesFromIntent, bool matches)
    {
        var entry = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-window-" + Guid.NewGuid().ToString()[..8]), "run-id")
            .RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        var run = new EvaluationRunRow
        {
            RunId = "run-9",
            State = "Running",
            StartTime = entry.CreatedAt.AddMinutes(minutesFromIntent),
        };

        Assert.Equal(matches, Cleanup.StartedWithTheIntent(run, entry));
    }

    [Fact]
    public void ARunWithNoStartTimeIsKeptRatherThanRuledOut()
    {
        var entry = Ledger.Create(Path.Combine(Path.GetTempPath(), "ahe2e-nostart-" + Guid.NewGuid().ToString()[..8]), "run-id")
            .RecordEvaluationRun("EV-25:Nightly", "env-1", "bot-1", "Nightly", "EV-25");

        Assert.True(Cleanup.StartedWithTheIntent(new EvaluationRunRow { RunId = "run-9", State = "Running" }, entry));
    }
}
