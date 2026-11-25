using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads every page of an AMS list route, asking for each next page on the route itself.</summary>
internal static class AmsPaging
{
    private const int MaxPages = 1000;

    /// <summary>Sends the call of each page with the skip token of the previous page's next link and collects the items of every page.</summary>
    internal static async Task<IReadOnlyList<TItem>> ReadAllAsync<TItem>(AmsRequestSender sender, Func<string?, AmsCall> pageCall, Func<JsonElement, TItem> readItem, Action<JsonElement>? readFirstPage, CancellationToken cancellationToken)
    {
        var items = new List<TItem>();
        var skipTokens = new HashSet<string>(StringComparer.Ordinal);
        string? skipToken = null;
        do
        {
            var call = pageCall(skipToken);
            var response = await sender.SendAsync(call, cancellationToken).ConfigureAwait(false);
            var firstPage = skipToken is null ? readFirstPage : null;
            skipToken = AmsBotServiceCall.Read(response, call.Phase, "a valid page", "list", root => ReadPage(root, readItem, firstPage, items));
            if (skipToken is not null && (!skipTokens.Add(skipToken) || skipTokens.Count >= MaxPages))
            {
                throw AgentDeployException.ServiceRejected(AgentDeployException.AddCorrelation($"The {call.Phase} returned a next page link that repeats an earlier page.", response.ClientRequestId, response.ServiceRequestId), ExitCodes.Failure, "invalid-ams-response", "Do not rely on this list. Inspect the service correlation before acting on it.");
            }
        }
        while (skipToken is not null);

        return items;
    }

    /// <summary>Returns the skip token of a next page link, or null when there is no next page.</summary>
    internal static string? SkipToken(string? nextLink) => nextLink is null ? null : QueryParameters(nextLink).FirstOrDefault(parameter => string.Equals(parameter.Name, "$skiptoken", StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } token ? token : throw new FormatException("nextLink has no $skiptoken.");

    private static string? ReadPage<TItem>(JsonElement root, Func<JsonElement, TItem> readItem, Action<JsonElement>? readFirstPage, List<TItem> items)
    {
        readFirstPage?.Invoke(root);
        items.AddRange(AmsJson.GetRequiredArray(root, "value").Select(readItem));
        return SkipToken(AmsJson.GetOptionalString(root, "nextLink"));
    }

    private static IEnumerable<(string Name, string Value)> QueryParameters(string link) => link.IndexOf('?', StringComparison.Ordinal) is var start and >= 0 ? link[(start + 1)..].Split('#')[0].Split('&').Select(pair => pair.Split('=', 2)).Select(pair => (Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty)) : [];
}
