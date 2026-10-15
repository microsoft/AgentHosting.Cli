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
