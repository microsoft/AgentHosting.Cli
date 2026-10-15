namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>
/// One environment row in <c>msagent env list</c> output. Non-secret by construction: identifiers, a
/// display name, and the trusted endpoint — never a token or an audience-scoped credential.
/// </summary>
public sealed record EnvironmentSummary
{
    /// <summary>The environment id passed to <c>msagent env select</c>.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The environment's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The tenant that owns the environment.</summary>
    public required string TenantId { get; init; }

    /// <summary>The trusted Power Platform API endpoint for the environment.</summary>
    public required string ApiEndpoint { get; init; }

    /// <summary>Whether this environment is the persisted active selection.</summary>
    public bool IsActive { get; init; }
}

/// <summary>Result of <c>msagent env list</c>.</summary>
public sealed record EnvironmentListResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>listed</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The environments available to the signed-in operator.</summary>
    public required IReadOnlyList<EnvironmentSummary> Environments { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines()
    {
        if (Environments.Count == 0)
        {
            return ["No environments are available to the signed-in account."];
        }

        var lines = new List<string> { $"{Environments.Count} environment(s) available." };
        lines.AddRange(TextGrid.RenderTable(
            "Environments",
            ["ACTIVE", "ENVIRONMENT ID", "DISPLAY NAME", "TENANT"],
            [.. Environments.Select(environment => new[]
            {
                environment.IsActive ? "*" : " ",
                environment.EnvironmentId,
                environment.DisplayName,
                environment.TenantId,
            })]));

        return lines;
    }
}

/// <summary>Result of <c>msagent env select</c>. Contains no token material.</summary>
public sealed record EnvironmentSelectionResult : ITextGridRenderable
{
    /// <summary>Output-envelope schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Machine-readable status string (<c>selected</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The now-active environment id.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The now-active environment's display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The now-active environment's tenant.</summary>
    public required string TenantId { get; init; }

    /// <summary>The trusted Power Platform API endpoint for the environment.</summary>
    public required string ApiEndpoint { get; init; }

    /// <summary>When the selection was made.</summary>
    public DateTimeOffset SelectedAt { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> ToTextLines() => [$"Active environment: {DisplayName} ({EnvironmentId}) in tenant {TenantId}."];
}
