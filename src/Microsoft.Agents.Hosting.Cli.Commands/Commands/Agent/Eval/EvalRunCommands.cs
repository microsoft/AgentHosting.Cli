using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent.Eval;

/// <summary>The addressing options every evaluation verb accepts, and the conversation sources they share.</summary>
public static class EvaluationSelectorSymbols
{
    /// <summary>The selectors that address the agent being evaluated.</summary>
    public static IReadOnlyList<CommandSymbol> Agent =>
    [
        OptionCatalog.Value("--project", "Project directory to act on. Defaults to the current directory."),
        OptionCatalog.Value("--agent-id", "Agent to act on. Wins over --agent-name."),
        OptionCatalog.Value("--agent-name", "Agent to act on, by name."),
        OptionCatalog.Value("--environment-id", "Act in this environment instead of the agent's own."),
    ];

    /// <summary>The options that name where conversations come from.</summary>
    public static IReadOnlyList<CommandSymbol> ConversationSource =>
    [
        OptionCatalog.Value("--from-csv", "CSV of conversations, in the Copilot Studio template's format. Not with --generate."),
        OptionCatalog.Integer("--generate", "Conversations to generate from the agent's own description instead of reading a file."),
        OptionCatalog.Integer("--turns", "Turns per generated conversation. Defaults to 3."),
    ];

    /// <summary>The symbols a verb addressing one run accepts.</summary>
    /// <param name="runDescription">What the verb does to the run it names.</param>
    public static IReadOnlyList<CommandSymbol> Run(string runDescription) => [OptionCatalog.Value("--run-id", runDescription, required: true), .. Agent];

    /// <summary>Binds the shared selectors and the run a verb was pointed at.</summary>
    /// <param name="input">The parsed command line.</param>
    public static TRequest BindRun<TRequest>(ICommandInput input) where TRequest : EvaluationRunSelector, new()
    {
        ArgumentNullException.ThrowIfNull(input);

        return new TRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            RunId = input.GetString("--run-id"),
        };
    }
}

/// <summary><c>msagent agent eval run</c>.</summary>
[Command("run", Description = "Run a Copilot Studio evaluation. Uses Copilot Credits, and the cost is reported when the run finishes.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 10, ProgressLabel = "Running evaluation")]
public sealed class EvalRunCommand : CommandBase<EvaluationRunRequest, EvaluationRunResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--testset", "Existing test set to run, by name or id. Not with --from-csv or --generate."),
        .. EvaluationSelectorSymbols.ConversationSource,
        OptionCatalog.Value("--name", "Name for the test set created from --from-csv or --generate."),
        OptionCatalog.Switch("--update-testset", "Replace the conversations in a test set of the same name instead of failing."),
        OptionCatalog.Value("--connection", "Copilot Studio connection the agent runs as. Omit to evaluate unauthenticated."),
        OptionCatalog.Switch("--published", "Evaluate the published agent instead of the draft."),
        OptionCatalog.Switch("--watch", "Poll until the run finishes and print the results."),
        OptionCatalog.Integer("--timeout-minutes", "Minutes --watch polls before giving up. Defaults to 60."),
        OptionCatalog.Value("--output", "Write the finished run to this file as JSON. Requires --watch, and writes nothing if it times out."),
    ];

    /// <inheritdoc />
    public override IReadOnlyList<UsageRoute> UsageRoutes =>
    [
        new("--testset") { Summary = "Run an existing test set" },
        new("--from-csv", "--name") { Summary = "Run conversations read from a CSV" },
        new("--generate", "--name") { Summary = "Run conversations generated from the agent's description" },
    ];

    /// <inheritdoc />
    protected override EvaluationRunRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationRunRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            TestSet = input.GetString("--testset"),
            FromCsv = input.GetString("--from-csv"),
            Generate = input.GetInt("--generate"),
            Turns = input.GetInt("--turns"),
            Name = input.GetString("--name"),
            UpdateTestSet = input.GetSwitch("--update-testset"),
            Connection = input.GetString("--connection"),
            Published = input.GetSwitch("--published"),
            Watch = input.GetSwitch("--watch"),
            TimeoutMinutes = input.GetInt("--timeout-minutes"),
            Output = input.GetString("--output"),
        };
    }
}

/// <summary><c>msagent agent eval list</c>.</summary>
[Command("list", Description = "List an agent's evaluation runs, newest first.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 20)]
public sealed class EvalListCommand : CommandBase<EvaluationListRequest, EvaluationListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Integer("--limit", "Maximum rows to return. Defaults to 20."),
    ];

    /// <inheritdoc />
    protected override EvaluationListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationListRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Limit = input.GetInt("--limit"),
        };
    }
}

/// <summary><c>msagent agent eval show</c>.</summary>
[Command("show", Description = "Show one evaluation run and its per-conversation results.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 30)]
public sealed class EvalShowCommand : CommandBase<EvaluationShowRequest, EvaluationRunResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Run("The evaluation run to show."),
        OptionCatalog.Switch("--transcript", "Print every conversation's questions and answers below the results, shortened."),
        OptionCatalog.Integer("--conversation", "Print one conversation's questions and answers, by its position in the results."),
        OptionCatalog.Value("--output", "Write the full run to this file. A .json name writes JSON, any other name writes Markdown."),
    ];

    /// <inheritdoc />
    protected override EvaluationShowRequest Bind(ICommandInput input) => EvaluationSelectorSymbols.BindRun<EvaluationShowRequest>(input) with
    {
        Transcript = input.GetSwitch("--transcript"),
        Conversation = input.GetInt("--conversation"),
        Output = input.GetString("--output"),
    };
}

/// <summary><c>msagent agent eval cancel</c>.</summary>
[Command("cancel", Description = "Stop an evaluation run that is still queued or in progress.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 40)]
public sealed class EvalCancelCommand : CommandBase<EvaluationCancelRequest, EvaluationRunResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => EvaluationSelectorSymbols.Run("The evaluation run to stop.");

    /// <inheritdoc />
    protected override EvaluationCancelRequest Bind(ICommandInput input) => EvaluationSelectorSymbols.BindRun<EvaluationCancelRequest>(input);
}

/// <summary><c>msagent agent eval delete</c>.</summary>
[Command("delete", Description = "Delete a finished evaluation run. The test set it used is left alone.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 50)]
public sealed class EvalDeleteCommand : CommandBase<EvaluationDeleteRequest, EvaluationRunResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => EvaluationSelectorSymbols.Run("The evaluation run to delete.");

    /// <inheritdoc />
    protected override EvaluationDeleteRequest Bind(ICommandInput input) => EvaluationSelectorSymbols.BindRun<EvaluationDeleteRequest>(input);
}

/// <summary><c>msagent agent eval generate</c>.</summary>
[Command("generate", Description = "Generate evaluation conversations from the agent's description. Creates no test set. Use --output to keep them.", ParentGroup = typeof(AgentEvalGroup), SortPriority = 60, ProgressLabel = "Generating conversations")]
public sealed class EvalGenerateCommand : CommandBase<EvaluationGenerateRequest, EvaluationGenerateResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Integer("--count", "Conversations to generate. Defaults to 10."),
        OptionCatalog.Integer("--turns", "Turns per conversation. Defaults to 3."),
        OptionCatalog.Value("--output", "Write the conversations to this file as CSV. Omit to list them without keeping a copy."),
    ];

    /// <inheritdoc />
    protected override EvaluationGenerateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationGenerateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Count = input.GetInt("--count"),
            Turns = input.GetInt("--turns"),
            Output = input.GetString("--output"),
        };
    }
}
