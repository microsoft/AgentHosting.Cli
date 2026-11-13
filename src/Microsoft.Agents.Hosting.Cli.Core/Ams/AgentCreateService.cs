using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>The <c>msagent agent create</c> application service.</summary>
public sealed class AgentCreateService
{
    private const int CodeAgentType = 0;
    private const string Origin = "AgentHostCLI";

    private readonly AgentCreationTarget _target;
    private readonly EnvironmentSelectionService _environments;
    private readonly IAmsAgentCreationTransport _transport;

    public AgentCreateService(
        AgentCreationTarget target,
        EnvironmentSelectionService environments,
        IAmsAgentCreationTransport transport)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<AgentCreateResult> CreateAsync(
        AgentCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var displayName = Required(request.DisplayName, "--agent-name");
        var language = ParseLanguage(request.Language);
        var languageVersion = Required(request.LanguageVersion, "--language-version");
        var generatedIdempotencyKey = request.IdempotencyKey is null;
        var idempotencyKey = ResolveIdempotencyKey(request.IdempotencyKey);

        var environment = await _environments
            .ResolveForCommandAsync(request.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        var target = _target.Resolve(environment.ApiEndpoint);

        AmsAgentCreationResult created;
        try
        {
            created = await _transport.CreateAsync(
                new AmsAgentCreationTransportRequest
                {
                    Endpoint = target.Endpoint,
                    Context = environment.Context,
                    RouteMode = target.RouteMode,
                    Audience = environment.ApiAudience,
                    TenantId = environment.TenantId,
                    EnvironmentId = environment.EnvironmentId,
                    IdempotencyKey = idempotencyKey,
                    DisplayName = displayName,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
                    AgentType = CodeAgentType,
                    Language = language,
                    LanguageVersion = languageVersion,
                    Origin = Origin,
                },
                cancellationToken).ConfigureAwait(false);

            RequireMatch("tenantId", environment.TenantId, created.TenantId, StringComparison.OrdinalIgnoreCase);
            RequireMatch("environmentId", environment.EnvironmentId, created.EnvironmentId, StringComparison.OrdinalIgnoreCase);
            RequireMatch("displayName", displayName, created.DisplayName, StringComparison.Ordinal);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw AgentCreationException.Canceled(idempotencyKey, ex);
        }
        catch (HttpRequestException ex)
        {
            throw AgentCreationException.TransportFailed(idempotencyKey, ex);
        }
        catch (AgentHostingException ex) when (
            generatedIdempotencyKey &&
            !ContainsIdempotencyKey(ex, idempotencyKey))
        {
            throw AgentCreationException.PreserveGeneratedIdempotencyKey(ex, idempotencyKey);
        }

        return new AgentCreateResult
        {
            Status = "agent-created",
            TenantId = created.TenantId,
            EnvironmentId = created.EnvironmentId,
            AgentId = created.AgentId,
            DisplayName = created.DisplayName,
            RepositoryId = created.RepositoryId,
        };
    }

    private static string Required(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"A value is required for `msagent agent create {option} <value>`.");
        }

        return value.Trim();
    }

    private static int ParseLanguage(string? value) =>
        Required(value, "--language").ToLowerInvariant() switch
        {
            "dotnet" => 1,
            "python" => 2,
            "node" => 3,
            _ => throw new UsageException(
                "`msagent agent create --language` must be `dotnet`, `python`, or `node`."),
        };

    private static string ResolveIdempotencyKey(string? value)
    {
        if (value is null)
        {
            return Guid.NewGuid().ToString();
        }

        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new UsageException(
                "`msagent agent create --idempotency-key` must contain no more than 128 visible ASCII characters.");
        }

        return value;
    }

    private static bool ContainsIdempotencyKey(AgentHostingException error, string idempotencyKey) =>
        error.Remediation?.Contains(idempotencyKey, StringComparison.Ordinal) == true;

    private static void RequireMatch(
        string field,
        string requestedValue,
        string returnedValue,
        StringComparison comparison)
    {
        if (!string.Equals(requestedValue, returnedValue, comparison))
        {
            throw AgentCreationException.ResponseMismatch(field, requestedValue, returnedValue);
        }
    }
}
