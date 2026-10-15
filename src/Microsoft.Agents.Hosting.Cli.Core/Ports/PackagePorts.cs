namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>What to package and where to put it.</summary>
public sealed record AgentPackageBuildRequest
{
    /// <summary>The local project directory to package.</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>The archive path to write, conventionally ending in <c>agent.zip</c>.</summary>
    public required string DestinationPath { get; init; }
}

/// <summary>
/// The result of building one package: where the bytes are, what they hash to, and how big they are.
///
/// <para>
/// It deliberately carries no publication state. Building a package is not publishing one, and the
/// digest here is the value a later publication compares against what the service reports — a
/// client-side integrity guard that holds independently of service-side validation.
/// </para>
/// </summary>
public sealed record AgentPackageBuildResult
{
    /// <summary>The full path of the archive that was written.</summary>
    public required string PackagePath { get; init; }

    /// <summary>The package transport version emitted by the builder.</summary>
    public required string PackageVersion { get; init; }

    /// <summary>Lowercase hexadecimal SHA-256 of the exact archive bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The archive size in bytes.</summary>
    public required long SizeInBytes { get; init; }

    /// <summary>The number of entries written, including the root envelope.</summary>
    public required int EntryCount { get; init; }
}

/// <summary>
/// Builds a version-zero agent package from a local source tree.
///
/// <para>
/// The port exists so command handlers never reach an archive implementation directly, and so the
/// deterministic behavior can be exercised offline. Implementations must be reproducible: the same
/// source content must produce byte-identical output, on any machine, with no dependence on a
/// discovered ignore-set, a compression implementation, a clock, or a local path.
/// </para>
/// </summary>
public interface IAgentPackageBuilder
{
    /// <summary>Builds the package described by <paramref name="request"/>.</summary>
    Task<AgentPackageBuildResult> BuildAsync(AgentPackageBuildRequest request, CancellationToken cancellationToken);
}
