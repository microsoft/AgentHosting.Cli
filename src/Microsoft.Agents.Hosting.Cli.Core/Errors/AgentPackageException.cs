namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// The closed set of failures the CLI can raise while building a deterministic package.
///
/// <para>
/// AMS reproduces none of these categories: it bounds the request body, computes its digest, and
/// forwards the archive as opaque bytes without parsing it.
/// </para>
/// </summary>
public enum AgentPackageRejection
{
    /// <summary>The source tree already contains a root envelope; the builder emits its own.</summary>
    EnvelopeSourceConflict,

    /// <summary>The archive exceeds the version-zero request-body bound.</summary>
    ArchiveSizeExceeded,

    /// <summary>The source contains more entries than version zero accepts.</summary>
    EntryCountExceeded,

    /// <summary>A link in the package source tree, which would make the digest machine-dependent.</summary>
    SourceLink,

    /// <summary>A package source entry is neither a regular file nor a directory.</summary>
    SourceEntryNotAFile,

    /// <summary>The package source could not be read.</summary>
    SourceUnreadable,
}

/// <summary>
/// A version-zero agent package was rejected while being built from a source tree.
///
/// <para>
/// Every rejection except <see cref="AgentPackageRejection.SourceUnreadable"/> maps to
/// <see cref="ExitCodes.Usage"/>: the content is something the operator supplied and can correct.
/// An unreadable source is environmental and maps to <see cref="ExitCodes.Failure"/>.
/// </para>
///
/// <para>
/// Messages name entry paths, sizes, and limits — never file content, so a rejection cannot leak the
/// bytes it rejected.
/// </para>
/// </summary>
public sealed class AgentPackageException : AgentHostingException
{
    public AgentPackageException(
        AgentPackageRejection rejection,
        string message,
        string? remediation = null,
        Exception? innerException = null)
        : base(
            message,
            rejection == AgentPackageRejection.SourceUnreadable ? ExitCodes.Failure : ExitCodes.Usage,
            ToErrorKind(rejection),
            remediation,
            innerException,
            ToErrorCode(rejection))
    {
        Rejection = rejection;
    }

    private static int ToErrorCode(AgentPackageRejection rejection) => rejection switch
    {
        AgentPackageRejection.EnvelopeSourceConflict => ErrorCodes.Package.EnvelopeSourceConflict,
        AgentPackageRejection.ArchiveSizeExceeded => ErrorCodes.Package.ArchiveSizeExceeded,
        AgentPackageRejection.EntryCountExceeded => ErrorCodes.Package.EntryCountExceeded,
        AgentPackageRejection.SourceLink => ErrorCodes.Package.SourceLink,
        AgentPackageRejection.SourceEntryNotAFile => ErrorCodes.Package.SourceEntryNotAFile,
        AgentPackageRejection.SourceUnreadable => ErrorCodes.Package.SourceUnreadable,
        _ => ErrorCodes.Package.SourceUnreadable,
    };

    /// <summary>The classified rejection category.</summary>
    public AgentPackageRejection Rejection { get; }

    /// <summary>
    /// The stable kebab-case error kind for <paramref name="rejection"/>, prefixed so a package
    /// rejection is distinguishable from any other CLI error kind in structured output.
    /// </summary>
    public static string ToErrorKind(AgentPackageRejection rejection) => rejection switch
    {
        AgentPackageRejection.EnvelopeSourceConflict => "package-envelope-source-conflict",
        AgentPackageRejection.ArchiveSizeExceeded => "package-archive-too-large",
        AgentPackageRejection.EntryCountExceeded => "package-entry-count-exceeded",
        AgentPackageRejection.SourceLink => "package-source-link",
        AgentPackageRejection.SourceEntryNotAFile => "package-source-not-a-file",
        _ => "package-source-unreadable",
    };
}
