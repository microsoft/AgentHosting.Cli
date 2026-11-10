using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>Anything the tree builder materialises into a System.CommandLine symbol.</summary>
/// <param name="Name">The symbol name as typed.</param>
/// <param name="Description">Help text.</param>
public abstract record CommandSymbol(string Name, string Description)
{
    /// <summary>The CLR type the parsed value binds to.</summary>
    public Type ValueType { get; init; } = typeof(string);

    /// <summary>Whether the parser requires a value.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Features that must all be enabled before help and parsing expose this symbol.</summary>
    public IReadOnlyList<FeatureName> RequiredFeatures { get; init; } = [];
}

/// <summary>A named option.</summary>
/// <param name="Name">The option name, including leading dashes.</param>
/// <param name="Description">Help text.</param>
public sealed record CommandOption(string Name, string Description) : CommandSymbol(Name, Description)
{
    /// <summary>Whether the option is a bare flag.</summary>
    public bool IsSwitch { get; init; }

    /// <summary>The short alias, declared rather than derived.</summary>
    public string? ShortAlias { get; init; }

    /// <summary>Additional long spellings the option answers to.</summary>
    public IReadOnlyList<string> ExtraAliases { get; init; } = [];

    /// <summary>Whether parser diagnostics must not include this option's values.</summary>
    public bool IsSensitive { get; init; }

    /// <summary>Whether descendants of the owning command also accept the option.</summary>
    public bool Recursive { get; init; }

    /// <summary>Whether one option occurrence may consume multiple values.</summary>
    public bool AllowMultipleArgumentsPerToken { get; init; }
}

/// <summary>A positional argument.</summary>
/// <param name="Name">The argument name as shown in usage.</param>
/// <param name="Description">Help text.</param>
public sealed record CommandArgument(string Name, string Description) : CommandSymbol(Name, Description);
