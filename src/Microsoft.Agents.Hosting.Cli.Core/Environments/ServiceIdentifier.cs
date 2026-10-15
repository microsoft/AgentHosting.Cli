namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// Validates the shape of identifiers a service returns before they reach a path or a request url.
/// </summary>
public static class ServiceIdentifier
{
    private const int MaximumLength = 128;
    private const int MaximumResourceLength = 512;

    /// <summary>Whether <paramref name="value"/> is a bounded identifier free of path and query characters.</summary>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="value"/> is a bare identifier or a resource path of such segments.</summary>
    public static bool IsWellFormedResource(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumResourceLength
        && !value.Contains("..", StringComparison.Ordinal)
        && value.Split('/').All(segment => segment.Length == 0 || IsWellFormed(segment));

    /// <summary>Whether <paramref name="value"/> is a well-formed GUID.</summary>
    public static bool IsGuid(string? value) => Guid.TryParse(value, out _);
}
