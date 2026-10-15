namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>The parsed command line, expressed without any parser type.</summary>
public interface ICommandInput
{
    /// <summary>Reads a string option.</summary>
    /// <param name="name">The option name.</param>
    string? GetString(string name);

    /// <summary>Reads a switch.</summary>
    /// <param name="name">The option name.</param>
    bool GetSwitch(string name);

    /// <summary>Reads a switch that distinguishes "not supplied" from false.</summary>
    /// <param name="name">The option name.</param>
    bool? GetOptionalSwitch(string name);

    /// <summary>Reads a nullable integer option.</summary>
    /// <param name="name">The option name.</param>
    int? GetInt(string name);

    /// <summary>Reads a repeated string option.</summary>
    /// <param name="name">The option name.</param>
    string[] GetStrings(string name);

    /// <summary>Reads a positional argument.</summary>
    /// <param name="name">The argument name.</param>
    string? GetArgument(string name);

    /// <summary>Whether the token was present, which differs from a truthy value.</summary>
    /// <param name="name">The option name.</param>
    bool WasProvided(string name);

    /// <summary>Whether machine-readable output was requested.</summary>
    bool Json { get; }

    /// <summary>Whether prompting is forbidden.</summary>
    bool NonInteractive { get; }
}
