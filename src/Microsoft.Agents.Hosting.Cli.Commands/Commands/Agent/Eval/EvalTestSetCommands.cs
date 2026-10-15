using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent.Eval;

/// <summary><c>msagent agent eval testset list</c>.</summary>
[Command("list", Description = "List the evaluation test sets an agent holds.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 10)]
public sealed class EvalTestSetListCommand : CommandBase<EvaluationTestSetListRequest, EvaluationTestSetListResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => EvaluationSelectorSymbols.Agent;

    /// <inheritdoc />
    protected override EvaluationTestSetListRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetListRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
        };
    }
}

/// <summary><c>msagent agent eval testset show</c>.</summary>
[Command("show", Description = "Show one evaluation test set and the conversations it holds.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 20)]
public sealed class EvalTestSetShowCommand : CommandBase<EvaluationTestSetShowRequest, EvaluationTestSetResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--testset", "Test set to show, by name or id.", required: true),
    ];

    /// <inheritdoc />
    protected override EvaluationTestSetShowRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetShowRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            TestSet = input.GetString("--testset"),
        };
    }
}

/// <summary><c>msagent agent eval testset create</c>.</summary>
[Command("create", Description = "Create an evaluation test set from a CSV or from generated conversations.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 30)]
public sealed class EvalTestSetCreateCommand : CommandBase<EvaluationTestSetCreateRequest, EvaluationTestSetResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--name", "Name to give the test set.", required: true),
        .. EvaluationSelectorSymbols.ConversationSource,
    ];

    /// <inheritdoc />
    protected override EvaluationTestSetCreateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetCreateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            Name = input.GetString("--name"),
            FromCsv = input.GetString("--from-csv"),
            Generate = input.GetInt("--generate"),
            Turns = input.GetInt("--turns"),
        };
    }
}

/// <summary><c>msagent agent eval testset update</c>.</summary>
[Command("update", Description = "Add conversations to an evaluation test set, or replace the ones it holds.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 40)]
public sealed class EvalTestSetUpdateCommand : CommandBase<EvaluationTestSetUpdateRequest, EvaluationTestSetResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--testset", "Test set to change, by name or id.", required: true),
        .. EvaluationSelectorSymbols.ConversationSource,
        OptionCatalog.Switch("--replace", "Leave only the new conversations, discarding the ones the set already held."),
    ];

    /// <inheritdoc />
    protected override EvaluationTestSetUpdateRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetUpdateRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            TestSet = input.GetString("--testset"),
            FromCsv = input.GetString("--from-csv"),
            Generate = input.GetInt("--generate"),
            Turns = input.GetInt("--turns"),
            Replace = input.GetSwitch("--replace"),
        };
    }
}

/// <summary><c>msagent agent eval testset export</c>.</summary>
[Command("export", Description = "Write an evaluation test set's conversations out as CSV.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 50)]
public sealed class EvalTestSetExportCommand : CommandBase<EvaluationTestSetExportRequest, EvaluationTestSetResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--testset", "Test set to export, by name or id.", required: true),
        OptionCatalog.Value("--output", "CSV file to write.", required: true),
    ];

    /// <inheritdoc />
    protected override EvaluationTestSetExportRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetExportRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            TestSet = input.GetString("--testset"),
            Output = input.GetString("--output"),
        };
    }
}

/// <summary><c>msagent agent eval testset delete</c>.</summary>
[Command("delete", Description = "Delete an evaluation test set and the conversations it holds. Runs that used it are unaffected.", ParentGroup = typeof(AgentEvalTestSetGroup), SortPriority = 60)]
public sealed class EvalTestSetDeleteCommand : CommandBase<EvaluationTestSetDeleteRequest, EvaluationTestSetDeleteResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        .. EvaluationSelectorSymbols.Agent,
        OptionCatalog.Value("--testset", "Test set to delete, by name or id.", required: true),
        OptionCatalog.Switch("--force", "Delete without asking for confirmation."),
    ];

    /// <inheritdoc />
    protected override EvaluationTestSetDeleteRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new EvaluationTestSetDeleteRequest
        {
            Project = input.GetString("--project"),
            AgentId = input.GetString("--agent-id"),
            AgentName = input.GetString("--agent-name"),
            EnvironmentId = input.GetString("--environment-id"),
            TestSet = input.GetString("--testset"),
            Force = input.GetSwitch("--force"),
            NonInteractive = input.NonInteractive,
        };
    }
}
