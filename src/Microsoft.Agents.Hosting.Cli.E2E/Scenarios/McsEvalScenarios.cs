using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>
/// Copilot Studio evaluation: conversation test sets, generated conversations, and the scored runs
/// that grade an agent.
///
/// <para>These carry their own feature and their own <c>eval</c> pack rather than sitting with the
/// agent scenarios, because a scored run is paid for in Copilot Credits and the service takes
/// minutes to return one. A default run must therefore neither pay for an evaluation nor wait on
/// one. They reuse the run's session agent, so scheduling the pack on its own deploys exactly one
/// agent and deletes it again.</para>
/// </summary>
public static class McsEvalScenarios
{
    private static readonly ScenarioBuilder Builder = new("mcs-eval");

    private static readonly Capability[] Dev = [Capability.Auth, Capability.McsDev];

    private static readonly Capability[] DevClassic = [Capability.Auth, Capability.McsDev, Capability.WorkspaceClassic];

    /// <summary>A run id no environment holds, for the cases rejected once it is looked up.</summary>
    private const string UnusedRunId = "00000000-0000-0000-0000-000000000003";

    /// <summary>A test set name no agent holds, for the cases rejected once it is looked up.</summary>
    private const string UnusedTestSet = "ahe2e-no-such-test-set";

    /// <summary>How long a watched run is given before the scenario stops waiting on the service.</summary>
    private const int WatchTimeoutMinutes = 8;

    /// <summary>How long a stopped run is given to reach a state the service will do no more work on.</summary>
    private static readonly TimeSpan TerminalWait = TimeSpan.FromMinutes(5);

    /// <summary>How long a started run is given to appear in the agent's listing.</summary>
    private static readonly TimeSpan ListingWait = TimeSpan.FromMinutes(1);

    /// <summary>Every evaluation scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() =>
    [
        .. Arguments(),
        .. Rejections(),
        .. Reading(),
        .. Projects(),
        .. TestSets(),
        .. Runs(),
    ];

    /// <summary>The cases the command line rejects on its own, before any network call.</summary>
    private static IEnumerable<Scenario> Arguments()
    {
        yield return Builder.Make("EV-01", "agent eval run with neither a test set nor a source is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "run", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a run with nothing to score is a usage error");
            context.Assert.Contains(result, "Name an existing test set with --testset", "the failure names every way to supply conversations");
        });

        yield return Builder.Make("EV-02", "agent eval run rejects a source alongside an existing test set", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "run", "--testset", UnusedTestSet, "--generate", "2", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "naming a set and a source at once is a usage error");
            context.Assert.Contains(result, "do not apply", "the failure says which options were ignored");
        });

        yield return Builder.Make("EV-03", "agent eval show without a run id is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "show", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a missing run id is a usage error");
            context.Assert.Contains(result, "'--run-id' is required", "the failure names what is missing");
        });

        yield return Builder.Make("EV-04", "agent eval testset show without a selector is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "testset", "show", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a missing test set selector is a usage error");
            context.Assert.Contains(result, "'--testset' is required", "the failure names what is missing");
        });

        yield return Builder.Make("EV-05", "agent eval testset export without an output file is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "testset", "export", "--testset", UnusedTestSet, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an export with nowhere to write is a usage error");
            context.Assert.Contains(result, "'--output' is required", "the failure names what is missing");
        });

        yield return Builder.Make("EV-06", "agent eval testset create without a name is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "testset", "create", "--generate", "2", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a nameless test set is a usage error");
            context.Assert.Contains(result, "'--name' is required", "the failure names what is missing");
        });

        yield return Builder.Make("EV-07", "agent eval generate rejects a count below one", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "generate", "--count", "0", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "generating nothing is a usage error");
            context.Assert.Contains(result, "--count must be a positive integer", "the failure says what the value must be");
        });

        yield return Builder.Make("EV-08", "agent eval list rejects a limit below one", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "eval", "list", "--limit", "0", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "listing nothing is a usage error");
            context.Assert.Contains(result, "--limit must be a positive integer", "the failure says what the value must be");
        });
    }

    /// <summary>The cases the service or the CSV reader rejects once an agent has been resolved.</summary>
    private static IEnumerable<Scenario> Rejections()
    {
        yield return Builder.Make("EV-09", "agent eval testset create with no conversation source is a usage error", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await RunAsync(context, [.. TestSet(session, "create"), "--name", TestSetName(context), "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a test set with no conversations is a usage error");
            context.Assert.Contains(result, "Supply conversations with --from-csv", "the failure names every source");
        });

        yield return Builder.Make("EV-10", "agent eval testset create rejects a CSV and a generate count together", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var csv = WriteCsv(context, "conversations.csv", (1, "What can you do?"));

            var result = await RunAsync(
                context,
                [.. TestSet(session, "create"), "--name", TestSetName(context), "--from-csv", csv, "--generate", "2", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "two conversation sources at once is a usage error");
            context.Assert.Contains(result, "cannot be combined", "the failure says the two options exclude each other");
        });

        yield return Builder.Make("EV-11", "agent eval testset create names a CSV that is not there", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var missing = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId), "absent.csv");

            var result = await RunAsync(
                context,
                [.. TestSet(session, "create"), "--name", TestSetName(context), "--from-csv", missing, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a missing conversation file is a usage error");
            context.Assert.Contains(result, "the file does not exist", "the failure says what is wrong with the file");
        });

        yield return Builder.Make("EV-12", "agent eval testset create rejects a CSV whose header is wrong", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var csv = WriteLines(context, "wrong-header.csv", ["prompt,reply", "\"What can you do?\",\"\""]);

            var result = await RunAsync(
                context,
                [.. TestSet(session, "create"), "--name", TestSetName(context), "--from-csv", csv, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unreadable conversation file is a usage error");
            context.Assert.Contains(result, "the header must name the columns", "the failure names the columns the template uses");
        });

        yield return Builder.Make("EV-13", "agent eval testset create rejects more conversations than a set holds", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var csv = WriteCsv(context, "too-many.csv", [.. Enumerable.Range(1, 21).Select(number => (number, $"Question {number}?"))]);

            var result = await RunAsync(
                context,
                [.. TestSet(session, "create"), "--name", TestSetName(context), "--from-csv", csv, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "exceeding the service's limit is a usage error");
            context.Assert.Contains(result, "a test set holds at most", "the failure states the limit");
        });

        yield return Builder.Make("EV-14", "agent eval show names a run the environment does not hold", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await RunAsync(context, [.. Eval(session, "show"), "--run-id", UnusedRunId, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unknown run is a usage error");
            context.Assert.Contains(result, UnusedRunId, "the message names what was sought");
        });

        yield return Builder.Make("EV-15", "agent eval testset show names a set the agent does not hold", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await RunAsync(context, [.. TestSet(session, "show"), "--testset", UnusedTestSet, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unknown test set is a usage error");
            context.Assert.Contains(result, UnusedTestSet, "the message names what was sought");
        });
    }

    /// <summary>The reads, which create nothing and are the cheapest proof the surface is wired up.</summary>
    private static IEnumerable<Scenario> Reading()
    {
        yield return Builder.Make("EV-16", "agent eval list reports the agent it was pointed at", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await RunAsync(context, [.. Eval(session, "list"), "--limit", "5", "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(result);

            var listed = result.Json<EvaluationListResult>();
            context.Assert.Equal(session.CloudAgentId, listed.McsAgentId, "the agent read is the one deployed");
            context.Assert.Equal(session.EnvironmentId, listed.EnvironmentId, "the environment is the one deployed to");
            context.Assert.True(listed.Runs.Count <= 5, "no more rows are returned than were asked for");
            context.Assert.True(listed.Runs.All(run => !string.IsNullOrWhiteSpace(run.RunId)), "every row carries a run id");
        });

        yield return Builder.Make("EV-17", "agent eval testset list reports the agent it was pointed at", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var result = await RunAsync(context, [.. TestSet(session, "list"), "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(result);

            var listed = result.Json<EvaluationTestSetListResult>();
            context.Assert.Equal(session.CloudAgentId, listed.McsAgentId, "the agent read is the one deployed");
            context.Assert.True(listed.TestSets.All(set => !string.IsNullOrWhiteSpace(set.TestSetId)), "every row carries an id");
            context.Assert.True(listed.TestSets.All(set => !string.IsNullOrWhiteSpace(set.DisplayName)), "every row carries a name");
        });
    }

    /// <summary>
    /// Resolving the agent from a project directory rather than an explicit id, for both workspace
    /// shapes. This is the path a user standing in a workspace takes, and the only one that reads the
    /// schema name off disk.
    /// </summary>
    private static IEnumerable<Scenario> Projects()
    {
        yield return Builder.Make("EV-27", "agent eval resolves the agent from a CLI-shaped workspace", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            await AssertResolvesFromProjectAsync(context, session).ConfigureAwait(false);
        });

        yield return Builder.Make("EV-28", "agent eval resolves the agent from a classic-shaped workspace", DevClassic, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.FixtureClassic).ConfigureAwait(false);

            await AssertResolvesFromProjectAsync(context, session).ConfigureAwait(false);
        });
    }

    /// <summary>The test-set lifecycle, which stores conversations but scores nothing.</summary>
    private static IEnumerable<Scenario> TestSets()
    {
        yield return Builder.Make("EV-18", "agent eval testset create stores the conversations a CSV holds", Dev, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var csv = WriteCsv(context, "conversations.csv", (1, "What can you do?"), (1, "And what else?"), (2, "Who made you?"));
            var created = await CreateTestSetAsync(context, session, TestSetName(context), csv).ConfigureAwait(false);

            context.Assert.Equal("test-set-created", created.Status, "the set is reported as created");
            context.Assert.True(created.Conversations.Count == 2, "the rows are grouped into the conversations they name");
            context.Assert.True(created.Conversations.Any(conversation => conversation.TurnCount == 2), "a conversation keeps every turn it was given");

            var shown = await ShowTestSetAsync(context, session, created.TestSet.TestSetId).ConfigureAwait(false);
            context.Assert.Equal(created.TestSet.TestSetId, shown.TestSet.TestSetId, "the set reads back by id");
            context.Assert.True(shown.Conversations.Count == 2, "the stored set holds what was sent");

            var listed = await ListTestSetsAsync(context, session).ConfigureAwait(false);
            context.Assert.True(
                listed.TestSets.Any(set => string.Equals(set.TestSetId, created.TestSet.TestSetId, StringComparison.OrdinalIgnoreCase)),
                "the new set is listed");
        });

        yield return Builder.Make("EV-19", "agent eval testset update --replace swaps the conversations a set holds", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            var created = await CreateTestSetAsync(context, session, name, WriteCsv(context, "before.csv", (1, "What can you do?"))).ConfigureAwait(false);

            var after = WriteCsv(context, "after.csv", (1, "Who made you?"), (2, "Where do you run?"));
            var updated = await RunAsync(context, [.. TestSet(session, "update"), "--testset", name, "--from-csv", after, "--replace", "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(updated, "agent eval testset update --replace");

            var replaced = updated.Json<EvaluationTestSetResult>();
            context.Assert.Equal(created.TestSet.TestSetId, replaced.TestSet.TestSetId, "the same set is changed rather than a new one made");
            context.Assert.True(replaced.Conversations.Count == 2, "the set holds the replacements and nothing else");
            context.Assert.True(
                replaced.Conversations.All(conversation => !conversation.FirstQuestion.Contains("What can you do", StringComparison.OrdinalIgnoreCase)),
                "the conversations that were replaced are gone");
        });

        yield return Builder.Make("EV-20", "agent eval testset export writes a CSV the CLI reads back", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            await CreateTestSetAsync(context, session, name, WriteCsv(context, "conversations.csv", (1, "What can you do?"), (2, "Who made you?"))).ConfigureAwait(false);

            var exported = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId), "exported.csv");
            var export = await RunAsync(context, [.. TestSet(session, "export"), "--testset", name, "--output", exported, "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(export, "agent eval testset export");
            context.Assert.FileExists(exported, "the CSV is written");
            context.Assert.FileContains(exported, "conversationNumber", "the export carries the template's header");
            context.Assert.FileContains(exported, "What can you do?", "the export carries the conversations");

            var round = await RunAsync(context, [.. TestSet(session, "update"), "--testset", name, "--from-csv", exported, "--replace", "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(round, "the exported CSV is accepted as input");
            context.Assert.True(round.Json<EvaluationTestSetResult>().Conversations.Count == 2, "the round trip loses no conversation");
        });

        yield return Builder.Make("EV-21", "agent eval testset create refuses a name the agent already uses", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            var csv = WriteCsv(context, "conversations.csv", (1, "What can you do?"));
            await CreateTestSetAsync(context, session, name, csv).ConfigureAwait(false);

            var again = await RunAsync(context, [.. TestSet(session, "create"), "--name", name, "--from-csv", csv, "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(again, 2, "a duplicate name is a usage error");
            context.Assert.Contains(again, "already exists", "the failure says the name is taken");

            var listed = await ListTestSetsAsync(context, session).ConfigureAwait(false);
            context.Assert.True(
                listed.TestSets.Count(set => string.Equals(set.DisplayName, name, StringComparison.OrdinalIgnoreCase)) == 1,
                "the refusal left one set, not two");
        });

        yield return Builder.Make("EV-22", "agent eval testset delete with a mismatched confirmation deletes nothing", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            var created = await CreateTestSetAsync(context, session, name, WriteCsv(context, "conversations.csv", (1, "What can you do?"))).ConfigureAwait(false);

            var declined = await context.Msagent.RunAsync(
                [.. TestSet(session, "delete"), "--testset", name, "--json"],
                context.Workspaces.EmptyDirectory(context.ScenarioId),
                stdin: "definitely-not-the-name",
                cancellationToken: context.CancellationToken).ConfigureAwait(false);

            context.Assert.ExitZero(declined, "declining is not an error");
            context.Assert.Contains(declined, "name-mismatch", "the result says why nothing happened");

            var shown = await ShowTestSetAsync(context, session, created.TestSet.TestSetId).ConfigureAwait(false);
            context.Assert.Equal(created.TestSet.TestSetId, shown.TestSet.TestSetId, "the set survives");
        });

        yield return Builder.Make("EV-23", "agent eval testset delete --force removes the set", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            var created = await CreateTestSetAsync(context, session, name, WriteCsv(context, "conversations.csv", (1, "What can you do?"))).ConfigureAwait(false);

            var deleted = await RunAsync(context, [.. TestSet(session, "delete"), "--testset", name, "--force", "--json", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "agent eval testset delete --force");
            context.Assert.Equal("test-set-deleted", deleted.Json<EvaluationTestSetDeleteResult>().Status, "the set is reported as deleted");
            context.Ledger.MarkDeleted(name);

            var listed = await ListTestSetsAsync(context, session).ConfigureAwait(false);
            context.Assert.False(
                listed.TestSets.Any(set => string.Equals(set.TestSetId, created.TestSet.TestSetId, StringComparison.OrdinalIgnoreCase)),
                "the set is gone from the listing");
        });
    }

    /// <summary>The scored runs, which spend Copilot Credits and wait on the evaluation service.</summary>
    private static IEnumerable<Scenario> Runs()
    {
        yield return Builder.Make("EV-24", "agent eval generate writes conversations it did not store", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var output = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId), "generated.csv");
            var before = (await ListTestSetsAsync(context, session).ConfigureAwait(false)).TestSets.Count;

            var result = await RunAsync(
                context,
                [.. Eval(session, "generate"), "--count", "2", "--turns", "2", "--output", output, "--json", "--non-interactive"],
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(result, "agent eval generate");

            var generated = result.Json<EvaluationGenerateResult>();
            context.Assert.True(generated.Conversations.Count > 0, "the service returned conversations");
            context.Assert.True(generated.Conversations.All(conversation => !string.IsNullOrWhiteSpace(conversation.FirstQuestion)), "every conversation opens with a question");
            context.Assert.FileExists(output, "the conversations are written as CSV");
            context.Assert.FileContains(output, "conversationNumber", "the CSV carries the template's header");

            var after = (await ListTestSetsAsync(context, session).ConfigureAwait(false)).TestSets.Count;
            context.Assert.True(after == before, "generating stores no test set");
        });

        yield return Builder.Make("EV-25", "an evaluation run is started, listed, shown, stopped and removed", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            await CreateTestSetAsync(context, session, name, WriteCsv(context, "conversations.csv", (1, "What can you do?"))).ConfigureAwait(false);

            var started = await StartRunAsync(context, session, name).ConfigureAwait(false);
            context.Assert.Equal("evaluation-started", started.Status, "the run is reported as started");
            context.Assert.True(!string.IsNullOrWhiteSpace(started.Run.RunId), "the started run carries an id");

            await context.Assert.EventuallyAsync(
                async () =>
                {
                    var listed = await RunAsync(context, [.. Eval(session, "list"), "--limit", "20", "--json", "--non-interactive"]).ConfigureAwait(false);

                    return listed.ExitCode == 0
                        && listed.Json<EvaluationListResult>().Runs.Any(run => string.Equals(run.RunId, started.Run.RunId, StringComparison.OrdinalIgnoreCase));
                },
                ListingWait,
                "the started run is listed").ConfigureAwait(false);

            var shown = await ShowRunAsync(context, session, started.Run.RunId).ConfigureAwait(false);
            context.Assert.Equal(started.Run.RunId, shown.Run.RunId, "the run reads back by id");

            await StopAsync(context, session, started.Run.RunId).ConfigureAwait(false);
            await DeleteRunAsync(context, session, started.Run.RunId).ConfigureAwait(false);
            await AssertRemovedAsync(context, session, started.Run.RunId).ConfigureAwait(false);
        });

        yield return Builder.Make("EV-26", "agent eval run --watch scores every conversation and writes the report", Dev, SafetyTier.Creating, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var name = TestSetName(context);
            await CreateTestSetAsync(context, session, name, WriteCsv(context, "conversations.csv", (1, "What can you do?"), (2, "Who made you?"))).ConfigureAwait(false);

            var output = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId), "run.json");

            var watched = await StartRunAsync(
                context,
                session,
                name,
                "--watch",
                "--timeout-minutes", $"{WatchTimeoutMinutes}",
                "--output", output).ConfigureAwait(false);

            if (watched.TimedOut)
            {
                await StopAsync(context, session, watched.Run.RunId).ConfigureAwait(false);

                throw new ScenarioBlockedException(
                    $"the evaluation had not finished after {WatchTimeoutMinutes} minutes; the run was stopped and left for the agent's deletion to remove",
                    "evaluation slower than the wait");
            }

            context.Assert.Equal("evaluation-completed", watched.Status, "a watched run reports the finished run");
            context.Assert.Equal(EvaluationRunStates.Completed, watched.Run.State, "the run finished rather than failing or being stopped");
            context.Assert.True(watched.Cases.Count == 2, "every conversation in the set was scored");
            context.Assert.True(
                watched.Cases.All(testCase => !string.IsNullOrWhiteSpace(testCase.EvaluationResult)),
                "the grader reached a verdict on every conversation");
            context.Assert.FileExists(output, "the finished run is written to the output file");

            var transcript = await RunAsync(context, [.. Eval(session, "show"), "--run-id", watched.Run.RunId, "--transcript", "--json", "--non-interactive"]).ConfigureAwait(false);
            context.Assert.ExitZero(transcript, "agent eval show --transcript");

            var printed = transcript.Json<EvaluationRunResult>();
            context.Assert.True(printed.TranscriptNumbers.Count == watched.Cases.Count, "every scored conversation can be printed back");
            context.Assert.True(
                printed.Cases.All(testCase => testCase.Turns.Count > 0),
                "every printed conversation carries the turns it ran");

            await DeleteRunAsync(context, session, watched.Run.RunId).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Starts a run against a stored test set and asserts the service accepted it.
    /// </summary>
    private static async Task<EvaluationRunResult> StartRunAsync(ScenarioContext context, SessionAgent session, string testSet, params string[] extraArguments)
    {
        var intent = $"{context.ScenarioId}:{testSet}";
        context.Ledger.RecordEvaluationRun(intent, session.EnvironmentId, session.CloudAgentId, testSet, context.ScenarioId);

        var result = await RunAsync(
            context,
            [.. Eval(session, "run"), "--testset", testSet, "--json", "--non-interactive", .. extraArguments],
            TimeSpan.FromMinutes(WatchTimeoutMinutes + 2)).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval run");

        var started = result.Json<EvaluationRunResult>();
        context.Ledger.ResolveEvaluationRun(intent, started.Run.RunId);

        return started;
    }

    /// <summary>
    /// Stops a run and waits for the service to stop working on it.
    ///
    /// <para>Whether a stop lands is a race with the service: a one-conversation run can finish
    /// before the request arrives, and the command then refuses it rather than lying about what it
    /// did. Both answers are correct, so each is asserted on its own terms and the wait afterwards
    /// is what both have to satisfy.</para>
    /// </summary>
    private static async Task StopAsync(ScenarioContext context, SessionAgent session, string runId)
    {
        var cancelled = await RunAsync(context, [.. Eval(session, "cancel"), "--run-id", runId, "--json", "--non-interactive"]).ConfigureAwait(false);

        if (cancelled.ExitCode == 0)
        {
            context.Assert.Equal("evaluation-cancelled", cancelled.Json<EvaluationRunResult>().Status, "the run is reported as stopped");
        }
        else
        {
            context.Assert.ExitCode(cancelled, 2, "a run the service already finished cannot be stopped");
            context.Assert.Contains(cancelled, "is already", "the refusal names the state the run reached");
        }

        await context.Assert.EventuallyAsync(
            async () =>
            {
                var current = await RunAsync(context, [.. Eval(session, "show"), "--run-id", runId, "--json", "--non-interactive"]).ConfigureAwait(false);

                return current.ExitCode == 0 && EvaluationRunStates.IsTerminal(current.Json<EvaluationRunResult>().Run.State);
            },
            TerminalWait,
            "the stopped run reaches a state the service does no more work on").ConfigureAwait(false);
    }

    /// <summary>Deletes a run that has already finished.</summary>
    private static async Task DeleteRunAsync(ScenarioContext context, SessionAgent session, string runId)
    {
        var deleted = await RunAsync(context, [.. Eval(session, "delete"), "--run-id", runId, "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(deleted, "agent eval delete");
        context.Assert.Equal("evaluation-deleted", deleted.Json<EvaluationRunResult>().Status, "the run is reported as deleted");
        context.Ledger.MarkDeleted(runId);
    }

    /// <summary>
    /// Asserts a deleted run is gone, whichever way the service expresses that: some environments
    /// stop answering for it, and others keep the record and report it deleted.
    /// </summary>
    private static async Task AssertRemovedAsync(ScenarioContext context, SessionAgent session, string runId)
    {
        var read = await RunAsync(context, [.. Eval(session, "show"), "--run-id", runId, "--json", "--non-interactive"]).ConfigureAwait(false);

        if (read.ExitCode == 0)
        {
            context.Assert.Equal(EvaluationRunStates.Deleted, read.Json<EvaluationRunResult>().Run.State, "a run the service keeps after a delete reports itself deleted");
        }
        else
        {
            context.Assert.ExitCode(read, 2, "the deleted run can no longer be read");
        }
    }

    private static async Task<EvaluationRunResult> ShowRunAsync(ScenarioContext context, SessionAgent session, string runId)
    {
        var result = await RunAsync(context, [.. Eval(session, "show"), "--run-id", runId, "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval show");

        return result.Json<EvaluationRunResult>();
    }

    /// <summary>Records the set in the ledger before creating it, so a crash mid-create still leaves a cleanup trail.</summary>
    private static async Task<EvaluationTestSetResult> CreateTestSetAsync(ScenarioContext context, SessionAgent session, string name, string csvPath)
    {
        context.Ledger.RecordEvaluationTestSet(name, session.EnvironmentId, session.CloudAgentId, context.ScenarioId);

        var result = await RunAsync(context, [.. TestSet(session, "create"), "--name", name, "--from-csv", csvPath, "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval testset create");

        return result.Json<EvaluationTestSetResult>();
    }

    private static async Task<EvaluationTestSetResult> ShowTestSetAsync(ScenarioContext context, SessionAgent session, string selector)
    {
        var result = await RunAsync(context, [.. TestSet(session, "show"), "--testset", selector, "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval testset show");

        return result.Json<EvaluationTestSetResult>();
    }

    private static async Task<EvaluationTestSetListResult> ListTestSetsAsync(ScenarioContext context, SessionAgent session)
    {
        var result = await RunAsync(context, [.. TestSet(session, "list"), "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval testset list");

        return result.Json<EvaluationTestSetListResult>();
    }

    /// <summary>
    /// Reads an agent's test sets through <c>--project</c> rather than <c>--agent-id</c>, which makes
    /// the CLI resolve the local record and the schema name its workspace declares. The environment is
    /// still named so the scenario does not depend on whichever one the machine has selected.
    /// </summary>
    private static async Task AssertResolvesFromProjectAsync(ScenarioContext context, SessionAgent session)
    {
        var result = await RunAsync(
            context,
            ["agent", "eval", "testset", "list", "--project", session.Workspace.ProjectDirectory, "--environment-id", session.EnvironmentId, "--json", "--non-interactive"]).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent eval testset list --project");

        var listed = result.Json<EvaluationTestSetListResult>();
        context.Assert.Equal(session.CloudAgentId, listed.McsAgentId, "the workspace resolves to the agent it deployed");
        context.Assert.Equal(session.EnvironmentId, listed.EnvironmentId, "the environment is the one deployed to");
    }

    /// <summary>
    /// Addresses the session agent by the id the service assigned it rather than through a project,
    /// so no evaluation scenario depends on the working directory it happens to run in.
    /// </summary>
    private static string[] Eval(SessionAgent session, string verb) =>
        ["agent", "eval", verb, "--agent-id", session.CloudAgentId, "--environment-id", session.EnvironmentId];

    private static string[] TestSet(SessionAgent session, string verb) =>
        ["agent", "eval", "testset", verb, "--agent-id", session.CloudAgentId, "--environment-id", session.EnvironmentId];

    /// <summary>A test set name unique to this run and this scenario, so no two collide on the agent.</summary>
    private static string TestSetName(ScenarioContext context) =>
        $"ahe2e-{context.ScenarioId.ToLowerInvariant()}-{context.RunSuffix}";

    /// <summary>Writes a conversation CSV in the Copilot Studio template's format.</summary>
    private static string WriteCsv(ScenarioContext context, string fileName, params (int Number, string Question)[] rows) =>
        WriteLines(
            context,
            fileName,
            ["conversationNumber,question,response", .. rows.Select(row => $"{row.Number},\"{row.Question}\",\"\"")]);

    private static string WriteLines(ScenarioContext context, string fileName, IReadOnlyList<string> lines)
    {
        var path = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId), fileName);
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// Runs the CLI under the scenario's own bound, so a command still in flight when the scenario
    /// times out is cancelled rather than left to charge the next scenario's credits.
    /// </summary>
    private static Task<MsagentResult> RunAsync(ScenarioContext context, IReadOnlyList<string> arguments, TimeSpan? timeout = null) =>
        context.Msagent.RunAsync(
            arguments,
            context.Workspaces.EmptyDirectory(context.ScenarioId),
            timeout: timeout,
            cancellationToken: context.CancellationToken);
}
