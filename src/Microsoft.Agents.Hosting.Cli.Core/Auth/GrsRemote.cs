using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Recognizes GRS repositories by their git url and their id.</summary>
public static class GrsRemote
{
    /// <summary>The repository a git url names, when it is an https url of a host msagent signs git in to whose path ends in git/repositories/&lt;id&gt;.</summary>
    /// <param name="url">The url.</param>
    /// <param name="trust">Decides which hosts are GRS hosts.</param>
    /// <returns>The repository id, or null when the url names no GRS repository.</returns>
    public static string? RepositoryId(string? url, GitHostTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && trust.Trusts(uri.Host)
            && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [.., var git, var repositories, var id]
            && string.Equals(git, "git", StringComparison.OrdinalIgnoreCase)
            && string.Equals(repositories, "repositories", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(id, out var repositoryId)
            && repositoryId != Guid.Empty
                ? repositoryId.ToString("D")
                : null;
    }

    /// <summary>Requires a --repository-id value to be a repository id.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id in its canonical form.</returns>
    public static string RequireRepositoryId(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed.ToString("D") : throw new UsageException($"--repository-id '{value}' is not a repository id; `msagent agent show` names an agent's repository.");
}
