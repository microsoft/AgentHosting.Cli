using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>One destination a routed command can dispatch to, feeding the JSON completeness guard.</summary>
/// <param name="RequestType">The request the route binds.</param>
/// <param name="ResultType">The result the route produces.</param>
public sealed record RouteMetadata(Type RequestType, Type ResultType);

/// <summary>The tree-builder's view of a command, independent of how it was declared.</summary>
public interface ICommandImplementation
{
    /// <summary>The options and arguments the command accepts.</summary>
    IReadOnlyList<CommandSymbol> Symbols { get; }

    /// <summary>Every result type the command can produce, feeding the JSON completeness guard.</summary>
    IReadOnlyList<Type> ResultTypes { get; }

    /// <summary>The required-option patterns help renders as usage lines; empty derives one from the required options.</summary>
    IReadOnlyList<UsageRoute> UsageRoutes => [];

    /// <summary>Help text that replaces the command's description while the features it names are enabled, or null to keep the description.</summary>
    FeatureDescription? EnabledDescription => null;

    /// <summary>Runs the command.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken);
}

/// <summary>A command with one request, one result and one handler.</summary>
/// <typeparam name="TRequest">The request the command binds.</typeparam>
/// <typeparam name="TResult">The result the handler produces.</typeparam>
public abstract class CommandBase<TRequest, TResult> : ICommandImplementation
    where TResult : notnull
{
    /// <summary>The options and arguments the command accepts.</summary>
    public abstract IReadOnlyList<CommandSymbol> Symbols { get; }

    /// <summary>Routes for a command that picks its handler from the input; empty for the normal case.</summary>
    public virtual IReadOnlyList<RouteMetadata> Routes => [];

    /// <inheritdoc />
    public virtual IReadOnlyList<UsageRoute> UsageRoutes => [];

    /// <inheritdoc />
    public virtual FeatureDescription? EnabledDescription => null;

    /// <inheritdoc />
    public IReadOnlyList<Type> ResultTypes => Routes.Count == 0 ? [typeof(TResult)] : [.. Routes.Select(route => route.ResultType)];

    /// <summary>Builds the request from the parsed input.</summary>
    /// <param name="input">The parsed command line.</param>
    protected abstract TRequest Bind(ICommandInput input);

    /// <summary>Reports a cross-option rule the input breaks, or null when it is valid.</summary>
    /// <param name="input">The parsed command line.</param>
    protected virtual string? Validate(ICommandInput input) => null;

    /// <summary>Reports a service-aware cross-option rule the input breaks, or null when valid.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="services">The resolved service provider.</param>
    protected virtual string? Validate(ICommandInput input, IServiceProvider services) => Validate(input);

    /// <summary>Rejects a route that is unavailable before validating its route-specific inputs.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="services">The resolved service provider.</param>
    protected virtual void EnsureAvailable(ICommandInput input, IServiceProvider services)
    {
    }

    /// <inheritdoc />
    public async Task<object> ExecuteAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(services);

        EnsureAvailable(input, services);

        if (Validate(input, services) is { } failure)
        {
            throw new CommandUsageException(failure);
        }

        return await DispatchAsync(input, services, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves and invokes the handler for one route's request and result pair.</summary>
    /// <typeparam name="TRouteRequest">The request the route binds.</typeparam>
    /// <typeparam name="TRouteResult">The result the route produces.</typeparam>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="request">The bound request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    protected static async Task<object> InvokeAsync<TRouteRequest, TRouteResult>(IServiceProvider services, TRouteRequest request, CancellationToken cancellationToken)
        where TRouteResult : notnull
    {
        ArgumentNullException.ThrowIfNull(services);

        var handler = (ICommandHandler<TRouteRequest, TRouteResult>?)services.GetService(typeof(ICommandHandler<TRouteRequest, TRouteResult>))
            ?? throw new CommandConfigurationException($"No handler is registered for {typeof(TRouteRequest).Name}.");

        return await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Dispatches to the handler for the bound request.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="services">The resolved service provider.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    protected virtual Task<object> DispatchAsync(ICommandInput input, IServiceProvider services, CancellationToken cancellationToken) =>
        InvokeAsync<TRequest, TResult>(services, Bind(input), cancellationToken);
}
