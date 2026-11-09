using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Validates channel selections and appends catalog-generated parameter metadata to channel help.
/// </summary>
internal sealed class DeploymentChannelHelpAction : SynchronousCommandLineAction
{
    private readonly SynchronousCommandLineAction _inner;
    private readonly IReadOnlySet<Command> _channelCommands;

    public DeploymentChannelHelpAction(
        SynchronousCommandLineAction inner,
        IReadOnlySet<Command> channelCommands)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _channelCommands = channelCommands ?? throw new ArgumentNullException(nameof(channelCommands));
    }

    public override int Invoke(ParseResult parseResult)
    {
        var command = parseResult.CommandResult.Command;
        if (!_channelCommands.Contains(command))
        {
            return _inner.Invoke(parseResult);
        }

        var channelOption = command.Options
            .OfType<Option<string>>()
            .FirstOrDefault(option => option.Name == "--channel");
        if (channelOption is null ||
            parseResult.GetResult(channelOption) is not { Implicit: false } channelResult ||
            channelResult.Tokens.Count == 0)
        {
            return _inner.Invoke(parseResult);
        }

        var channelName = DeploymentChannelCatalog.ValidateChannelName(channelResult.Tokens[^1].Value);
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(channelName, ChannelKeyType.Name);

        if (!DeploymentChannelCatalog.SupportsOperation(channelDefinition, command.Name))
        {
            throw DeploymentException.ChannelOperationNotSupported(channelName, command.Name);
        }

        var hiddenOptions = command.Options.Where(option =>
        {
            var collection = option.Name switch
            {
                "--add-site" or "--set-site" or "--remove-site" => "sites",
                "--add-page" or "--set-page" or "--remove-page" => "pages",
                "--add-registration" or "--set-registration" or "--remove-registration" => "lineRegistrations",
                _ => null,
            };
            return collection is not null &&
                !channelDefinition.Parameters.Any(parameter => parameter.Name == collection);
        }).Select(option => (Option: option, option.Hidden)).ToArray();

        int exitCode;
        try
        {
            foreach (var (option, _) in hiddenOptions)
            {
                option.Hidden = true;
            }

            exitCode = _inner.Invoke(parseResult);
        }
        finally
        {
            foreach (var (option, hidden) in hiddenOptions)
            {
                option.Hidden = hidden;
            }
        }

        if (command.Name is "create" or "update" &&
            channelDefinition.Parameters.Any(parameter => !parameter.IsReadOnly))
        {
            var output = parseResult.InvocationConfiguration.Output ?? Console.Out;
            output.WriteLine("Parameters:");
            WriteParameters(output, channelDefinition.Parameters, command.Name == "create");
        }

        return exitCode;
    }

    private static void WriteParameters(
        TextWriter output,
        IEnumerable<ChannelParameterDefinition> parameters,
        bool isCreate)
    {
        var rows = ParameterRows(parameters, isCreate).ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        var signatureWidth = rows.Max(row => row.Signature.Length) + 2;
        foreach (var row in rows)
        {
            output.WriteLine($"{row.Signature.PadRight(signatureWidth)}{row.Description}");
        }
    }

    private static IEnumerable<(string Signature, string Description)> ParameterRows(
        IEnumerable<ChannelParameterDefinition> parameters,
        bool isCreate,
        int indent = 2)
    {
        foreach (var parameter in parameters
            .Where(parameter => !parameter.IsReadOnly || (!isCreate && parameter.IsSelector))
            .OrderByDescending(parameter => parameter.IsSelector))
        {
            var signature = $"{new string(' ', indent)}{parameter.Name} <{parameter.Type}>" +
                ((isCreate ? parameter.IsRequired : parameter.IsSelector) ? " (REQUIRED)" : string.Empty);
            var description = ParameterDescription(parameter);
            yield return (signature, description);
            if (parameter.ItemParameters is { } itemParameters)
            {
                foreach (var row in ParameterRows(itemParameters, isCreate, indent + 4))
                {
                    yield return row;
                }
            }
        }
    }

    private static string ParameterDescription(ChannelParameterDefinition parameter)
    {
        var description = parameter.DefaultValue is null
            ? parameter.Description
            : $"{parameter.Description} Default: {parameter.DefaultValue}.";
        return parameter.AllowedValues is { } values
            ? $"{description} Allowed values: {string.Join(", ", values)}."
            : description;
    }
}
