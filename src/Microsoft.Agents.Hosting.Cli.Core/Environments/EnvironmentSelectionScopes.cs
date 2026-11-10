using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// Persisted environment selections available for an explicit reset. Identity changes must preserve
/// these records: ownership is checked on use, and legacy selections remain visible as unbound.
/// </summary>
public sealed class EnvironmentSelectionScopes
{
    private readonly IReadOnlyList<IEnvironmentSelectionStore> _stores;

    /// <summary>Creates the set from one selection store per cloud.</summary>
    public EnvironmentSelectionScopes(IEnumerable<IEnvironmentSelectionStore> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);

        _stores = [.. stores];
    }

    /// <summary>Creates the set covering every declared cloud's selection file under one config directory.</summary>
    public static EnvironmentSelectionScopes ForConfigDirectory(string configDirectory, string selectionFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectionFileName);

        return new EnvironmentSelectionScopes(
            PowerPlatformApiRingSelection.DeclaredRings.Select(ring =>
                new FileEnvironmentSelectionStore(
                    Path.Combine(
                        configDirectory,
                        PowerPlatformApiRingSelection.RingScopedName(selectionFileName, ring)))));
    }

    /// <summary>Discards every cloud's selection and reports whether any was there to discard.</summary>
    public async Task<bool> ClearAllAsync(CancellationToken cancellationToken)
    {
        var discarded = false;

        foreach (var store in _stores)
        {
            discarded |= await DiscardAsync(store, cancellationToken).ConfigureAwait(false);
        }

        return discarded;
    }

    private static async Task<bool> DiscardAsync(IEnvironmentSelectionStore store, CancellationToken cancellationToken)
    {
        bool selected;
        try
        {
            selected = await store.GetActiveAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (AgentHostingStateException)
        {
            selected = true;
        }

        if (selected)
        {
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        return selected;
    }
}
