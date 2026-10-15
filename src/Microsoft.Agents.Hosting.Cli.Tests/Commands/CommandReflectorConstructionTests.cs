using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Tests.Commands;

public sealed class CommandReflectorConstructionTests
{
    private abstract class AbstractCommand : ICommandImplementation
    {
        public IReadOnlyList<CommandSymbol> Symbols => [];

        public IReadOnlyList<Type> ResultTypes => [];

        public Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<object>("unused");
    }

    private sealed class InjectedCommand : ICommandImplementation
    {
        public InjectedCommand(string dependency) => Dependency = dependency;

        public string Dependency { get; }

        public IReadOnlyList<CommandSymbol> Symbols => [];

        public IReadOnlyList<Type> ResultTypes => [];

        public Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<object>(Dependency);
    }

    private sealed class ThrowingCommand : ICommandImplementation
    {
        public ThrowingCommand() => throw new InvalidOperationException("boom");

        public IReadOnlyList<CommandSymbol> Symbols => [];

        public IReadOnlyList<Type> ResultTypes => [];

        public Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<object>("unused");
    }

    private sealed class NotACommand
    {
    }

    [Fact]
    public void AnAbstractCommandIsNamedInTheFailure()
    {
        var failure = Assert.Throws<CommandConfigurationException>(() => CommandReflector.Instantiate(typeof(AbstractCommand)));

        Assert.Contains(nameof(AbstractCommand), failure.Message, StringComparison.Ordinal);
        Assert.Contains("abstract", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandNeedingConstructorArgumentsExplainsTheRule()
    {
        var failure = Assert.Throws<CommandConfigurationException>(() => CommandReflector.Instantiate(typeof(InjectedCommand)));

        Assert.Contains(nameof(InjectedCommand), failure.Message, StringComparison.Ordinal);
        Assert.Contains("parameterless constructor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AThrowingConstructorIsReportedAsConfiguration()
    {
        var failure = Assert.Throws<CommandConfigurationException>(() => CommandReflector.Instantiate(typeof(ThrowingCommand)));

        Assert.Contains(nameof(ThrowingCommand), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeThatIsNotACommandImplementationIsNamed()
    {
        var failure = Assert.Throws<CommandConfigurationException>(() => CommandReflector.Instantiate(typeof(NotACommand)));

        Assert.Contains(nameof(NotACommand), failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ICommandImplementation), failure.Message, StringComparison.Ordinal);
    }
}
