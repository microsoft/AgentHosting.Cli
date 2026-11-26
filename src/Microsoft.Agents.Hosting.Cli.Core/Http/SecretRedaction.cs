namespace Microsoft.Agents.Hosting.Cli.Core.Http;

internal static class SecretRedaction
{
    internal const string Mask = "*****";

    internal static bool IsMasked(string? value) => value == Mask;

    internal static bool IsSecret(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        HasNumberedSuffix(name, "key") ||
        HasNumberedSuffix(name, "token") ||
        HasNumberedSuffix(name, "credential");

    private static bool HasNumberedSuffix(string name, string suffix)
    {
        var suffixEnd = name.Length;
        while (suffixEnd > 0 && char.IsAsciiDigit(name[suffixEnd - 1]))
        {
            suffixEnd--;
        }

        return name.AsSpan(0, suffixEnd).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
