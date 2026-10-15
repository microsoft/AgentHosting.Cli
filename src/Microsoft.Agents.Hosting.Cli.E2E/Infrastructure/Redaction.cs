using System.Text.RegularExpressions;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Masks anything credential-shaped before it reaches a transcript, a ledger, or a report.</summary>
public static partial class Redaction
{
    private const string Mask = "***";

    private const string KeepPrefix = "$1***";

    private const string KeepEnds = "$1***$2";

    private const string KeepSuffix = "***$1";

    /// <summary>Masks bearer tokens, client secrets, and anything naming a person.</summary>
    /// <param name="value">The text about to be written.</param>
    /// <returns>The same text with nothing secret and nobody named.</returns>
    public static string Apply(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var masked = BearerToken().Replace(value, "******");
        masked = SecretAssignment().Replace(masked, KeepPrefix);
        masked = PersonField().Replace(masked, KeepEnds);
        masked = EmailAddress().Replace(masked, Mask);

        return MaskProse(masked);
    }

    /// <summary>Masks the sentences the CLI writes a principal's name into.</summary>
    private static string MaskProse(string value)
    {
        var masked = PrincipalLine().Replace(value, KeepEnds);
        masked = SharedWith().Replace(masked, KeepEnds);
        masked = RemovedWith().Replace(masked, KeepEnds);
        masked = SendLinkTo().Replace(masked, KeepEnds);
        masked = DidNotEndAccess().Replace(masked, KeepEnds);
        masked = MatchedSeveralAccounts().Replace(masked, KeepEnds);
        masked = OwnsThisAgent().Replace(masked, KeepSuffix);
        masked = AlreadyHeld().Replace(masked, KeepSuffix);

        return HeldNoDirectShare().Replace(masked, KeepSuffix);
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex("""(?i)("?(?:client_?secret|password|secretText|accessToken|id_token)"?\s*[:=]\s*"?)[^"',\s]+""")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("""(?i)("?(?:principalName|signInAddress|userPrincipalName)"?\s*[:=]\s*"?)[^",]*("?)""")]
    private static partial Regex PersonField();

    [GeneratedRegex(@"[A-Za-z0-9._%+'#\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(?m)^(\s*principal\s+).+?(\s*\(\w+ [0-9a-fA-F-]{36}\)\s*)$")]
    private static partial Regex PrincipalLine();

    [GeneratedRegex(@"(Shared .+? with ).+?( as ')")]
    private static partial Regex SharedWith();

    [GeneratedRegex(@"(Removed the direct share of .+? with ).+?(\.)")]
    private static partial Regex RemovedWith();

    [GeneratedRegex(@"(Send this link to ).+?(:)")]
    private static partial Regex SendLinkTo();

    [GeneratedRegex(@"(did not end ).+?('s access)")]
    private static partial Regex DidNotEndAccess();

    [GeneratedRegex(@"(matches more than one account in this environment: ).+?(\.)")]
    private static partial Regex MatchedSeveralAccounts();

    [GeneratedRegex(@"(?m)^.+?( owns this agent,)")]
    private static partial Regex OwnsThisAgent();

    [GeneratedRegex(@"(?m)^.+?( already had ')")]
    private static partial Regex AlreadyHeld();

    [GeneratedRegex(@"(?m)^.+?( held no direct share of )")]
    private static partial Regex HeldNoDirectShare();
}