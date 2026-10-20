using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Resolves directory principals from the environment's own Dataverse tables.
/// </summary>
public sealed class DataversePrincipalAdapter : IDirectoryPrincipalPort
{
    private const string DataRoute = "api/data/v9.2";

    private const int EntraGroupTeamType = 2;

    private const int LookupBatchSize = 20;

    private readonly DataverseWebApiClient _client;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The shared Dataverse Web API client.</param>
    public DataversePrincipalAdapter(DataverseWebApiClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public async Task<DirectoryPrincipal?> ResolveUserAsync(CopilotEnvironmentContext environment, string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var trimmed = selector.Trim();
        var matches = (await ReadUsersAsync(environment, Guid.TryParse(trimmed, out var objectId) ? ObjectIdFilter([objectId.ToString()]) : SignInAddressFilter(ODataLiteral.Quote(trimmed)), cancellationToken).ConfigureAwait(false)).DistinctBy(user => user.ObjectId, StringComparer.OrdinalIgnoreCase).ToArray();

        return matches switch
        {
            [] => null,
            [var single] => single,
            _ => throw CopilotStudioException.SharePrincipalAmbiguous(trimmed, matches.Select(match => match.DisplayName)),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DirectoryPrincipal>> ResolveManyAsync(CopilotEnvironmentContext environment, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectIds);

        var resolved = new Dictionary<string, DirectoryPrincipal>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in objectIds.Where(objectId => Guid.TryParse(objectId, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(LookupBatchSize))
        {
            foreach (var principal in await ReadUsersAsync(environment, ObjectIdFilter(batch), cancellationToken).ConfigureAwait(false))
            {
                resolved[principal.ObjectId] = principal;
            }

            var unresolved = batch.Where(objectId => !resolved.ContainsKey(objectId)).ToArray();

            if (unresolved.Length == 0)
            {
                continue;
            }

            foreach (var principal in await ReadGroupsAsync(environment, ObjectIdFilter(unresolved), cancellationToken).ConfigureAwait(false))
            {
                resolved[principal.ObjectId] = principal;
            }
        }

        return resolved;
    }

    private static string ObjectIdFilter(IReadOnlyCollection<string> objectIds) =>
        string.Join(" or ", objectIds.Select(objectId => $"azureactivedirectoryobjectid eq {objectId}"));

    private static string SignInAddressFilter(string literal) => $"internalemailaddress eq {literal} or domainname eq {literal}";

    private async Task<IReadOnlyList<DirectoryPrincipal>> ReadUsersAsync(CopilotEnvironmentContext environment, string filter, CancellationToken cancellationToken) =>
        [.. (await _client.ReadCollectionAsync(
            environment,
            $"{DataRoute}/systemusers?$select=azureactivedirectoryobjectid,fullname,domainname&{DataverseWebApiClient.ToFilterQuery($"({filter}) and isdisabled eq false")}",
            element => ToPrincipal(element, "User", "fullname", "domainname"),
            cancellationToken).ConfigureAwait(false)).OfType<DirectoryPrincipal>()];

    private async Task<IReadOnlyList<DirectoryPrincipal>> ReadGroupsAsync(CopilotEnvironmentContext environment, string filter, CancellationToken cancellationToken) =>
        [.. (await _client.ReadCollectionAsync(
            environment,
            $"{DataRoute}/teams?$select=azureactivedirectoryobjectid,name&{DataverseWebApiClient.ToFilterQuery($"({filter}) and teamtype eq {EntraGroupTeamType}")}",
            element => ToPrincipal(element, "Group", "name", null),
            cancellationToken).ConfigureAwait(false)).OfType<DirectoryPrincipal>()];

    /// <summary>
    /// Projects one row, keyed by its Entra object id. A row without one names a principal sharing cannot address, so it is skipped.
    /// </summary>
    private static DirectoryPrincipal? ToPrincipal(JsonElement element, string principalType, string nameColumn, string? signInColumn)
    {
        if (DataverseWebApiClient.ReadString(element, "azureactivedirectoryobjectid") is not { } objectId)
        {
            return null;
        }

        return new DirectoryPrincipal
        {
            ObjectId = objectId,
            DisplayName = DataverseWebApiClient.ReadString(element, nameColumn) ?? objectId,
            PrincipalType = principalType,
            SignInAddress = signInColumn is null ? null : DataverseWebApiClient.ReadString(element, signInColumn),
        };
    }
}
