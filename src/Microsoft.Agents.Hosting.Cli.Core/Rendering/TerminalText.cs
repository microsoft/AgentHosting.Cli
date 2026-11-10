using System.Globalization;

namespace Microsoft.Agents.Hosting.Cli.Core.Rendering;

/// <summary>
/// Neutralizes terminal control and escape sequences in text that originated outside the CLI.
/// </summary>
public static class TerminalText
{
    private const char Replacement = '\uFFFD';
    private const char ZeroWidthNonJoiner = '\u200C';
    private const char ZeroWidthJoiner = '\u200D';
    private const int MaximumServiceDetailLength = 600;

    /// <summary>Returns <paramref name="text"/> with control characters replaced, preserving tabs.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (!NeedsSanitizing(text))
        {
            return text;
        }

        return string.Create(text.Length, text, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                destination[index] = IsUnsafe(source[index]) ? Replacement : source[index];
            }
        });
    }

    /// <summary>Returns detail a service supplied, sanitized and cut to a length one message can carry.</summary>
    /// <param name="text">The detail the service supplied.</param>
    public static string ServiceDetail(string? text)
    {
        var sanitized = Sanitize(text);
        return sanitized.Length <= MaximumServiceDetailLength ? sanitized.Trim() : string.Concat(sanitized.AsSpan(0, MaximumServiceDetailLength).TrimEnd(), "...");
    }

    private static bool NeedsSanitizing(string text)
    {
        foreach (var character in text)
        {
            if (IsUnsafe(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnsafe(char character) => character is not ('\t' or ZeroWidthNonJoiner or ZeroWidthJoiner) && (char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
