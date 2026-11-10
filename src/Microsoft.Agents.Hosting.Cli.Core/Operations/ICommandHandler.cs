namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>A command result that can render itself as human-readable text.</summary>
public interface ITextGridRenderable
{
    /// <summary>The human-readable rendering, one entry per output line, never coloured.</summary>
    IReadOnlyList<string> ToTextLines();
}

/// <summary>A command result whose text lines go to stdout exactly as they are, with no colour or JSON envelope, for a program that reads them.</summary>
public interface IRawOutputResult : ITextGridRenderable;

/// <summary>Runs one command's request and produces its result.</summary>
/// <typeparam name="TRequest">The request the command binds.</typeparam>
/// <typeparam name="TResult">The result produced.</typeparam>
public interface ICommandHandler<in TRequest, TResult>
    where TResult : notnull
{
    /// <summary>Handles one request.</summary>
    /// <param name="request">The bound request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<TResult> HandleAsync(TRequest request, CancellationToken cancellationToken);
}
