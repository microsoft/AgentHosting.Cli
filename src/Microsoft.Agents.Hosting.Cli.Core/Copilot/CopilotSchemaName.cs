using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Derives and validates the schema names that identify an agent within an environment.
///
/// A schema name is <c>{publisherPrefix}_{sanitizedName}</c>, matching what Copilot Studio produces,
/// so a workspace created here is indistinguishable from one created by the portal or another tool.
/// </summary>
public sealed partial class CopilotSchemaName
{
    private const int MaximumLength = 100;

    /// <summary>Derives a schema name, or validates the one the caller supplied.</summary>
    /// <param name="displayName">The agent's display name.</param>
    /// <param name="publisherPrefix">The publisher prefix.</param>
    /// <param name="explicitSchemaName">An explicit schema name that overrides derivation.</param>
    /// <returns>The validated schema name.</returns>
    public string Derive(string? displayName, string? publisherPrefix, string? explicitSchemaName)
    {
        if (!string.IsNullOrWhiteSpace(explicitSchemaName))
        {
            return Validate(explicitSchemaName);
        }

        if (string.IsNullOrWhiteSpace(publisherPrefix))
        {
            throw new UsageException("A publisher prefix is required. Pass --publisher-prefix, or supply --schema-name directly.");
        }

        if (!PublisherPrefixPattern().IsMatch(publisherPrefix))
        {
            throw new UsageException($"The publisher prefix '{publisherPrefix}' is not valid. Use 2 to 8 alphanumeric characters starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new UsageException("A name is required to derive a schema name. Pass --agent-name, or supply --schema-name directly.");
        }

        return Validate($"{publisherPrefix.ToLowerInvariant()}_{Sanitize(displayName)}");
    }

    /// <summary>Validates a schema name against the Copilot Studio contract.</summary>
    /// <param name="schemaName">The schema name to validate.</param>
    /// <returns>The schema name, unchanged.</returns>
    // Deliberately an instance member: registered as a singleton and injected alongside Derive, so both
    // halves of the schema-name contract are reached through the same seam.
#pragma warning disable CA1822
    public string Validate(string schemaName)
#pragma warning restore CA1822
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new UsageException("A schema name is required.");
        }

        if (schemaName.Length > MaximumLength)
        {
            throw new UsageException($"The schema name '{schemaName}' is longer than {MaximumLength} characters.");
        }

        if (!SchemaNamePattern().IsMatch(schemaName))
        {
            throw new UsageException($"The schema name '{schemaName}' contains unsupported characters. Use letters, digits, and the characters _ - . {{ }} !");
        }

        return schemaName;
    }

    private static string Sanitize(string displayName)
    {
        var sanitized = new StringBuilder(displayName.Length);
        foreach (var character in displayName)
        {
            if (char.IsLetterOrDigit(character))
            {
                sanitized.Append(char.ToLowerInvariant(character));
            }
        }

        return sanitized.Length == 0
            ? throw new UsageException($"The name '{displayName}' contains no letters or digits, so no schema name can be derived. Supply --schema-name.")
            : sanitized.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.{}!]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublisherPrefixPattern();
}
