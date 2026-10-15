using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>Resolves a test set from the name or id a command supplied, always reading from the service.</summary>
public sealed class EvaluationTestSetLookup
{
    private readonly IAgentEvaluationPort _evaluations;

    /// <summary>Creates the lookup.</summary>
    /// <param name="evaluations">Reads the agent's test sets.</param>
    public EvaluationTestSetLookup(IAgentEvaluationPort evaluations) => _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

    /// <summary>Finds the one test set matching a name or id, or null when none does.</summary>
    /// <param name="target">The agent being addressed.</param>
    /// <param name="selector">A test set name or component id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<EvaluationTestSetSummary?> FindAsync(EvaluationTarget target, string selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        return Match(await _evaluations.ListTestSetsAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false), selector);
    }

    /// <summary>
    /// Finds the one test set carrying a name, ignoring ids entirely.
    ///
    /// <para>This is what a verb naming the set it is about to create needs: a name that happens to
    /// read like another set's id must create the named set rather than address the set that id
    /// belongs to.</para>
    /// </summary>
    /// <param name="target">The agent being addressed.</param>
    /// <param name="name">The name to look for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<EvaluationTestSetSummary?> FindByNameAsync(EvaluationTarget target, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return MatchByName(await _evaluations.ListTestSetsAsync(target.Environment, target.McsAgentId, cancellationToken).ConfigureAwait(false), name.Trim());
    }

    /// <summary>Finds the one test set matching a name or id, failing when none or several do.</summary>
    /// <param name="target">The agent being addressed.</param>
    /// <param name="selector">A test set name or component id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<EvaluationTestSetSummary> RequireAsync(EvaluationTarget target, string selector, CancellationToken cancellationToken) =>
        await FindAsync(target, selector, cancellationToken).ConfigureAwait(false) ?? throw EvaluationException.TestSetNotFound(selector);

    /// <summary>The one test set a selector names, failing when the name is ambiguous.</summary>
    /// <param name="testSets">Every test set the agent holds.</param>
    /// <param name="selector">A test set name or component id.</param>
    public static EvaluationTestSetSummary? Match(IReadOnlyList<EvaluationTestSetSummary> testSets, string selector)
    {
        ArgumentNullException.ThrowIfNull(testSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var trimmed = selector.Trim();

        return testSets.FirstOrDefault(testSet => string.Equals(testSet.TestSetId, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? MatchByName(testSets, trimmed);
    }

    private static EvaluationTestSetSummary? MatchByName(IReadOnlyList<EvaluationTestSetSummary> testSets, string name)
    {
        var matches = testSets.Where(testSet => string.Equals(testSet.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToArray();

        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw EvaluationException.TestSetAmbiguous(name, [.. matches.Select(testSet => testSet.TestSetId)]),
        };
    }
}
