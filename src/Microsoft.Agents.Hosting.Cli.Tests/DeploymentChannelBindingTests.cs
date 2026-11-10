using System.CommandLine;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment.Channel;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelBindingTests
{
    [Fact]
    public async Task Update_BindsScalarAssignmentsWithSeparateFields()
    {
        var capture = await Execute("facebook",
            "--parameters", "isEnabled=false", "appSecret=value=with=equals");

        var request = Assert.IsType<DeploymentChannelUpdateRequest>(capture.Request);
        Assert.Equal(["isEnabled=false", "appSecret=value=with=equals"], request.Parameters);
    }

    private static async Task<Capture> Execute(string channel, params string[] arguments)
    {
        var capture = new Capture();
        await Execute(channel, capture, arguments);
        return capture;
    }

    private static Task<object> Execute(string channel, Capture capture, params string[] arguments)
    {
        var implementation = new DeploymentChannelUpdateCommand();
        var command = new Command("update");
        var symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var declaration in implementation.Symbols.OfType<CommandOption>())
        {
            Option option = declaration.ValueType == typeof(bool)
                ? new Option<bool>(declaration.Name)
                : declaration.ValueType == typeof(string[])
                ? new Option<string[]>(declaration.Name)
                {
                    AllowMultipleArgumentsPerToken = declaration.AllowMultipleArgumentsPerToken,
                }
                : new Option<string>(declaration.Name) { Required = declaration.IsRequired };
            if (declaration.ShortAlias is { } alias)
            {
                option.Aliases.Add(alias);
            }
            foreach (var extraAlias in declaration.ExtraAliases)
            {
                option.Aliases.Add(extraAlias);
            }
            command.Add(option);
            symbols.Add(declaration.Name, option);
        }

        var parse = command.Parse(["--deployment-id", "dep-1", "--channel", channel, .. arguments]);
        Assert.Empty(parse.Errors);
        var input = new SystemCommandLineInput(parse, symbols, json: false, nonInteractive: true);
        return implementation.ExecuteAsync(input, capture, default);
    }

    private sealed class Capture : IServiceProvider, IDeploymentAgentTypeResolution,
        ICommandHandler<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>
    {
        internal DeploymentChannelUpdateRequest? Request { get; private set; }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IDeploymentAgentTypeResolution) ||
            serviceType == typeof(ICommandHandler<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>)
                ? this : null;

        public DeploymentAgentTypeResult Resolve(DeploymentAgentTypeQuery query) =>
            new() { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.DeploymentRecord };

        public Task<DeploymentResult<DeploymentChannelRecord>> HandleAsync(
            DeploymentChannelUpdateRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new DeploymentResult<DeploymentChannelRecord> { Status = DeploymentOperationStatus.Updated });
        }
    }
}
