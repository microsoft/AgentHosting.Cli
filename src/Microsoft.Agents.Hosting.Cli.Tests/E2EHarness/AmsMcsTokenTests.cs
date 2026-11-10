using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsTokenTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FakeMsagent _msagent = new();

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public async Task TheTokenIsThePasswordLineOfMsagentsGitCredentialForTheTestApi()
    {
        var token = FakeTokens.Jwt("harness@example.invalid");
        _msagent.Answer("auth git-credential get", $"protocol=https\r\nhost={AmsMcsTestKit.GitHost}\r\nusername=msagent\r\npassword=  {token}  \r\n");
        var tokens = Services().MsagentTokens;

        var given = await tokens.TokenAsync(PowerPlatformApiTargetConfig.Test.Audience.ToUpperInvariant(), CancellationToken.None);

        var call = Assert.Single(_msagent.Calls);
        Assert.Equal(token, given);
        Assert.Equal(PowerPlatformApiTargetConfig.Test.Audience, tokens.Audience);
        Assert.Equal(["auth", "git-credential", "get"], call.Arguments);
        Assert.Equal($"protocol=https\nhost={AmsMcsTestKit.GitHost}\n", call.Stdin);
        Assert.Equal(AmsMcsServices.CallTimeout, call.Timeout);
        Assert.Equal("AmsMcsAgents", call.Environment["AH_CLI_FEATURES"]);
        Assert.Equal(AmsMcsTestKit.GitHost, call.Environment["MSAGENT_GIT_HOST"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("username=msagent\n")]
    [InlineData("username=msagent\npassword=\n")]
    [InlineData("password=   \r\n")]
    [InlineData("username=msagent\nPassword=token\n")]
    [InlineData("username=msagent\n password=token\n")]
    public async Task NoPasswordLineGivesNoToken(string credential)
    {
        _msagent.Answer("auth git-credential get", credential);

        var failure = await Assert.ThrowsAsync<E2EException>(() => Services().MsagentTokens.TokenAsync(PowerPlatformApiTargetConfig.Test.Audience, CancellationToken.None));

        Assert.Equal("msagent auth git-credential get gave no token", failure.Message);
    }

    [Fact]
    public async Task AFailingCredentialCallNamesItsExitAndHidesWhatItPrinted()
    {
        var script = AmsMcsTestKit.Script(_scratch.Folder("bin"), "msagent", "echo password=hunter2 1>&2\r\nexit /b 4\r\n", "echo password=hunter2 >&2\nexit 4\n");
        var transcript = new Transcript(_scratch.Folder("run"));
        var tokens = new MsagentTokens(new MsagentCli(script, transcript, _scratch.Folder("dumps")), AmsMcsTestKit.GitHost, PowerPlatformApiTargetConfig.Test.Audience);

        var failure = await Assert.ThrowsAsync<E2EException>(() => tokens.TokenAsync(PowerPlatformApiTargetConfig.Test.Audience, CancellationToken.None));

        Assert.StartsWith("msagent auth git-credential get exited 4: ", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherResourceIsRefusedWithoutCallingMsagent()
    {
        var failure = await Assert.ThrowsAsync<E2EException>(() => Services().MsagentTokens.TokenAsync("https://service.powerapps.com/", CancellationToken.None));

        Assert.Equal($"msagent's git credential gives a token for {PowerPlatformApiTargetConfig.Test.Audience} only, not for https://service.powerapps.com/.", failure.Message);
        Assert.Empty(_msagent.Calls);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(null)]
    public async Task ATokenIsReusedUntilShortlyBeforeItExpires(int? minutesLeft)
    {
        var token = minutesLeft is { } minutes ? FakeTokens.JwtOf($$"""{"upn":"harness@example.invalid","exp":{{DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds()}}}""") : "opaque-token";
        _msagent.Answer("auth git-credential get", FakeTokens.Credential(token));
        var tokens = Services().MsagentTokens;

        Assert.Equal(token, await tokens.TokenAsync(tokens.Audience, CancellationToken.None));
        Assert.Equal(token, await tokens.TokenAsync(tokens.Audience, CancellationToken.None));
        Assert.Single(_msagent.Calls);
    }

    [Fact]
    public async Task ATokenThatExpiresWithinFiveMinutesIsFetchedAgain()
    {
        var expiring = FakeTokens.JwtOf($$"""{"exp":{{DateTimeOffset.UtcNow.AddMinutes(4).ToUnixTimeSeconds()}}}""");
        var fresh = FakeTokens.JwtOf($$"""{"exp":{{DateTimeOffset.UtcNow.AddMinutes(60).ToUnixTimeSeconds()}}}""");
        _msagent.Answer("auth git-credential get", FakeTokens.Credential(expiring)).Answer("auth git-credential get", FakeTokens.Credential(fresh));
        var tokens = Services().MsagentTokens;

        Assert.Equal(expiring, await tokens.TokenAsync(tokens.Audience, CancellationToken.None));
        Assert.Equal(fresh, await tokens.TokenAsync(tokens.Audience, CancellationToken.None));
        Assert.Equal(fresh, await tokens.TokenAsync(tokens.Audience, CancellationToken.None));
        Assert.Equal(2, _msagent.Calls.Count);
    }

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("""{"ab":1}""")]
    [InlineData("""{"abc":1}""")]
    [InlineData("""{"upn":"a???>>>b"}""")]
    public void APayloadIsReadWhateverItsPadding(string payload)
    {
        var read = AccessTokenPayload.Read(FakeTokens.JwtOf(payload));

        Assert.Equal(payload, read?.GetRawText());
    }

    [Fact]
    public void AnUrlSafePayloadIsDecoded()
    {
        var token = FakeTokens.JwtOf("""{"upn":"a???>>>b"}""");

        Assert.Contains(token.Split('.')[1], character => character is '-' or '_');
        Assert.Equal("a???>>>b", AccessTokenPayload.Read(token)?.GetProperty("upn").GetString());
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("header.payload")]
    [InlineData("a.b.c.d")]
    [InlineData("header.!!!.signature")]
    [InlineData("header.bm90IGpzb24.signature")]
    [InlineData("header.WzFd.signature")]
    public void ATokenThatIsNotAJwtWithAnObjectPayloadReadsAsNothing(string token)
    {
        Assert.Null(AccessTokenPayload.Read(token));
        Assert.Null(AccessTokenPayload.ExpiresOn(token));
    }

    [Theory]
    [InlineData("""{"exp":1893456000}""", 1893456000L)]
    [InlineData("""{"upn":"harness@example.invalid"}""", null)]
    [InlineData("""{"exp":"1893456000"}""", null)]
    [InlineData("""{"exp":1893456000.5}""", null)]
    public void TheExpiryIsTheNumericExpClaim(string payload, long? seconds)
    {
        Assert.Equal(seconds is { } value ? DateTimeOffset.FromUnixTimeSeconds(value) : null, AccessTokenPayload.ExpiresOn(FakeTokens.JwtOf(payload)));
    }

    private AmsMcsServices Services()
    {
        var transcript = new Transcript(_scratch.Folder("run"));
        return new AmsMcsServices(AmsMcsTestKit.Config(), "run-1", _msagent.Cli(transcript, _scratch.Path), transcript, new FakeTokens());
    }
}
