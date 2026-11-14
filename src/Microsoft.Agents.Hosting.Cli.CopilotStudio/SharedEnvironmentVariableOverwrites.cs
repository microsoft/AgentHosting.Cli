using System.Collections.Immutable;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

internal static class SharedEnvironmentVariableOverwrites
{
    private const string EnvironmentVariablesFolder = "environmentvariables";
    private const string EnvironmentVariableFileSuffix = ".mcs.yml";

    internal static ImmutableArray<string>? ApprovedOverwrites(bool overwriteSharedEnvironmentVariables) => overwriteSharedEnvironmentVariables ? null : ImmutableArray<string>.Empty;

    internal static async Task RefuseUnapprovedOverwritesAsync(this IEnvironmentVariableDependencyValidator validator, IEnumerable<EnvironmentVariableDefinition?> writtenVariables, DirectoryPath workspaceFolder, DefinitionBase definition, bool compareWithCloudCache, ISyncDataverseClient dataverseClient, string environmentId, CancellationToken cancellationToken)
    {
        if (!writtenVariables.Any(variable => variable?.SchemaName.Value is { } schemaName && !EnvironmentVariableOwnership.IsOwnedBy(schemaName, definition)))
        {
            return;
        }

        var check = await validator.ValidateEnvironmentVariableDependenciesAsync(workspaceFolder, definition, compareWithCloudCache, dataverseClient, cancellationToken).ConfigureAwait(false);

        if (!check.Diagnostics.IsDefaultOrEmpty)
        {
            throw Translate(new WorkspaceValidationException(check.Diagnostics), environmentId);
        }

        if (!check.OverwrittenSchemaNames.IsDefaultOrEmpty)
        {
            throw CopilotStudioException.SharedEnvironmentVariablesRequireOverwrite(check.OverwrittenSchemaNames, [.. check.OverwrittenSchemaNames.Select(schemaName => $"{EnvironmentVariablesFolder}/{schemaName}{EnvironmentVariableFileSuffix}")], environmentId);
        }
    }

    internal static CopilotStudioException Translate(WorkspaceValidationException failure, string environmentId)
    {
        string[] files = [.. failure.Diagnostics.Where(diagnostic => diagnostic.Kind == WorkspaceDiagnosticKind.UnapprovedOverwrite).Select(diagnostic => diagnostic.FilePath)];

        return files.Length == 0 ? WorkspaceDefinitionReader.FilesUnreadable(failure) : CopilotStudioException.SharedEnvironmentVariablesRequireOverwrite([.. files.Select(SchemaNameOf)], files, environmentId, failure);
    }

    private static string SchemaNameOf(string file) => Path.GetFileName(file) is var name && name.EndsWith(EnvironmentVariableFileSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^EnvironmentVariableFileSuffix.Length] : name;
}
