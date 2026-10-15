using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// A trivial, immutable <see cref="IInteractionPolicy"/> whose answer is fixed at construction. The host
/// composes it from the resolved <c>--non-interactive</c> flag and host interactivity; the auth flows
/// consult it before attempting any UI. It never performs a prompt itself. Having a concrete
/// implementation in Core keeps the interaction-policy seam testable without the command host.
/// </summary>
public sealed class StaticInteractionPolicy : IInteractionPolicy
{
    /// <summary>An interaction policy that forbids all interactive identity UI.</summary>
    public static StaticInteractionPolicy NonInteractive { get; } = new(false);

    /// <summary>An interaction policy that allows interactive identity UI.</summary>
    public static StaticInteractionPolicy Interactive { get; } = new(true);

    public StaticInteractionPolicy(bool isInteractionAllowed)
    {
        IsInteractionAllowed = isInteractionAllowed;
    }

    /// <inheritdoc />
    public bool IsInteractionAllowed { get; }
}
