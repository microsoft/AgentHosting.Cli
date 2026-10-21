using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Identity;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Resolves tenant users through Microsoft Graph with the CLI's delegated <c>User.ReadBasic.All</c> grant.</summary>
public sealed class GraphTenantUserDirectoryAdapter : ITenantUserDirectoryPort
{
    private const string UserProjection = "$select=id,displayName,userPrincipalName";

    private const int AmbiguousMatchCount = 2;

    private const int ObjectIdsPerRequest = 15;

    private static readonly IReadOnlyList<string> GraphScopes = [$"{IdentityProvisioningConfig.GraphEndpoint}/.default"];

    private readonly IdentityGraphClient _graph;

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">Sends Microsoft Graph requests as the signed-in account.</param>
    public GraphTenantUserDirectoryAdapter(IIdentityRestClient client) => _graph = new IdentityGraphClient(client ?? throw new ArgumentNullException(nameof(client)));

    /// <inheritdoc />
    public async Task<DirectoryPrincipal?> FindUserAsync(string tenantId, string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var trimmed = address.Trim();

        try
        {
            return await FindByPrincipalNameAsync(tenantId, trimmed, cancellationToken).ConfigureAwait(false) ?? await FindByMailAsync(tenantId, trimmed, cancellationToken).ConfigureAwait(false);
        }
        catch (TargetRequestException failure) when (failure.ExitCode == ExitCodes.Failure)
        {
            throw ManagedAgentException.SharePrincipalLookupFailed(trimmed, failure.Message, failure);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<DirectoryPrincipal> ResolveManyAsync(string tenantId, IReadOnlyCollection<string> objectIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(objectIds);

        return objectIds.Select(objectId => Guid.TryParse(objectId, out var parsed) ? parsed.ToString() : null).OfType<string>().Distinct().Chunk(ObjectIdsPerRequest).ToAsyncEnumerable().SelectMany(batch => _graph.EnumerateCollectionAsync(UsersMatching($"id in ({string.Join(',', batch.Select(ODataLiteral.Quote))})"), GraphScopes, tenantId, cancellationToken)).Select(ReadUser).OfType<DirectoryPrincipal>();
    }

    private async Task<DirectoryPrincipal?> FindByPrincipalNameAsync(string tenantId, string address, CancellationToken cancellationToken)
    {
        try
        {
            return await _graph.TryGetAsync($"/v1.0/users({Uri.EscapeDataString(ODataLiteral.Quote(address))})?{UserProjection}", GraphScopes, tenantId, cancellationToken).ConfigureAwait(false) is { } response ? RequireUser(BlueprintIdentityJson.ParseObject(response.Body), address) : null;
        }
        catch (TargetRequestException failure) when (failure.Status == (int)HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    private async Task<DirectoryPrincipal?> FindByMailAsync(string tenantId, string address, CancellationToken cancellationToken) => await _graph.EnumerateCollectionAsync($"{UsersMatching($"mail eq {ODataLiteral.Quote(address)}")}&$top={AmbiguousMatchCount}", GraphScopes, tenantId, cancellationToken).Select(user => RequireUser(user, address)).DistinctBy(user => user.ObjectId).Take(AmbiguousMatchCount).ToArrayAsync(cancellationToken).ConfigureAwait(false) switch
    {
        [] => null,
        [var single] => single,
        var matches => throw ManagedAgentException.SharePrincipalAmbiguous(address, tenantId, matches.Select(match => match.SignInAddress).OfType<string>()),
    };

    private static string UsersMatching(string filter) => $"/v1.0/users?$filter={Uri.EscapeDataString(filter)}&{UserProjection}";

    private static DirectoryPrincipal RequireUser(JsonObject user, string address) => ReadUser(user) ?? throw ManagedAgentException.SharePrincipalLookupFailed(address, "it returned a user without a usable object id or sign-in address.");

    private static DirectoryPrincipal? ReadUser(JsonObject user) => Guid.TryParse(BlueprintIdentityJson.ReadString(user, "id"), out var objectId) && ReadText(user, "userPrincipalName") is { } userPrincipalName ? new DirectoryPrincipal { ObjectId = objectId.ToString(), DisplayName = ReadText(user, "displayName") ?? userPrincipalName, PrincipalType = "User", SignInAddress = userPrincipalName } : null;

    private static string? ReadText(JsonObject user, string name) => BlueprintIdentityJson.ReadString(user, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
