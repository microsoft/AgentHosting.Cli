namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>A scenario, which stays data until the runner invokes <see cref="Run"/>.</summary>
public sealed record Scenario
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Feature { get; init; }

    public required IReadOnlyList<Capability> Requires { get; init; }

    public required SafetyTier Tier { get; init; }

    /// <summary>Whether the smoke preset includes it.</summary>
    public bool Smoke { get; init; }

    public string? Pack { get; init; }

    /// <summary>How long the scenario may run, when it needs longer than the runner's default.</summary>
    public TimeSpan? Timeout { get; init; }

    public required Func<ScenarioContext, Task> Run { get; init; }
}

/// <summary>Everything a scenario is given; it starts no process and reads no config of its own.</summary>
public sealed class ScenarioContext
{
    public required string ScenarioId { get; init; }

    public required MsagentCli Msagent { get; init; }

    public required E2EConfig Config { get; init; }

    public required Assertions Assert { get; init; }

    public required Ledger Ledger { get; init; }

    public required Workspaces Workspaces { get; init; }

    public required CloudProbe Cloud { get; init; }

    /// <summary>The shared pool of deployed agents, so most scenarios need not deploy their own.</summary>
    public required SessionResources Session { get; init; }

    public required Transcript Transcript { get; init; }

    /// <summary>The suffix distinguishing this run's resources from any other's.</summary>
    public required string RunSuffix { get; init; }

    /// <summary>The token bounding this scenario, cancelled when it exceeds the runner's timeout.</summary>
    public required CancellationToken CancellationToken { get; init; }

    public StandaloneServices? Standalone { get; init; }

    /// <summary>The dev environment, which every cloud scenario targets unless it says otherwise.</summary>
    public string DevEnvironmentId => Config.Mcs.DevEnvironmentId
        ?? throw new E2EException($"{ScenarioId} requires mcs.devEnvironmentId, which the capability check should have caught.");

    /// <summary>The test environment, for the promotion scenarios.</summary>
    public string TestEnvironmentId => Config.Mcs.TestEnvironmentId
        ?? throw new E2EException($"{ScenarioId} requires mcs.testEnvironmentId, which the capability check should have caught.");

    /// <summary>The Entra object id the sharing scenarios grant access to, which names no person.</summary>
    public string ShareUserObjectId => Config.Mcs.ShareUserObjectId
        ?? throw new E2EException($"{ScenarioId} requires mcs.shareUserObjectId, which the capability check should have caught.");

    /// <summary>A bot name unique to this run, used for every bot the run creates.</summary>
    public string BotName(string? label = null) =>
        label is { Length: > 0 } ? $"{Config.Azure.BotNamePrefix}-{RunSuffix}-{label}" : $"{Config.Azure.BotNamePrefix}-{RunSuffix}";
}
