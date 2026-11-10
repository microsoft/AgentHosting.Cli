using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public sealed class ProfileFederationAssertionProvider : IDisposable
{
    private const int MaximumResponseBytes = 65536;
    private const string Audience = "api://AzureADTokenExchange";
    private readonly ProfileCredentialRuntimeOptions _runtime;
    private readonly HttpClient _client;
    public ProfileFederationAssertionProvider(ProfileCredentialRuntimeOptions runtime, HttpMessageHandler? transport = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _client = new HttpClient(transport ?? TransportDefaults.CreateRedirectRejectingHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<string> GetAssertionAsync(AuthCredentialMethod method, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Validate(_runtime, method);
        var github = method == AuthCredentialMethod.GitHubFederated;
        var token = github ? _runtime.GitHubRequestToken : _runtime.AzureDevOpsRequestToken;
        var uri = RuntimeUri(github ? _runtime.GitHubRequestUrl : _runtime.AzureDevOpsRequestUrl, github);
        using var request = new HttpRequestMessage(github ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes) throw Failure();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = new byte[MaximumResponseBytes + 1];
            try
            {
                var count = 0;
                while (count <= MaximumResponseBytes)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaximumResponseBytes) throw Failure();
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
                var field = github ? "value" : "oidcToken";
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Count(p => p.Name == field) != 1 ||
                    !document.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString())) throw Failure();
                return value.GetString()!;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or FormatException)
        { throw Failure(); }
    }

    internal static void Validate(ProfileCredentialRuntimeOptions runtime, AuthCredentialMethod method)
    {
        var github = method == AuthCredentialMethod.GitHubFederated;
        if (!github && method != AuthCredentialMethod.AzureDevOpsFederated) throw Failure();
        var token = github ? runtime.GitHubRequestToken : runtime.AzureDevOpsRequestToken;
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw Failure();
        _ = RuntimeUri(github ? runtime.GitHubRequestUrl : runtime.AzureDevOpsRequestUrl, github);
    }

    private static Uri RuntimeUri(string? value, bool github)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw Failure();
        var name = github ? "audience" : "api-version";
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => string.Equals(Uri.UnescapeDataString(p[0]), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parameters.Length > 1 || github && parameters.Length == 1 &&
            (parameters[0].Length != 2 || Uri.UnescapeDataString(parameters[0][1]) != Audience)) throw Failure();
        if (parameters.Length == 1) return uri;
        var query = uri.Query.TrimStart('?');
        var builder = new UriBuilder(uri) { Query = query + (query.Length == 0 ? "" : "&") + name + "=" +
            (github ? Uri.EscapeDataString(Audience) : "7.1-preview.1") };
        return builder.Uri;
    }

    private static AuthenticationException Failure() => new("Federated assertion acquisition failed or its runtime configuration is invalid.",
        remediation: "Check the host-provided federation URL, request token, and runtime response.");
    public void Dispose() => _client.Dispose();
}
