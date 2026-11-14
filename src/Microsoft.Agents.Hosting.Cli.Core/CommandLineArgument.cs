using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.Core;

/// <summary>Quotes the values of a printed command so it runs as printed in the operator's shell.</summary>
public static partial class CommandLineArgument
{
    private const string SafePunctuation = "_-.:/+";

    private const string PowerShellParameterDelimiters = ".:";

    /// <summary>Quotes a value for PowerShell on Windows or a POSIX shell elsewhere, leaving a value that needs no quoting unchanged.</summary>
    /// <param name="value">The value to print.</param>
    /// <returns>The value, quoted so the shell passes it on unchanged.</returns>
    public static string Quote(string value) => Quote(value, OperatingSystem.IsWindows());

    internal static string Quote(string value, bool forWindows)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || SafePunctuation.Contains(character, StringComparison.Ordinal) || (forWindows && character == '\\')) && !(forWindows && value.StartsWith('-') && value.AsSpan().ContainsAny(PowerShellParameterDelimiters)))
        {
            return value;
        }

        return forWindows ? $"'{PowerShellSingleQuote().Replace(value, "$0$0")}'" : $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    [GeneratedRegex("['\u2018-\u201B]", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellSingleQuote();
}
