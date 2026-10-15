using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>
/// Reads the ring the active sign-in targets, before any container exists.
/// </summary>
public static class ActiveProfileRing
{
    /// <summary>The ring the active profile targets.</summary>
    /// <param name="profilesFilePath">The profiles file the host persists sign-ins to.</param>
    /// <returns>The ring, or <see cref="PowerPlatformApiRing.Production"/> when nobody is signed in.</returns>
    /// <exception cref="Errors.AuthProfileStateException">The stored profile state is unusable.</exception>
    public static async Task<PowerPlatformApiRing> ReadAsync(string profilesFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesFilePath);

        var active = await new FileAuthProfileStore(profilesFilePath)
            .GetActiveProfileAsync(CancellationToken.None)
            .ConfigureAwait(false);

        return active?.Ring ?? PowerPlatformApiRing.Production;
    }
}
