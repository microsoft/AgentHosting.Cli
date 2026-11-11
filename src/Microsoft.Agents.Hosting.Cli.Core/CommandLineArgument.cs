using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core;

/// <summary>Quotes the values of a printed command so it runs as printed in the operator's shell.</summary>
public static class CommandLineArgument
{
    private const string SafePunctuation = "_-.:/+";

    /// <summary>Quotes a value for the shell of this platform, leaving a value that needs no quoting unchanged.</summary>
    /// <param name="value">The value to print.</param>
    /// <returns>The value, quoted so the shell passes it on unchanged.</returns>
    public static string Quote(string value) => Quote(value, OperatingSystem.IsWindows());

    internal static string Quote(string value, bool forWindows)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || SafePunctuation.Contains(character, StringComparison.Ordinal) || (forWindows && character == '\\')))
        {
            return value;
        }

        return forWindows ? QuoteForWindows(value) : $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static string QuoteForWindows(string value)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else if (character == '"')
            {
                quoted.Append('\\', backslashes * 2).Append("\"\"");
                backslashes = 0;
            }
            else
            {
                quoted.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
