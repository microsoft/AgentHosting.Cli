namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>Renders values as OData string literals.</summary>
public static class ODataLiteral
{
    /// <summary>Quotes a value as an OData single-quoted string literal, doubling any quote it carries.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The quoted literal.</returns>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
