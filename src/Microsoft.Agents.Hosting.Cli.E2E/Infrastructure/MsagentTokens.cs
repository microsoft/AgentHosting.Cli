namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class MsagentTokens : IAccessTokens
{
    private const string PasswordPrefix = "password=";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan UnknownLifetime = TimeSpan.FromMinutes(30);

    private readonly MsagentCli _msagent;
    private readonly string? _gitHost;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Token, DateTimeOffset ExpiresOn)? _cached;

    public MsagentTokens(MsagentCli msagent, string? gitHost, string audience)
    {
        _msagent = msagent;
        _gitHost = gitHost;
        Audience = audience;
    }

    public string Audience { get; }

    public async Task<string> TokenAsync(string resource, CancellationToken cancellationToken)
    {
        if (!string.Equals(resource, Audience, StringComparison.OrdinalIgnoreCase))
        {
            throw new E2EException($"msagent's git credential gives a token for {Audience} only, not for {resource}.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_cached is { } cached && cached.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow)
            {
                return cached.Token;
            }

            var credential = await _msagent.RunAsync(["auth", "git-credential", "get"], stdin: $"protocol=https\nhost={_gitHost}\n", timeout: AmsMcsServices.CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (credential.ExitCode != 0)
            {
                throw new E2EException($"msagent auth git-credential get exited {credential.ExitCode}: {Redaction.Apply(credential.StdErr.Trim())}");
            }

            var token = credential.StdOut.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith(PasswordPrefix, StringComparison.Ordinal)).Select(line => line[PasswordPrefix.Length..].Trim()).FirstOrDefault(value => value.Length > 0) ?? throw new E2EException("msagent auth git-credential get gave no token");

            _cached = (token, AccessTokenPayload.ExpiresOn(token) ?? DateTimeOffset.UtcNow + UnknownLifetime);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }
}
