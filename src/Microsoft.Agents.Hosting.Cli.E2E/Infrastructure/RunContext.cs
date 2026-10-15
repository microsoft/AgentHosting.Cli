namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Everything one run shares, assembled once and handed to each scenario.</summary>
public sealed class RunContext
{
    public required string RunId { get; init; }

    public required string RunSuffix { get; init; }

    public required string RunDirectory { get; init; }

    public required string WorkspaceRoot { get; init; }

    public required E2EConfig Config { get; init; }

    public required Capabilities Capabilities { get; init; }

    public required MsagentBinary Binary { get; init; }

    public required MsagentCli Msagent { get; init; }

    public required AzureCli Azure { get; init; }

    public required CloudProbe Cloud { get; init; }

    public required Ledger Ledger { get; init; }

    public required Workspaces Workspaces { get; init; }

    /// <summary>The run's shared agent pool.</summary>
    public required SessionResources Session { get; init; }

    public required Transcript Transcript { get; init; }

    public required Reporter Reporter { get; init; }

    /// <summary>Builds the context one scenario sees.</summary>
    public ScenarioContext ContextFor(Scenario scenario, Assertions assertions, CancellationToken cancellationToken) => new()
    {
        ScenarioId = scenario.Id,
        Msagent = Msagent,
        Config = Config,
        Assert = assertions,
        Ledger = Ledger,
        Workspaces = Workspaces,
        Cloud = Cloud,
        Session = Session,
        Transcript = Transcript,
        RunSuffix = RunSuffix,
        CancellationToken = cancellationToken,
    };
}
