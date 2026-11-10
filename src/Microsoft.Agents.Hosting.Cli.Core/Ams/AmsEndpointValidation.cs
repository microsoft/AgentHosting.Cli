using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal static class AmsEndpointValidation
{
    internal static Uri RequireHttps(string value, string unparsableSubject, string parsedSubject, Func<string, string, AgentHostingException> notUsable)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw notUsable($"{unparsableSubject} is not an absolute uri.", "Supply an absolute https endpoint, for example https://<host>.");
        }

        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw notUsable($"{parsedSubject} uses scheme '{endpoint.Scheme}', and a delegated token is only ever sent over https.", "Supply an https endpoint.");
        }

        return string.IsNullOrEmpty(endpoint.UserInfo) ? endpoint : throw notUsable($"{parsedSubject} carries embedded user information.", "Supply an https endpoint with no embedded credentials.");
    }
}
