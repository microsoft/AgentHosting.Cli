using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

/// <summary>Binds token-bearing requests to the selected discovery URL and its advertised operations.</summary>
internal sealed class McsLiveEndpointDiscovery(McsLiveTestTarget target)
{
    private string[] _operationUrls = [];

    public Uri DiscoveryUri { get; } = new(
        $"https://{McsLiveTargetValidation.GetAuthority(target)}/copilotstudio/agents/{Uri.EscapeDataString(target.SchemaName)}/conversation-endpoints");

    public Uri? CreateConversationUri { get; private set; }

    public void ReadResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var urls = new List<string>();
            foreach (var (name, requiresConversationId) in new[]
            {
                ("createConversation", false),
                ("executeTurn", true),
                ("continueTurn", true),
                ("subscribe", true),
            })
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    if (name == "subscribe")
                    {
                        continue;
                    }

                    throw new JsonException();
                }

                var url = value.GetString();
                var expanded = url?.Replace("{conversationId}", "conversation", StringComparison.Ordinal);
                if (url is null ||
                    url.Contains("{conversationId}", StringComparison.Ordinal) != requiresConversationId ||
                    expanded!.Contains('{') || expanded.Contains('}') ||
                    !Uri.TryCreate(expanded, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
                    !uri.Host.EndsWith(".environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                {
                    throw new JsonException();
                }

                urls.Add(url);
            }

            _operationUrls = urls.ToArray();
            CreateConversationUri = new Uri(urls[0]);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new McsLiveTestProtocolException("The service returned invalid conversation endpoint discovery.");
        }
    }

    public bool IsExpectedRequestResource(string resource)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        if (string.Equals(uri.AbsoluteUri, DiscoveryUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var template in _operationUrls)
        {
            const string placeholder = "{conversationId}";
            var index = template.IndexOf(placeholder, StringComparison.Ordinal);
            if (index < 0)
            {
                if (string.Equals(uri.AbsoluteUri, new Uri(template).AbsoluteUri, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            const string marker = "mcs-conversation-placeholder";
            var normalized = new Uri(template.Replace(placeholder, marker, StringComparison.Ordinal)).AbsoluteUri;
            index = normalized.IndexOf(marker, StringComparison.Ordinal);
            var prefix = normalized[..index];
            var suffix = normalized[(index + marker.Length)..];
            var actual = uri.AbsoluteUri;
            if (actual.StartsWith(prefix, StringComparison.Ordinal) &&
                actual.EndsWith(suffix, StringComparison.Ordinal) &&
                actual.Length > prefix.Length + suffix.Length)
            {
                var conversation = actual.Substring(prefix.Length, actual.Length - prefix.Length - suffix.Length);
                var decoded = Uri.UnescapeDataString(conversation);
                if (decoded is not "." and not ".." &&
                    !decoded.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%') &&
                    uri == new Uri(template.Replace(placeholder, Uri.EscapeDataString(decoded), StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
