namespace Microsoft.Agents.Hosting.Cli.Commands.Model;

/// <summary>A cross-option rule the caller broke, surfaced as a usage error.</summary>
public sealed class CommandUsageException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">The message shown to the caller.</param>
    public CommandUsageException(string message) : base(message)
    {
    }
}

/// <summary>A command declared in a way the framework cannot materialise.</summary>
public sealed class CommandConfigurationException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">What is wrong with the declaration.</param>
    public CommandConfigurationException(string message) : base(message)
    {
    }
}
