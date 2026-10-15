namespace Microsoft.Agents.Hosting.Cli.Core.Http;

public sealed record ServiceCorrelation(string? ClientRequestId, string? ServiceRequestId)
{
    public const string ClientRequestIdHeader = "x-ms-client-request-id";

    private static readonly string[] ServiceRequestIdHeaders = ["x-ms-request-id", "x-ms-correlation-request-id", "request-id"];

    public static ServiceCorrelation None { get; } = new(null, null);

    public static string? ReadServiceRequestId(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var headerName in ServiceRequestIdHeaders)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            }
        }

        return null;
    }

    public string Describe() => string.Join(", ", Parts());

    public string Annotate(string message) =>
        Describe() is { Length: > 0 } correlation ? $"{message} ({correlation})." : message;

    private IEnumerable<string> Parts()
    {
        if (!string.IsNullOrWhiteSpace(ClientRequestId))
        {
            yield return $"clientRequestId={ClientRequestId}";
        }

        if (!string.IsNullOrWhiteSpace(ServiceRequestId))
        {
            yield return $"serviceRequestId={ServiceRequestId}";
        }
    }
}
