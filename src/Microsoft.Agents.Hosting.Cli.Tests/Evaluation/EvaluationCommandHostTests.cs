using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Evaluation;

public sealed class EvaluationCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Run_BindsEveryOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(
            operations,
            "agent",
            "eval",
            "run",
            "--project", "C:\\src\\agent",
            "--agent-id", "agent-9",
            "--agent-name", "Contoso",
            "--environment-id", "env-9",
            "--from-csv", "cases.csv",
            "--turns", "4",
            "--name", "Nightly",
            "--update-testset",
            "--connection", "conn-1",
            "--published",
            "--watch",
            "--timeout-minutes", "15",
            "--output", "run.json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        var request = operations.LastEvaluationRunRequest;
        Assert.Equal("C:\\src\\agent", request?.Project);
        Assert.Equal("agent-9", request?.AgentId);
        Assert.Equal("Contoso", request?.AgentName);
        Assert.Equal("env-9", request?.EnvironmentId);
        Assert.Equal("cases.csv", request?.FromCsv);
        Assert.Equal(4, request?.Turns);
        Assert.Equal("Nightly", request?.Name);
        Assert.True(request?.UpdateTestSet);
        Assert.Equal("conn-1", request?.Connection);
        Assert.True(request?.Published);
        Assert.True(request?.Watch);
        Assert.Equal(15, request?.TimeoutMinutes);
        Assert.Equal("run.json", request?.Output);
        Assert.Null(request?.TestSet);
        Assert.Null(request?.Generate);
    }

    [Fact]
    public void Run_BindsShortAliases()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "run", "-tset", "Nightly", "-w", "-tmo", "5", "-o", "run.json", "-pubd");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Nightly", operations.LastEvaluationRunRequest?.TestSet);
        Assert.True(operations.LastEvaluationRunRequest?.Watch);
        Assert.Equal(5, operations.LastEvaluationRunRequest?.TimeoutMinutes);
        Assert.Equal("run.json", operations.LastEvaluationRunRequest?.Output);
        Assert.True(operations.LastEvaluationRunRequest?.Published);
    }

    [Fact]
    public void Run_BindsGenerationAliases()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "run", "-gen", "12", "-trn", "2", "-uts", "--name", "Nightly");

        Assert.Equal(12, operations.LastEvaluationRunRequest?.Generate);
        Assert.Equal(2, operations.LastEvaluationRunRequest?.Turns);
        Assert.True(operations.LastEvaluationRunRequest?.UpdateTestSet);
    }

    [Fact]
    public void Run_LeavesOptionalSwitchesUnsetWhenAbsent()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "run", "--testset", "Nightly");

        Assert.False(operations.LastEvaluationRunRequest?.Watch);
        Assert.False(operations.LastEvaluationRunRequest?.Published);
        Assert.False(operations.LastEvaluationRunRequest?.UpdateTestSet);
        Assert.Null(operations.LastEvaluationRunRequest?.TimeoutMinutes);
        Assert.Null(operations.LastEvaluationRunRequest?.Output);
    }

    [Fact]
    public void Run_RejectsAnUnknownOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "eval", "run", "--min-score", "0.8");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--min-score", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastEvaluationRunRequest);
    }

    [Fact]
    public void Run_RejectsANonNumericTimeout()
    {
        var (exitCode, _, _) = Run(new FakeOperations(), "agent", "eval", "run", "--testset", "Nightly", "--timeout-minutes", "soon");

        Assert.Equal(ExitCodes.Usage, exitCode);
    }

    [Fact]
    public void Run_EmitsTheResultEnvelopeAsJson()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "eval", "run", "--testset", "Nightly", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("evaluation-started", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("env-1", root.GetProperty("environmentId").GetString());
        Assert.Equal("bot-1", root.GetProperty("mcsAgentId").GetString());
        Assert.Equal("run-1", root.GetProperty("run").GetProperty("runId").GetString());
    }

    [Fact]
    public void Run_RendersCreditsAfterTheRunFinishes()
    {
        var operations = new FakeOperations
        {
            OnEvaluationRun = _ => new EvaluationRunResult
            {
                Status = "evaluation-completed",
                EnvironmentId = "env-1",
                McsAgentId = "bot-1",
                Run = new EvaluationRunRow { RunId = "run-1", State = "Completed", TestCaseCount = 2, Score = 1, Succeeded = 2, FailedCount = 0, Credits = 13.58 },
            },
        };

        var (exitCode, stdout, _) = Run(operations, "agent", "eval", "run", "--testset", "Nightly", "--watch");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("13.58", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void List_BindsTheLimit()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "list", "--agent-id", "agent-9", "--limit", "5");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-9", operations.LastEvaluationListRequest?.AgentId);
        Assert.Equal(5, operations.LastEvaluationListRequest?.Limit);
    }

    [Fact]
    public void List_DefaultsTheLimitToUnset()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "list");

        Assert.Null(operations.LastEvaluationListRequest?.Limit);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_BindTheRunIdOption(string verb)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", verb, "--run-id", "run-9", "--agent-id", "agent-9");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("run-9", RunIdOf(operations, verb));
        Assert.Equal("agent-9", SelectorOf(operations, verb)?.AgentId);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_BindTheRunIdShortAlias(string verb)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", verb, "-rid", "run-9");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("run-9", RunIdOf(operations, verb));
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_RequireTheRunIdOption(string verb)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", verb, "--agent-id", "agent-9");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(SelectorOf(operations, verb));
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_RejectAPositionalRunId(string verb)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", verb, "run-9");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(SelectorOf(operations, verb));
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_BindEveryAgentSelector(string verb)
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", verb, "--run-id", "run-9", "--project", "C:\\src\\agent", "--agent-id", "agent-9", "--agent-name", "Contoso", "--environment-id", "env-9");

        var selector = SelectorOf(operations, verb);
        Assert.Equal("C:\\src\\agent", selector?.Project);
        Assert.Equal("agent-9", selector?.AgentId);
        Assert.Equal("Contoso", selector?.AgentName);
        Assert.Equal("env-9", selector?.EnvironmentId);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public void RunVerbs_AdvertiseTheRunIdOptionInHelp(string verb)
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "eval", verb, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--run-id", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("<run-id>", stdout.Split('\n')[0], StringComparison.Ordinal);
    }

    private static EvaluationRunSelector? SelectorOf(FakeOperations operations, string verb) => verb switch
    {
        "show" => operations.LastEvaluationShowRequest,
        "cancel" => operations.LastEvaluationCancelRequest,
        _ => operations.LastEvaluationDeleteRequest,
    };

    private static string? RunIdOf(FakeOperations operations, string verb) => SelectorOf(operations, verb)?.RunId;

    [Fact]
    public void Show_BindsTheTranscriptOptions()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "show", "--run-id", "run-9", "--transcript", "--conversation", "2", "--output", "run.md");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(operations.LastEvaluationShowRequest?.Transcript);
        Assert.Equal(2, operations.LastEvaluationShowRequest?.Conversation);
        Assert.Equal("run.md", operations.LastEvaluationShowRequest?.Output);
    }

    [Fact]
    public void Show_BindsTheTranscriptShortAliases()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "show", "-rid", "run-9", "-tr", "-conv", "3", "-o", "run.json");

        Assert.True(operations.LastEvaluationShowRequest?.Transcript);
        Assert.Equal(3, operations.LastEvaluationShowRequest?.Conversation);
        Assert.Equal("run.json", operations.LastEvaluationShowRequest?.Output);
    }

    [Fact]
    public void Show_LeavesTheTranscriptOptionsUnsetWhenAbsent()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "show", "--run-id", "run-9");

        Assert.False(operations.LastEvaluationShowRequest?.Transcript);
        Assert.Null(operations.LastEvaluationShowRequest?.Conversation);
        Assert.Null(operations.LastEvaluationShowRequest?.Output);
    }

    [Fact]
    public void Show_RejectsANonNumericConversation()
    {
        var (exitCode, _, _) = Run(new FakeOperations(), "agent", "eval", "show", "--run-id", "run-9", "--conversation", "first");

        Assert.Equal(ExitCodes.Usage, exitCode);
    }

    [Fact]
    public void Generate_BindsEveryOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "generate", "--count", "20", "--turns", "5", "--output", "out.csv");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(20, operations.LastEvaluationGenerateRequest?.Count);
        Assert.Equal(5, operations.LastEvaluationGenerateRequest?.Turns);
        Assert.Equal("out.csv", operations.LastEvaluationGenerateRequest?.Output);
    }

    [Fact]
    public void Generate_BindsShortAliases()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "generate", "-cnt", "20", "-trn", "5", "-o", "out.csv");

        Assert.Equal(20, operations.LastEvaluationGenerateRequest?.Count);
        Assert.Equal(5, operations.LastEvaluationGenerateRequest?.Turns);
        Assert.Equal("out.csv", operations.LastEvaluationGenerateRequest?.Output);
    }

    [Fact]
    public void TestSetList_ReachesTheOperation()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "list", "--environment-id", "env-9");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("env-9", operations.LastEvaluationTestSetListRequest?.EnvironmentId);
    }

    [Fact]
    public void TestSetShow_RequiresTheTestSet()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "show");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastEvaluationTestSetShowRequest);
    }

    [Fact]
    public void TestSetShow_BindsTheTestSet()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "show", "--testset", "Nightly");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Nightly", operations.LastEvaluationTestSetShowRequest?.TestSet);
    }

    [Fact]
    public void TestSetCreate_BindsEveryOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "create", "--name", "Nightly", "--from-csv", "cases.csv");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Nightly", operations.LastEvaluationTestSetCreateRequest?.Name);
        Assert.Equal("cases.csv", operations.LastEvaluationTestSetCreateRequest?.FromCsv);
    }

    [Fact]
    public void TestSetCreate_RequiresTheName()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "create", "--from-csv", "cases.csv");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastEvaluationTestSetCreateRequest);
    }

    [Fact]
    public void TestSetUpdate_BindsReplace()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "update", "-tset", "Nightly", "-csv", "cases.csv", "-rep");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Nightly", operations.LastEvaluationTestSetUpdateRequest?.TestSet);
        Assert.Equal("cases.csv", operations.LastEvaluationTestSetUpdateRequest?.FromCsv);
        Assert.True(operations.LastEvaluationTestSetUpdateRequest?.Replace);
    }

    [Fact]
    public void TestSetUpdate_LeavesReplaceOffWhenAbsent()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "testset", "update", "--testset", "Nightly", "--generate", "3");

        Assert.False(operations.LastEvaluationTestSetUpdateRequest?.Replace);
        Assert.Equal(3, operations.LastEvaluationTestSetUpdateRequest?.Generate);
    }

    [Fact]
    public void TestSetExport_BindsTheOutput()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "eval", "testset", "export", "--testset", "Nightly", "--output", "out.csv", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("out.csv", operations.LastEvaluationTestSetExportRequest?.Output);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("out.csv", document.RootElement.GetProperty("outputPath").GetString());
    }

    [Fact]
    public void TestSetExport_RequiresTheOutput()
    {
        var (exitCode, _, _) = Run(new FakeOperations(), "agent", "eval", "testset", "export", "--testset", "Nightly");

        Assert.Equal(ExitCodes.Usage, exitCode);
    }

    [Fact]
    public void TestSetDelete_BindsForce()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "eval", "testset", "delete", "--testset", "Nightly", "--force");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Nightly", operations.LastEvaluationTestSetDeleteRequest?.TestSet);
        Assert.True(operations.LastEvaluationTestSetDeleteRequest?.Force);
    }

    [Fact]
    public void TestSetDelete_PassesNonInteractiveThrough()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "eval", "testset", "delete", "--testset", "Nightly", "--non-interactive");

        Assert.True(operations.LastEvaluationTestSetDeleteRequest?.NonInteractive);
        Assert.False(operations.LastEvaluationTestSetDeleteRequest?.Force);
    }

    [Fact]
    public void TestSetDelete_EmitsTheDeleteEnvelopeAsJson()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "eval", "testset", "delete", "--testset", "Nightly", "--force", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("test-set-deleted", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("testset-1", document.RootElement.GetProperty("testSetId").GetString());
    }

    [Fact]
    public void EvalGroupListsEveryVerb()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "eval", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);

        foreach (var verb in new[] { "run", "list", "show", "cancel", "delete", "generate", "testset" })
        {
            Assert.Contains(verb, stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TestSetGroupListsEveryVerb()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "eval", "testset", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);

        foreach (var verb in new[] { "list", "show", "create", "update", "export", "delete" })
        {
            Assert.Contains(verb, stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunHelpWarnsThatCreditsAreConsumed()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "agent", "eval", "run", "--help");

        Assert.Contains("Copilot Credits", stdout, StringComparison.Ordinal);
    }
}
