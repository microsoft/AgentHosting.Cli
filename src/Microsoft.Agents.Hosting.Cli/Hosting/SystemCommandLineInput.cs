using System.CommandLine;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Reads a <see cref="ParseResult"/> through the parser-free <see cref="ICommandInput"/> seam.</summary>
public sealed class SystemCommandLineInput : ICommandInput
{
    private readonly ParseResult _parseResult;
    private readonly IReadOnlyDictionary<string, Symbol> _symbols;

    /// <summary>Creates the input.</summary>
    /// <param name="parseResult">The parsed command line.</param>
    /// <param name="symbols">The symbols the command declared, by name.</param>
    /// <param name="json">Whether machine-readable output was requested.</param>
    /// <param name="nonInteractive">Whether prompting is forbidden.</param>
    public SystemCommandLineInput(ParseResult parseResult, IReadOnlyDictionary<string, Symbol> symbols, bool json, bool nonInteractive)
    {
        _parseResult = parseResult ?? throw new ArgumentNullException(nameof(parseResult));
        _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        Json = json;
        NonInteractive = nonInteractive;
    }

    /// <inheritdoc />
    public bool Json { get; }

    /// <inheritdoc />
    public bool NonInteractive { get; }

    /// <inheritdoc />
    public string? GetString(string name) => Read<string>(name);

    /// <inheritdoc />
    public bool GetSwitch(string name) => Read<bool>(name);

    /// <inheritdoc />
    public bool? GetOptionalSwitch(string name) => Read<bool?>(name);

    /// <inheritdoc />
    public int? GetInt(string name) => Read<int?>(name);

    /// <inheritdoc />
    public string[] GetStrings(string name) => Read<string[]>(name) ?? [];

    /// <inheritdoc />
    public string? GetArgument(string name) => Read<string>(name);

    /// <inheritdoc />
    public bool WasProvided(string name) => _symbols.TryGetValue(name, out var symbol) && symbol is Option option && _parseResult.GetResult(option) is { Implicit: false };

    private T? Read<T>(string name) => _symbols.TryGetValue(name, out var symbol) switch
    {
        true when symbol is Option<T> option => _parseResult.GetValue(option),
        true when symbol is Argument<T> argument => _parseResult.GetValue(argument),
        _ => default,
    };
}
