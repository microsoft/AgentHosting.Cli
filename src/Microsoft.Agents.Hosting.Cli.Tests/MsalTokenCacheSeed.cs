using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal static class MsalTokenCacheSeed
{
    private const string Environment = "login.microsoftonline.com";

    public static string HomeAccountId(string username, string tenantId) => $"{ObjectId(username)}.{tenantId}";

    public static string AccessToken(string username) => $"access-token-for-{username}";

    public static void Seed(IPublicClientApplication client, string tenantId, string scope, params string[] usernames)
    {
        var clientId = client.AppConfig.ClientId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cachedAt = now.ToString(CultureInfo.InvariantCulture);
        var expiresOn = (now + 3600).ToString(CultureInfo.InvariantCulture);
        var cache = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
        {
            ["Account"] = Entries(usernames, tenantId, username => new() { ["realm"] = tenantId, ["local_account_id"] = ObjectId(username), ["username"] = username, ["authority_type"] = "MSSTS" }),
            ["AccessToken"] = Entries(usernames, tenantId, username => new() { ["client_id"] = clientId, ["secret"] = AccessToken(username), ["credential_type"] = "AccessToken", ["realm"] = tenantId, ["target"] = scope, ["token_type"] = "Bearer", ["cached_at"] = cachedAt, ["expires_on"] = expiresOn, ["extended_expires_on"] = expiresOn }),
            ["RefreshToken"] = Entries(usernames, tenantId, _ => new() { ["client_id"] = clientId, ["secret"] = "refresh-token", ["credential_type"] = "RefreshToken" }),
        });

        client.UserTokenCache.SetBeforeAccessAsync(notification =>
        {
            notification.TokenCache.DeserializeMsalV3(cache, shouldClearExistingCache: true);
            return Task.CompletedTask;
        });
    }

    private static Dictionary<string, Dictionary<string, string>> Entries(IEnumerable<string> usernames, string tenantId, Func<string, Dictionary<string, string>> fields) =>
        usernames.ToDictionary(username => username, username => new Dictionary<string, string>(fields(username)) { ["home_account_id"] = HomeAccountId(username, tenantId), ["environment"] = Environment });

    private static string ObjectId(string username) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(username)).AsSpan(0, 16)).ToString("D");
}
