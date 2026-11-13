using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Shared validation for optional, case-sensitive authentication profile names.</summary>
public static class AuthProfileName
{
    public const int MaxLength = 30;

    /// <summary>Accepts unnamed profiles or bounded, nonblank names without control characters.</summary>
    public static bool IsValid(string? name) =>
        name is null || !string.IsNullOrWhiteSpace(name) && name.Length <= MaxLength && !name.Any(char.IsControl);

    public static void Validate(string? name)
    {
        if (!IsValid(name))
            throw new UsageException($"An authentication profile name must contain 1 to {MaxLength} characters and must not be whitespace-only or contain control characters.");
    }
}
