namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>How the token cache is protected at rest on a given platform.</summary>
public enum CacheProtectionKind
{
    /// <summary>Windows DPAPI current-user protection.</summary>
    WindowsDpapi,

    /// <summary>macOS Keychain.</summary>
    MacKeychain,

    /// <summary>Linux libsecret / Gnome Keyring.</summary>
    LinuxKeyring,

    /// <summary>An unprotected plaintext file (only ever used after explicit opt-in).</summary>
    UnprotectedFile,
}

/// <summary>
/// The immutable, platform-neutral description of where and how the AgentHost MSAL token cache is
/// stored. It uses AgentHost-specific service, account, keyring, and file names so the cache is not
/// shared with PAC or the TypeScript CLI, and it carries no secret material. This pure record is the
/// input used to build MSAL <c>StorageCreationProperties</c>, so cache configuration is unit-testable
/// against temporary directories.
///
/// Adapted from PAC's <c>MsalExtensionCache</c> constants with AgentHost-owned names.
/// </summary>
public sealed record AgentHostCacheDescriptor
{
    /// <summary>The directory the cache (and its locking file) live in.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>The cache file name (also the lock file on Keychain/Keyring platforms).</summary>
    public required string CacheFileName { get; init; }

    /// <summary>The macOS Keychain service name.</summary>
    public required string MacKeychainService { get; init; }

    /// <summary>The macOS Keychain account name.</summary>
    public required string MacKeychainAccount { get; init; }

    /// <summary>The Linux keyring schema (collection namespace).</summary>
    public required string LinuxKeyringSchema { get; init; }

    /// <summary>The Linux keyring collection.</summary>
    public required string LinuxKeyringCollection { get; init; }

    /// <summary>The Linux keyring human-readable label.</summary>
    public required string LinuxKeyringLabel { get; init; }

    /// <summary>The full path to the cache file (<see cref="CacheDirectory"/> + <see cref="CacheFileName"/>).</summary>
    public string CacheFilePath => Path.Combine(CacheDirectory, CacheFileName);

    /// <summary>The AgentHost cache directory segment appended under the config directory.</summary>
    public const string CacheSubdirectory = "cache";

    /// <summary>The auth-cache leaf directory that isolates the C# demonstrator's cache.</summary>
    public const string AuthCacheLeaf = "auth-csharp";

    /// <summary>
    /// Builds the descriptor for the AgentHost cache under the supplied config directory at
    /// <c>{configDir}/cache/auth-csharp</c>. The config directory is resolved by the host; Core never
    /// reads the environment.
    /// </summary>
    public static AgentHostCacheDescriptor Create(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException("Config directory is required.", nameof(configDirectory));
        }

        return new AgentHostCacheDescriptor
        {
            CacheDirectory = Path.Combine(configDirectory, CacheSubdirectory, AuthCacheLeaf),
            CacheFileName = "agenthost_csharp_msal.cache",
            MacKeychainService = "agenthost_cli_csharp_service",
            MacKeychainAccount = "agenthost_cli_csharp_account",
            LinuxKeyringSchema = "com.microsoft.agenthost.cli.csharp",
            LinuxKeyringCollection = "default",
            LinuxKeyringLabel = "MSAL token cache for AgentHost CLI (C# demonstrator)",
        };
    }
}
