using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>The format every deployment name that is being created or renamed must follow.</summary>
public static partial class DeploymentNameRule
{
    private const int MinimumLength = 4;
    private const int MaximumLength = 42;

    /// <summary>Validates a deployment name that is being created or renamed, and returns it trimmed.</summary>
    /// <param name="option">The option that supplied the name, which the error names.</param>
    /// <param name="value">The name to validate.</param>
    /// <returns>The trimmed name.</returns>
    public static string Validate(string option, string? value)
    {
        var trimmed = DeploymentRequestValidator.Required(option, value);

        return trimmed.Length is < MinimumLength or > MaximumLength || !NamePattern().IsMatch(trimmed) ? throw new UsageException($"{option} must be between {MinimumLength} and {MaximumLength} characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.") : trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
