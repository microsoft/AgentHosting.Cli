using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>Scaffolds a new local MCS workspace from a template.</summary>
public sealed class CopilotScaffoldService
{
    private readonly ICopilotScaffoldPort _scaffolder;
    private readonly CopilotSchemaName _schemaNames;
    private readonly ICopilotFileSystem _fileSystem;

    /// <summary>Creates the service.</summary>
    /// <param name="scaffolder">Writes template content to disk.</param>
    /// <param name="schemaNames">Derives and validates schema names.</param>
    /// <param name="fileSystem">Inspects and resolves directories.</param>
    public CopilotScaffoldService(ICopilotScaffoldPort scaffolder, CopilotSchemaName schemaNames, ICopilotFileSystem fileSystem)
    {
        _scaffolder = scaffolder ?? throw new ArgumentNullException(nameof(scaffolder));
        _schemaNames = schemaNames ?? throw new ArgumentNullException(nameof(schemaNames));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>Scaffolds a new local workspace. Cloud content is created later by `msagent agent deploy`.</summary>
    /// <param name="request">The command input.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The scaffolded workspace.</returns>
    public CopilotInitResult Init(CopilotInitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new UsageException("`msagent agent create` requires --agent-name.");
        }

        var schemaName = _schemaNames.Derive(request.Name, request.PublisherPrefix, request.SchemaName);
        var projectDirectory = _fileSystem.ResolveFullPath(request.ProjectDirectory ?? _fileSystem.CurrentDirectory);

        if (!_fileSystem.IsEmptyDirectory(projectDirectory))
        {
            throw CopilotStudioException.WorkspaceNotEmpty(projectDirectory);
        }

        var template = CopilotAuthoringModes.Resolve(request.AuthoringMode);

        return new CopilotInitResult
        {
            Status = "initialized",
            ProjectDirectory = projectDirectory,
            SchemaName = schemaName,
            Template = template.TemplateName,
            AuthoringMode = template.AuthoringMode,
            CreatedFiles = _scaffolder.Scaffold(new CopilotScaffoldCommand
            {
                ProjectDirectory = projectDirectory,
                TemplateFolder = template.TemplateFolder,
                DisplayName = request.Name,
                SchemaName = schemaName,
                Instructions = request.Instructions ?? string.Empty,
            }),
        };
    }
}