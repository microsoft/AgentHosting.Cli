using System.Net.Http.Headers;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

internal static class HttpRequestHeadersExtensions
{
    internal static void TryAddWhenPresent(this HttpRequestHeaders headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.TryAddWithoutValidation(name, value);
        }
    }
}
