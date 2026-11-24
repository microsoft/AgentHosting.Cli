using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;

namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

internal static class DeploymentParameterInput
{
    // Expands whole-object file references into assignments, leaving inline assignments unchanged.
    // For example, @parameters.json containing {"enabled":true,"optional":null} produces
    // enabled=true and optional=. Parameter semantics and duplicates remain the plan's responsibility.
    internal static async Task<IReadOnlyList<string>> ReadParametersAsync(
        IReadOnlyList<string> parameters,
        CancellationToken cancellationToken,
        string optionName = "--parameters")
    {
        var assignments = new List<string>();
        foreach (var parameter in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parameter.StartsWith('@'))
            {
                assignments.Add(parameter);
                continue;
            }

            var document = await ReadJsonFileAsync(parameter, optionName, cancellationToken).ConfigureAwait(false);
            if (document.ValueKind != JsonValueKind.Object)
            {
                throw new CommandUsageException($"{optionName} JSON files must contain an object.");
            }

            foreach (var property in document.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Contains('='))
                {
                    throw new CommandUsageException(
                        $"{optionName} JSON files require valid key:value pairs: key cannot be empty, whitespace, or contain =.");
                }

                assignments.Add($"{property.Name}={AssignmentValue(property.Value)}");
            }
        }

        return assignments;
    }

    // Expands add/set object references and catalog-declared array file values without recursive expansion.
    // For example, sites=@sites.json containing [{"siteName":"Primary"}] becomes
    // sites=[{"siteName":"Primary"}], while a string field such as accessToken=@token stays literal.
    internal static async Task<IReadOnlyList<string>> ReadCollectionAssignmentsAsync(
        IReadOnlyList<string> assignments,
        IReadOnlyList<ChannelParameterDefinition> fields,
        string optionName,
        CancellationToken cancellationToken,
        bool allowObjectFiles = false)
    {
        var result = new List<string>(assignments.Count);
        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (allowObjectFiles && fields.Count > 0 && assignment.StartsWith('@'))
            {
                result.AddRange(await ReadParametersAsync(
                    [assignment], cancellationToken, optionName).ConfigureAwait(false));
                continue;
            }

            var separator = assignment.IndexOf('=');
            var field = separator <= 0 ? null : fields.FirstOrDefault(field =>
                !field.IsReadOnly && field.Type == DeploymentParameterType.Array &&
                field.Name.Equals(assignment[..separator].Trim(), StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                result.Add(assignment);
                continue;
            }

            var value = assignment[(separator + 1)..].Trim();
            if (!value.StartsWith('@'))
            {
                result.Add(assignment);
                continue;
            }

            var json = await ReadJsonFileAsync(value, $"{optionName} '{field.Name}'", cancellationToken).ConfigureAwait(false);
            result.Add($"{assignment[..separator]}={(json.ValueKind == JsonValueKind.Null ? string.Empty : json.GetRawText())}");
        }

        return result;
    }

    // Converts a JSON value to assignment text: "hello" becomes hello, null becomes an empty value,
    // and {"items":[null]} remains JSON text with its nested types and nulls unchanged.
    private static string? AssignmentValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };

    // Reads an @path reference and returns JSON detached from the disposed document.
    // For example, @.\sites.json or @C:\configs\sites.json containing [] returns a JSON array.
    // Relative paths use the current directory; read/parse errors omit file contents and cancellation propagates.
    private static async Task<JsonElement> ReadJsonFileAsync(
        string reference, string optionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference[1..]))
        {
            throw new CommandUsageException($"{optionName} @path requires a JSON file path.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(reference[1..], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CommandUsageException(
                $"Cannot read a {optionName} JSON file. Check the path and read permissions.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new CommandUsageException($"{optionName} JSON file contains invalid JSON.");
        }
    }
}
