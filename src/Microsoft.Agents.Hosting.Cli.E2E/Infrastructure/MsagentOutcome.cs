namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record MsagentRepositoryOutcome(string? RepositoryId, string? GitUrl, bool RepositoryCreated);

public sealed record MsagentOutcome
{
    public bool Success { get; init; }

    public string? Status { get; init; }

    public string? AgentId { get; init; }

    public string? DisplayName { get; init; }

    public string? SchemaName { get; init; }

    public int? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? Remediation { get; init; }

    public MsagentRepositoryOutcome? Ams { get; init; }

    public string FailureText => $"{ErrorMessage} {Remediation}";

    public static MsagentOutcome Read(MsagentResult result)
    {
        try
        {
            return result.Json<MsagentOutcome>();
        }
        catch (E2EException)
        {
            return new() { ErrorMessage = result.Output.Trim() };
        }
    }
}
