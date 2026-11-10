namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record AmsRepositoryUse(IReadOnlyList<string> Setups, int Own);

public static class AmsRepositories
{
    public static int Count(IReadOnlyDictionary<string, AmsRepositoryUse> uses, IEnumerable<string> scenarioIds)
    {
        var used = scenarioIds.Where(uses.ContainsKey).Select(scenarioId => uses[scenarioId]).ToArray();

        return used.SelectMany(use => use.Setups).Distinct(StringComparer.Ordinal).Count() + used.Sum(use => use.Own);
    }
}
