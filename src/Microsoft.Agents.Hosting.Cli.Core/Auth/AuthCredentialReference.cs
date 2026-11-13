namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Persisted credential-reference format markers, independent of profile-file schema versions.</summary>
public static class AuthCredentialReference
{
    /// <summary>Stable v1 marker; future formats must use separate markers to preserve existing references.</summary>
    public const string OwnedV1Prefix = "msagent-owned-v1:";
}
