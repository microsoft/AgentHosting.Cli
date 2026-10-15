namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

/// <summary>The usage wording shared by the verbs that route on agent type.</summary>
public static class UsageMessages
{
    /// <summary>The message for an option that belongs to the other agent type.</summary>
    /// <param name="option">The offending option name.</param>
    /// <param name="appliesTo">The agent type the option belongs to.</param>
    /// <param name="resolved">The agent type the command resolved.</param>
    public static string WrongAgentType(string option, string appliesTo, string resolved) =>
        $"{option} applies to a {appliesTo} deployment, and this command resolved a {resolved} deployment.";

    /// <summary>The System.CommandLine wording for options a type-specific verb requires.</summary>
    /// <param name="missing">The option names that were not supplied.</param>
    public static string Required(params string[] missing) =>
        string.Join(" ", missing.Select(option => $"Option '{option}' is required.")) + " Run `msagent --help` for usage.";

    /// <summary>Names every option the verb requires but did not receive, or null when all are present.</summary>
    /// <param name="options">Option names paired with the value supplied for each.</param>
    public static string? RequiredOptions(params (string Option, string? Value)[] options)
    {
        var missing = options.Where(entry => string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Option).ToArray();

        return missing.Length == 0 ? null : Required(missing);
    }

    /// <summary>Names the selector a verb needs when nothing in the project identified the agent.</summary>
    /// <param name="selector">The option that names the agent.</param>
    public static string AgentSelectorRequired(string selector) =>
        $"The project does not identify a single agent to act on. {Required(selector)}";

    /// <summary>The message for selectors that were supplied but name nothing the project holds.</summary>
    /// <param name="supplied">The selectors and values that matched nothing.</param>
    /// <param name="listCommand">The command that lists what the project does hold.</param>
    public static string NoMatchInProject(string supplied, string listCommand) =>
        $"Nothing in this project matches {supplied}. Run `{listCommand}` to see what it holds.";

    /// <summary>Reports the first supplied option from a set that does not apply.</summary>
    /// <param name="supplied">Option names paired with whether the caller supplied them.</param>
    public static string? FirstSupplied(IReadOnlyList<(string Option, bool Supplied)> supplied) =>
        supplied.FirstOrDefault(entry => entry.Supplied).Option;
}
