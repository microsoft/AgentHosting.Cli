namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Builds scenarios for one feature, so each file declares its rows rather than its plumbing.</summary>
public sealed class ScenarioBuilder
{
    private readonly string _feature;

    public ScenarioBuilder(string feature) => _feature = feature;

    /// <summary>Declares one scenario.</summary>
    public Scenario Make(
        string id,
        string title,
        IReadOnlyList<Capability> requires,
        SafetyTier tier,
        Func<ScenarioContext, Task> run,
        bool smoke = false) => new()
        {
            Id = id,
            Title = title,
            Feature = _feature,
            Requires = requires,
            Tier = tier,
            Smoke = smoke,
            Run = run,
        };
}
