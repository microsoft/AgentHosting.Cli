using System.Diagnostics;

namespace Microsoft.Agents.Hosting.Cli.Core.Playground;

/// <summary>Safe, privacy-preserving categories for foreground playground launch failures.</summary>
public enum PlaygroundLaunchFailureKind
{
    InvalidTool,
    InvalidTarget,
    InvalidClientId,
    InvalidTenantId,
    InvalidLaunchDirectory,
    StartFailed,
    CleanupFailed,
}

/// <summary>A bounded playground launch failure that never carries process or secret details.</summary>
public sealed class PlaygroundLaunchException(
    PlaygroundLaunchFailureKind kind,
    string message,
    string remediation) : Exception(message)
{
    public PlaygroundLaunchFailureKind Kind { get; } = kind;

    public string Remediation { get; } = remediation;
}

/// <summary>The fully materialized argument and child-environment contract for one launch.</summary>
public sealed record PlaygroundLaunchPlan(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public string? WorkingDirectory { get; init; }
}

/// <summary>Owns the foreground playground process for the duration of one invocation.</summary>
public interface IPlaygroundLauncher
{
    Task<int> RunAsync(
        PlaygroundTool tool,
        PlaygroundConnection connection,
        Action started,
        CancellationToken cancellationToken);
}

/// <summary>Non-secret, transport-specific inputs for a native playground launch.</summary>
public abstract record PlaygroundConnection
{
    private PlaygroundConnection() { }

    public sealed record DirectLine : PlaygroundConnection;

    public sealed record DirectEngine(
        Operations.LiveTestDestination Target,
        Guid ClientId) : PlaygroundConnection;
}

/// <summary>The small process lifecycle seam used to test ownership and shutdown ordering.</summary>
public interface IPlaygroundProcessFactory
{
    IPlaygroundProcess Start(ProcessStartInfo startInfo);
}

/// <summary>One exact child process owned by a foreground playground invocation.</summary>
public interface IPlaygroundProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void SendInterrupt();

    void KillTree();
}
