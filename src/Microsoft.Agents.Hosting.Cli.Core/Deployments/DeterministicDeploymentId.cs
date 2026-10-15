using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

/// <summary>
/// Derives a stable deployment id from the owning agent id and the deployment name. The id is a GUID
/// computed from <c>SHA-256(agentId + "|" + name)</c> with RFC 4122 version/variant bits applied, so
/// the same inputs always yield the same deployment id — giving repeatable runs, stable snapshots, and
/// deterministic tests. The deployment <em>type</em> and identity fields intentionally do not
/// participate: a deployment is identified by its agent and name alone. The agent id (any GUID spelling)
/// and the name (case-insensitive, matching Azure Bot Service naming) are canonicalized first, so
/// equivalent inputs resolve to the same id.
/// </summary>
public static class DeterministicDeploymentId
{
    public static Guid Compute(string agentId, string deploymentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);

        // Canonicalize the agent id so equivalent GUID spellings (braces, casing, hyphenation) all hash
        // to the same deployment id. Non-GUID text is used verbatim (the caller validates GUID shape).
        var canonicalAgentId = Guid.TryParse(agentId.Trim(), out var agentGuid)
            ? agentGuid.ToString("D")
            : agentId.Trim();

        // Azure Bot Service resource names are case-insensitive, so the same agent + a name that differs
        // only in case (e.g. "ProdBot" vs "prodbot") addresses one and the same bot. Canonicalize the
        // name to lower case so both spellings resolve to a single deployment id/record — otherwise two
        // records would adopt the same bot and deleting either would delete the shared bot.
        var canonicalName = deploymentName.Trim().ToLowerInvariant();

        var seed = $"{canonicalAgentId}|{canonicalName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));

        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);

        // Apply RFC 4122 version (5, name-based) and variant bits so the value is a well-formed GUID.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }
}
