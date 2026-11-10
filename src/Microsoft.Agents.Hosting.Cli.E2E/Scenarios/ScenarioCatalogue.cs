using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Azure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>Every scenario in the suite, and the filters that select a subset of them.</summary>
public static class ScenarioCatalogue
{
    /// <summary>The features a run can be scoped to.</summary>
    public static IReadOnlyList<string> Features => ["mcs-agent", "mcs-workflow", StandaloneWorkflowScenarios.Feature, "code-agent", "abs", "identity", "mcs-eval", AmsMcsServices.Feature];

    /// <summary>The features whose scenarios create Azure resources.</summary>
    public static IReadOnlyList<string> AzurePack => ["code-agent", "abs", "identity"];

    /// <summary>The features whose scenarios spend Copilot Credits and wait on the evaluation service.</summary>
    public static IReadOnlyList<string> EvalPack => ["mcs-eval"];

    public static IReadOnlyList<string> StandaloneWorkflowPack => [StandaloneWorkflowScenarios.Feature];

    public static IReadOnlyList<string> AmsMcsPack => [AmsMcsServices.Feature];

    /// <summary>The packs a run opts into by name; a feature no pack names belongs to core.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> OptionalPacks { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["azure"] = AzurePack,
            ["eval"] = EvalPack,
            ["swf"] = StandaloneWorkflowPack,
            [StandaloneWorkflowScenarios.ChaosPack] = [],
            [AmsMcsServices.Pack] = AmsMcsPack,
        };

    /// <summary>All scenarios, in the order a full run executes them.</summary>
    public static IReadOnlyList<Scenario> All() =>
    [
        .. McsAgentScenarios.All(),
        .. McsWorkflowScenarios.All(),
        .. StandaloneWorkflowScenarios.All(),
        .. CodeAgentScenarios.All(),
        .. AbsScenarios.All(),
        .. IdentityScenarios.All(),
        .. McsEvalScenarios.All(),
        .. AmsMcsScenarios.All(),
    ];

    /// <summary>Applies the pack, feature, scenario, and smoke filters in that order.</summary>
    public static IReadOnlyList<Scenario> Select(RunFilters filters) => Select(All(), filters);

    public static IReadOnlyList<Scenario> Select(IEnumerable<Scenario> candidates, RunFilters filters)
    {
        var scenarios = candidates;

        foreach (var pack in OptionalPacks.Where(candidate => !filters.Packs.Contains(candidate.Key, StringComparer.OrdinalIgnoreCase)))
        {
            var excluded = pack.Value;
            scenarios = scenarios.Where(scenario => !excluded.Contains(scenario.Feature, StringComparer.OrdinalIgnoreCase));
        }

        scenarios = scenarios.Where(scenario => scenario.Pack is null || filters.Packs.Contains(scenario.Pack, StringComparer.OrdinalIgnoreCase));

        if (filters.Packs.Contains(AmsMcsServices.Pack, StringComparer.OrdinalIgnoreCase))
        {
            scenarios = scenarios.Where(scenario => AmsMcsPack.Contains(scenario.Feature, StringComparer.OrdinalIgnoreCase));
        }

        if (filters.Features.Count > 0)
        {
            scenarios = scenarios.Where(scenario => filters.Features.Contains(scenario.Feature, StringComparer.OrdinalIgnoreCase));
        }

        if (filters.Only.Count > 0)
        {
            scenarios = scenarios.Where(scenario => filters.Only.Any(pattern => MatchesPattern(scenario.Id, pattern)));
        }

        if (filters.Smoke)
        {
            scenarios = scenarios.Where(scenario => scenario.Smoke);
        }

        return [.. scenarios];
    }

    private static bool MatchesPattern(string id, string pattern) =>
        pattern.EndsWith('*')
            ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(id, pattern, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The subset of scenarios a run or plan addresses.</summary>
public sealed record RunFilters
{
    public IReadOnlyList<string> Packs { get; init; } = ["core"];

    public IReadOnlyList<string> Features { get; init; } = [];

    public IReadOnlyList<string> Only { get; init; } = [];

    public bool Smoke { get; init; }
}
