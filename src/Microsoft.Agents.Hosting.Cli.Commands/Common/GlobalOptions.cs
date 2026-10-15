using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Commands.Common;

/// <summary>Recognises the options the host must honour before the parser runs, in every spelling.</summary>
public static class GlobalOptions
{
    /// <summary>The tokens that ask for help.</summary>
    public static IReadOnlyList<string> HelpTokens => ["--help", "-h", "-?"];

    /// <summary>The tokens that ask for the version.</summary>
    public static IReadOnlyList<string> VersionTokens => ["--version", OptionCatalog.Alias("--version")];

    /// <summary>Whether the arguments supply an option, by canonical name or declared alias.</summary>
    /// <param name="args">The raw command line.</param>
    /// <param name="name">The canonical option name.</param>
    public static bool IsPresent(IReadOnlyList<string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Contains(name, StringComparer.Ordinal) || args.Contains(OptionCatalog.Alias(name), StringComparer.Ordinal);
    }

    /// <summary>Whether the arguments ask for help or the version in any spelling.</summary>
    /// <param name="args">The raw command line.</param>
    public static bool AsksForHelpOrVersion(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(argument => HelpTokens.Contains(argument, StringComparer.Ordinal) || VersionTokens.Contains(argument, StringComparer.Ordinal));
    }

    /// <summary>Whether the arguments ask for help in any spelling.</summary>
    /// <param name="args">The raw command line.</param>
    public static bool AsksForHelp(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Any(argument => HelpTokens.Contains(argument, StringComparer.Ordinal));
    }

    /// <summary>The <c>auth</c> verbs that must run whatever the stored sign-in says.</summary>
    public static IReadOnlyList<string> RingIndependentAuthVerbs => ["login", "logout"];

    /// <summary>
    /// Whether the arguments name work that runs without a cloud read from the stored sign-in.
    /// </summary>
    /// <param name="args">The raw command line.</param>
    /// <returns><see langword="true"/> when the stored sign-in need not be read to dispatch.</returns>
    public static bool IsRingIndependent(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (AsksForHelpOrVersion(args))
        {
            return true;
        }

        string[] verbs = [.. args.Where(argument => !argument.StartsWith('-')).Take(2)];

        return verbs.Length switch
        {
            0 => true,
            2 => verbs[0].Equals("auth", StringComparison.Ordinal)
                && RingIndependentAuthVerbs.Contains(verbs[1], StringComparer.Ordinal),
            _ => false,
        };
    }
}
