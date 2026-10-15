namespace Microsoft.Agents.Hosting.Cli.Core.Packaging;

/// <summary>
/// The version-zero package names and the exact root-envelope bytes emitted by the builder.
///
/// <para>
/// The envelope is transport metadata produced by this CLI, not a runtime manifest and not something
/// AMS interprets. Its distinct name avoids colliding with the later developer-authored
/// <c>agent.manifest.json</c> contract.
/// </para>
///
/// <para>
/// The envelope bytes are a fixed literal rather than a serializer result so property order,
/// escaping, indentation, and trailing-newline defaults cannot change the package digest.
/// </para>
/// </summary>
public static class AgentPackageContract
{
    /// <summary>The only package version this contract accepts.</summary>
    public const string PackageVersion = "0";

    /// <summary>The only package type version zero defines.</summary>
    public const string PackageType = "code";

    /// <summary>The root envelope file name inside the archive.</summary>
    public const string EnvelopeFileName = "agenthosting.package.v0.json";

    /// <summary>The package archive file name.</summary>
    public const string ArchiveFileName = "agent.zip";

    /// <summary>The exact bytes of the emitted envelope: UTF-8, no BOM, no trailing newline.</summary>
    public static ReadOnlySpan<byte> EnvelopeBytes =>
        "{\"packageVersion\":\"0\",\"packageType\":\"code\"}"u8;
}
