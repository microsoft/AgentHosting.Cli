using System.Collections.Frozen;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

public static class PowerPlatformApiRingSelection
{
    private sealed record RingDescriptor(
        PowerPlatformApiRing Ring,
        string Token,
        string StateSuffix,
        PowerPlatformApiTargetConfig Target,
        IReadOnlyList<string> Aliases);

    private static readonly RingDescriptor[] Descriptors =
    [
        new(PowerPlatformApiRing.Production, "prod", string.Empty, PowerPlatformApiTargetConfig.Public, ["production"]),
        new(PowerPlatformApiRing.Preprod, "preprod", "-preprod", PowerPlatformApiTargetConfig.Preprod, []),
        new(PowerPlatformApiRing.Test, "test", "-test", PowerPlatformApiTargetConfig.Test, []),
    ];

    private static readonly FrozenDictionary<PowerPlatformApiRing, RingDescriptor> ByRing =
        Descriptors.ToFrozenDictionary(descriptor => descriptor.Ring);

    private static readonly FrozenDictionary<string, RingDescriptor> ByToken =
        Descriptors
            .SelectMany(
                descriptor => new[] { descriptor.Token }.Concat(descriptor.Aliases),
                (descriptor, token) => (Token: token, Descriptor: descriptor))
            .ToFrozenDictionary(
                entry => entry.Token,
                entry => entry.Descriptor,
                StringComparer.OrdinalIgnoreCase);

    public static PowerPlatformApiTargetConfig Default => TargetFor(PowerPlatformApiRing.Production);

    /// <summary>The tokens an operator may supply, in the order help text lists them.</summary>
    public static IReadOnlyList<string> AcceptedTokens { get; } =
        [.. Descriptors.Select(descriptor => descriptor.Token)];

    /// <summary>Every declared ring, so state that is scoped per ring can be enumerated.</summary>
    public static IReadOnlyList<PowerPlatformApiRing> DeclaredRings { get; } =
        [.. Descriptors.Select(descriptor => descriptor.Ring)];

    /// <summary>The accepted tokens as one display string, so every message spells them the same way.</summary>
    public static string AcceptedTokenList { get; } = string.Join(", ", AcceptedTokens);

    /// <summary>
    /// Resolves an operator-supplied ring token. A null or whitespace token selects
    /// <see cref="Default"/>; matching is case-insensitive and ignores surrounding whitespace.
    /// </summary>
    public static bool TryResolve(
        string? token,
        out PowerPlatformApiTargetConfig target,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            target = Default;
            error = null;
            return true;
        }

        var trimmed = token.Trim();

        if (ByToken.TryGetValue(trimmed, out var descriptor))
        {
            target = descriptor.Target;
            error = null;
            return true;
        }

        target = Default;
        error = $"'{trimmed}' is not a Power Platform API ring. Supply one of: {AcceptedTokenList}.";
        return false;
    }

    /// <summary>The token that names a ring in help text and messages.</summary>
    public static string CanonicalToken(PowerPlatformApiRing ring) => Describe(ring).Token;

    /// <summary>Whether a value is one of the declared rings.</summary>
    /// <param name="ring">The value to check.</param>
    /// <returns><see langword="true"/> when a ring is declared for it.</returns>
    public static bool IsDeclared(PowerPlatformApiRing ring) => ByRing.ContainsKey(ring);

    /// <summary>
    /// Returns <paramref name="ring"/> when it is declared, and rejects it otherwise. Used before a
    /// ring is persisted, so an undefined value from an API caller cannot be written to state that a
    /// later command, including the one that repairs it, has to read.
    /// </summary>
    /// <param name="ring">The ring an API caller supplied.</param>
    /// <returns>The same ring.</returns>
    /// <exception cref="UsageException">No ring is declared for the value.</exception>
    public static PowerPlatformApiRing Require(PowerPlatformApiRing ring) =>
        IsDeclared(ring)
            ? ring
            : throw new UsageException($"'{(int)ring}' is not a Power Platform API cloud. Supply one of: {AcceptedTokenList}.");

    /// <summary>
    /// The suffix that distinguishes a ring's own state. Production returns an empty suffix so its
    /// files keep the names they had before the ring became selectable.
    /// </summary>
    public static string StateSuffix(PowerPlatformApiRing ring) => Describe(ring).StateSuffix;

    /// <summary>The fixed coordinates a ring targets.</summary>
    public static PowerPlatformApiTargetConfig TargetFor(PowerPlatformApiRing ring) => Describe(ring).Target;

    /// <summary>
    /// Scopes a config-directory file or folder name to a ring, inserting the suffix before any
    /// extension. Identity state is deliberately not scoped this way: every ring shares one login
    /// authority and one client id, so one signed-in account serves all of them.
    /// </summary>
    public static string RingScopedName(string name, PowerPlatformApiRing ring)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        var suffix = StateSuffix(ring);

        if (suffix.Length == 0)
        {
            return name;
        }

        var extension = Path.GetExtension(name);

        return string.Concat(name.AsSpan(0, name.Length - extension.Length), suffix, extension);
    }

    private static RingDescriptor Describe(PowerPlatformApiRing ring) =>
        ByRing.TryGetValue(ring, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(ring), ring, "Unknown Power Platform API ring.");
}
