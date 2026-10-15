using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>
/// Appends catalog-generated parameter metadata to deployment channel help.
/// </summary>
internal sealed class DeploymentChannelHelpAction : SynchronousCommandLineAction
{
    private readonly SynchronousCommandLineAction _inner;
    private readonly IReadOnlySet<Command> _mutationCommands;

    public DeploymentChannelHelpAction(
        SynchronousCommandLineAction inner,
        IReadOnlySet<Command> mutationCommands)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mutationCommands = mutationCommands ?? throw new ArgumentNullException(nameof(mutationCommands));
    }

    public override int Invoke(ParseResult parseResult)
    {
        var exitCode = _inner.Invoke(parseResult);
        var command = parseResult.CommandResult.Command;
        if (!_mutationCommands.Contains(command))
        {
           return exitCode;
        }

        var channelOption = command.Options
            .OfType<Option<string>>()
            .FirstOrDefault(option => option.Name == "--channel");
        if (channelOption is null ||
            parseResult.GetResult(channelOption) is not { Implicit: false } channelResult ||
            channelResult.Tokens.Count == 0 ||
            string.IsNullOrWhiteSpace(channelResult.Tokens[^1].Value))
        {
            return exitCode;
        }

      var channelName = channelResult.Tokens[^1].Value;

      ChannelDefinition channelDefinition;
      try
      {
          channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(
              channelName,
              ChannelKeyType.Name);
      }
      catch (DeploymentException)
      {
          return exitCode;
      }

      if (channelDefinition is not { IsSupported: true })
      {
          return exitCode;
      }

      var output = parseResult.InvocationConfiguration.Output ?? Console.Out;
      var rows = channelDefinition.Parameters
          .Select(parameter => new
          {
            Signature = ParameterSignature(parameter),
            Description = ParameterDescription(parameter),
          })
          .ToArray();

      if (rows.Length == 0)
      {
          return exitCode;
      }

      output.WriteLine("Parameters:");
      var signatureWidth = rows.Max(row => row.Signature.Length) + 2;
      foreach (var row in rows)
      {
          output.WriteLine($"  {row.Signature.PadRight(signatureWidth)}{row.Description}");
      }

      return exitCode;
    }

    private static string ParameterSignature(ChannelParameterDefinition parameter) =>
        $"{parameter.Name} <{parameter.Type}>" +
        (parameter.IsRequired ? " (REQUIRED)" : string.Empty) +
        (parameter.IsReadOnly ? " (READ-ONLY)" : string.Empty);

    private static string ParameterDescription(ChannelParameterDefinition parameter)
    {
        return parameter.DefaultValue is null
            ? parameter.Description
            : $"{parameter.Description} Default: {parameter.DefaultValue}.";
    }
}
