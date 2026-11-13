using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Explicit partial state for host rendering; never exposes a raw authentication profile.</summary>
public interface IAuthOperationPartialState
{
    bool MetadataCommitted { get; }
    bool CleanupIncomplete { get; }
    AuthProfileInfo? Profile { get; }
    /// <summary>Whether this partial state describes a committed profile deletion.</summary>
    bool ProfileDeleted => false;
    /// <summary>The active-profile snapshot at deletion commit, or no remaining active profile.</summary>
    AuthProfileInfo? ActiveProfile => null;
}

/// <summary>Preserves the primary typed failure while reporting the transaction's durable state.</summary>
public sealed class AuthOperationException : AgentHostingException, IAuthOperationPartialState
{
    public AuthOperationException(bool metadataCommitted, bool cleanupIncomplete, AuthProfileInfo? profile,
        AgentHostingException primary)
        : this(metadataCommitted, cleanupIncomplete, profile, primary, false, null)
    {
    }

    public AuthOperationException(bool metadataCommitted, bool cleanupIncomplete, AuthProfileInfo? profile,
        AgentHostingException primary, bool profileDeleted, AuthProfileInfo? activeProfile)
        : base(
            metadataCommitted
                ? "Authentication metadata was committed, but credential finalization or owned cleanup is incomplete."
                : "Authentication metadata was not committed; staged credential cleanup is incomplete.",
            primary.ExitCode, primary.ErrorKind, primary.Remediation, primary, primary.ErrorCode)
    {
        MetadataCommitted = metadataCommitted;
        CleanupIncomplete = cleanupIncomplete;
        Profile = profile;
        ProfileDeleted = profileDeleted;
        ActiveProfile = activeProfile;
    }

    public bool MetadataCommitted { get; }
    public bool CleanupIncomplete { get; }
    public AuthProfileInfo? Profile { get; }
    public bool ProfileDeleted { get; }
    public AuthProfileInfo? ActiveProfile { get; }
}

/// <summary>Caller cancellation retains cancellation semantics and explicit safe partial state.</summary>
public sealed class AuthOperationCanceledException : OperationCanceledException, IAuthOperationPartialState
{
    public AuthOperationCanceledException(bool metadataCommitted, bool cleanupIncomplete, AuthProfileInfo? profile,
        OperationCanceledException primary)
        : this(metadataCommitted, cleanupIncomplete, profile, primary, false, null)
    {
    }

    public AuthOperationCanceledException(bool metadataCommitted, bool cleanupIncomplete, AuthProfileInfo? profile,
        OperationCanceledException primary, bool profileDeleted, AuthProfileInfo? activeProfile)
        : base(metadataCommitted
                ? "Cancellation occurred after authentication metadata was committed."
                : cleanupIncomplete
                    ? "Authentication was canceled; staged credential cleanup is incomplete."
                    : "Authentication was canceled before metadata was committed.",
            primary, primary.CancellationToken)
    {
        MetadataCommitted = metadataCommitted;
        CleanupIncomplete = cleanupIncomplete;
        Profile = profile;
        ProfileDeleted = profileDeleted;
        ActiveProfile = activeProfile;
    }

    public bool MetadataCommitted { get; }
    public bool CleanupIncomplete { get; }
    public AuthProfileInfo? Profile { get; }
    public bool ProfileDeleted { get; }
    public AuthProfileInfo? ActiveProfile { get; }
}
