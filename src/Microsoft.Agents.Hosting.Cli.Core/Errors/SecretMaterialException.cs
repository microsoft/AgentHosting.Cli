namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Raised by the secret-material guard when the ABS provisioning report would carry credential
/// material (a bearer token, a JWT, a PEM block, or a value under a known credential key name). It is
/// an internal invariant breach, not user-correctable input, so it maps to
/// <see cref="ExitCodes.Failure"/> (exit 1) and carries NO <c>errorKind</c> and NO remediation —
/// mirroring how the shipped TypeScript surfaces a generic <c>Error</c> thrown by
/// <c>assertNoSecretMaterial</c>. The message lists the offending property <em>paths</em> only, never
/// the values.
/// </summary>
public sealed class SecretMaterialException : AgentHostingException
{
    public SecretMaterialException(string message)
        : base(message, ExitCodes.Failure)
    {
    }
}
