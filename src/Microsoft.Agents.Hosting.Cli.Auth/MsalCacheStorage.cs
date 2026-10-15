using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Builds MSAL <see cref="StorageCreationProperties"/> from an AgentHost-owned
/// <see cref="AgentHostCacheDescriptor"/>. This is the single place the pure cache descriptor becomes
/// a live MSAL cache configuration. It mirrors PAC's
/// <c>MsalExtensionCache.GetStorageCreationProperties</c> with AgentHost-owned
/// service/keyring/file names, so the cache is not shared with PAC or the TypeScript CLI. No token is
/// read or written here.
/// </summary>
internal static class MsalCacheStorage
{
    // Matches PAC's LinuxKeyRing attribute pair values so the keyring item shape is familiar.
    private static readonly KeyValuePair<string, string> LinuxKeyringAttr1 = new("Version", "1");
    private static readonly KeyValuePair<string, string> LinuxKeyringAttr2 = new("CacheKind", "MSAL_Token_Cache");

    /// <summary>
    /// Builds the storage properties for <paramref name="descriptor"/>. When
    /// <paramref name="useUnprotectedFile"/> is <see langword="true"/> the Linux backing is an
    /// unprotected file (only after the explicit opt-in resolved by <see cref="AgentHostTokenCache"/>);
    /// otherwise it is the libsecret keyring. Windows (DPAPI) and macOS (Keychain) protection is
    /// implied by the platform and needs no flag.
    /// </summary>
    public static StorageCreationProperties Build(AgentHostCacheDescriptor descriptor, bool useUnprotectedFile)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var builder = new StorageCreationPropertiesBuilder(descriptor.CacheFileName, descriptor.CacheDirectory)
            .WithMacKeyChain(descriptor.MacKeychainService, descriptor.MacKeychainAccount);

        if (useUnprotectedFile)
        {
            builder.WithLinuxUnprotectedFile();
        }
        else
        {
            builder.WithLinuxKeyring(
                descriptor.LinuxKeyringSchema,
                descriptor.LinuxKeyringCollection,
                descriptor.LinuxKeyringLabel,
                LinuxKeyringAttr1,
                LinuxKeyringAttr2);
        }

        return builder.Build();
    }
}
