using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public enum DeploymentParameterType
{
    String,
    Boolean,
}

/// <summary>Controls whether a parsed <c>Key=</c> assignment may have an empty value.</summary>
internal enum DeploymentParameterEmptyValuePolicy
{
    Reject,
    Allow,
}

/// <summary>Parses, validates, and normalizes catalog-driven deployment parameters.</summary>
internal static class DeploymentParameterValidator
{
    /// <summary>
    /// Parses raw CLI assignments into a case-insensitive parameter map while rejecting malformed or duplicate
    /// entries. For example, <c>[" Enabled = True ", "Url=https://example.test?a=b"]</c> becomes
    /// <c>{ Enabled: "True", Url: "https://example.test?a=b" }</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ParseParameterAssignments(
        string optionName,
        IReadOnlyList<string> assignments,
        DeploymentParameterEmptyValuePolicy emptyValuePolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionName);
        ArgumentNullException.ThrowIfNull(assignments);

        var parsedParameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            RejectInvalidAssignment(optionName, assignment, index, emptyValuePolicy);

            var separator = assignment.IndexOf('=');
            var parameterName = assignment[..separator].Trim();
            var parameterValue = assignment[(separator + 1)..].Trim();
            RejectDuplicateKey(optionName, parsedParameters, parameterName, index);
            parsedParameters.Add(parameterName, parameterValue);
        }

        return parsedParameters;
    }

    /// <summary>
    /// Rejects an assignment that lacks a key/value separator, a key, or—when empty values are disallowed—a
    /// value. For example, create rejects <c>clientId=</c>, while update may use it to clear an optional value.
    /// </summary>
    private static void RejectInvalidAssignment(
        string optionName,
        string assignment,
        int index,
        DeploymentParameterEmptyValuePolicy emptyValuePolicy)
    {
        var separator = assignment.IndexOf('=');
        if (separator <= 0)
        {
            throw new UsageException(
                $"Invalid {optionName} value at position {index + 1}. Use Key=Value.");
        }

        var parameterName = assignment[..separator].Trim();
        var parameterValue = assignment[(separator + 1)..].Trim();
        if (parameterName.Length == 0 ||
            (emptyValuePolicy == DeploymentParameterEmptyValuePolicy.Reject && parameterValue.Length == 0))
        {
            throw new UsageException(
                $"Invalid {optionName} value at position {index + 1}. " +
                (emptyValuePolicy == DeploymentParameterEmptyValuePolicy.Allow
                    ? "Key is required; use Key= to clear an optional value."
                    : "Both Key and Value are required."));
        }
    }

    /// <summary>
    /// Rejects a repeated key using the parser's case-insensitive comparison. For example,
    /// <c>clientId=a</c> followed by <c>CLIENTID=b</c> is a duplicate.
    /// </summary>
    private static void RejectDuplicateKey(
        string optionName,
        Dictionary<string, string?> parsedParameters,
        string parameterName,
        int index)
    {
        if (parsedParameters.ContainsKey(parameterName))
        {
            throw new UsageException(
                $"{optionName} contains a duplicate key at position {index + 1}.");
        }
    }

    /// <summary>
    /// Rejects supplied parameter names that are absent from the catalog. Matching is case-insensitive, so
    /// <c>clientid</c> matches a catalog entry named <c>ClientId</c>, while <c>Unknown=value</c> is rejected.
    /// </summary>
    internal static void RejectUnknownParameters<TDefinition>(
        IEnumerable<TDefinition> definitions,
        IReadOnlyDictionary<string, string?> supplied,
        Func<TDefinition, string> name,
        Func<string, UsageException> error)
    {
        var known = definitions
            .Select(name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var parameterName in supplied.Keys.Where(parameterName => !known.Contains(parameterName)))
        {
            throw error(parameterName);
        }
    }

    /// <summary>
    /// Enforces create semantics for required parameters: every required parameter must be explicitly supplied
    /// with a non-blank value. A catalog default does not satisfy this check; for example, an omitted required
    /// <c>ClientId</c> is rejected even if its definition contains a default.
    /// </summary>
    internal static void RejectOmittedOrBlankRequiredParameters<TDefinition>(
        IEnumerable<TDefinition> definitions,
        IReadOnlyDictionary<string, string?> supplied,
        Func<TDefinition, string> name,
        Func<TDefinition, bool> required,
        Func<string, UsageException> error)
    {
        foreach (var definition in definitions.Where(required))
        {
            var parameterName = name(definition);
            if (!supplied.TryGetValue(parameterName, out var suppliedValue) ||
                string.IsNullOrWhiteSpace(suppliedValue))
            {
                throw error(parameterName);
            }
        }
    }

    /// <summary>
    /// Enforces update semantics for required parameters: omission is allowed and preserves the existing value,
    /// but an explicit blank is rejected. For example, omitting <c>ClientId</c> is valid while
    /// <c>ClientId=</c> is not.
    /// </summary>
    internal static void RejectClearingRequiredParameters<TDefinition>(
        IEnumerable<TDefinition> definitions,
        IReadOnlyDictionary<string, string?> supplied,
        Func<TDefinition, string> name,
        Func<TDefinition, bool> required,
        Func<string, UsageException> error)
    {
        foreach (var definition in definitions.Where(required))
        {
            var parameterName = name(definition);
            if (supplied.TryGetValue(parameterName, out var suppliedValue) &&
                string.IsNullOrWhiteSpace(suppliedValue))
            {
                throw error(parameterName);
            }
        }
    }

    /// <summary>
    /// Returns a copy with normalized defaults added only for omitted optional parameters. For example, an
    /// optional Boolean default of <c>True</c> becomes <c>true</c>; defaults on required parameters are ignored.
    /// </summary>
    internal static Dictionary<string, string?> ApplyOptionalParameterDefaults<TDefinition>(
        IEnumerable<TDefinition> definitions,
        IReadOnlyDictionary<string, string?> normalized,
        Func<TDefinition, string> name,
        Func<TDefinition, bool> required,
        Func<TDefinition, DeploymentParameterType> type,
        Func<TDefinition, string?> defaultValue,
        Func<string, DeploymentParameterType, UsageException> invalidValue)
    {
        var result = normalized.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value,
            StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Where(definition => !required(definition)))
        {
            var parameterName = name(definition);
            if (result.ContainsKey(parameterName) ||
                DeploymentRequestValidator.Trim(defaultValue(definition)) is not { } value)
            {
                continue;
            }

            result[parameterName] = NormalizeValue(
                parameterName,
                type(definition),
                value,
                invalidValue);
        }

        return result;
    }

    /// <summary>
    /// Returns a new parameter map using catalog casing and normalized typed values without mutating the supplied
    /// map. For example, <c>{ ISENABLED: "True" }</c> becomes <c>{ isEnabled: "true" }</c>.
    /// Empty values are preserved for callers that have already validated whether clearing is allowed.
    /// </summary>
    internal static Dictionary<string, string?> NormalizeParameters<TDefinition>(
        IEnumerable<TDefinition> definitions,
        IReadOnlyDictionary<string, string?> supplied,
        Func<TDefinition, string> name,
        Func<TDefinition, DeploymentParameterType> type,
        Func<string, DeploymentParameterType, UsageException> invalidValue)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var parameterName = name(definition);
            if (!supplied.TryGetValue(parameterName, out var suppliedValue))
            {
                continue;
            }

            if (string.IsNullOrEmpty(suppliedValue))
            {
                normalized[parameterName] = string.Empty;
                continue;
            }

            normalized[parameterName] = NormalizeValue(
                parameterName,
                type(definition),
                suppliedValue,
                invalidValue);
        }

        return normalized;
    }

    /// <summary>
    /// Normalizes one catalog value according to its declared type. For example, Boolean values
    /// <c>True</c> and <c>FALSE</c> become <c>true</c> and <c>false</c>.
    /// </summary>
    internal static string NormalizeValue(
        string parameterName,
        DeploymentParameterType type,
        string value,
        Func<string, DeploymentParameterType, UsageException> error)
    {
        if (type == DeploymentParameterType.Boolean)
        {
            if (!bool.TryParse(value, out var parsed))
            {
                throw error(parameterName, type);
            }

            return parsed ? "true" : "false";
        }

        return value;
    }
}
