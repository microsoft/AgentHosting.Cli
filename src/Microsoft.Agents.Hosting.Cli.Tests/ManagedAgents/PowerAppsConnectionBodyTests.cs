using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionBodyTests
{
    [Theory]
    [InlineData("consentLink")]
    [InlineData("testLinks")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("idToken")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("password")]
    public void Redacted_RemovesEveryCredentialBearingField(string propertyName)
    {
        var redacted = PowerAppsConnectionBody.Redacted($"{{\"{propertyName}\":\"https://login.example/?code=abc123\"}}");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_MatchesACredentialFieldWhateverItsCasing()
    {
        Assert.DoesNotContain("abc123", PowerAppsConnectionBody.Redacted("{\"ConsentLink\":\"abc123\"}"), StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_ReachesCredentialFieldsNestedUnderObjectsAndArrays()
    {
        var redacted = PowerAppsConnectionBody.Redacted("{\"properties\":{\"statuses\":[{\"error\":{\"accessToken\":\"abc123\"}}]}}");

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_KeepsTheFieldsThatCarryNoCredential()
    {
        var redacted = PowerAppsConnectionBody.Redacted("{\"properties\":{\"displayName\":\"Office 365 Outlook\"}}");

        Assert.Contains("Office 365 Outlook", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_ShortensABodyToTheExcerptLimit()
    {
        var body = "{\"displayName\":\"" + new string('x', 10_000) + "\"}";

        Assert.Equal(PowerAppsConnectionBody.MaxExcerptLength, PowerAppsConnectionBody.Redacted(body).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unterminated\":")]
    [InlineData("null")]
    public void Redacted_WithholdsABodyItCannotRead(string body)
    {
        Assert.Equal(PowerAppsConnectionBody.Unreadable, PowerAppsConnectionBody.Redacted(body));
    }

    [Theory]
    [InlineData("{\"consentLink\":\"https://login.example/?code=SECRET123\"")]
    [InlineData("{\"password\":\"SECRET123\",")]
    [InlineData("SECRET123 is not json")]
    public void Redacted_NeverEmitsACredentialFromABodyItCannotRead(string body)
    {
        Assert.DoesNotContain("SECRET123", PowerAppsConnectionBody.Redacted(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_DoesNotRecurseThroughABodyNestedPastTheDepthLimit()
    {
        Assert.Equal(PowerAppsConnectionBody.Unreadable, PowerAppsConnectionBody.Redacted(Nested(PowerAppsConnectionBody.MaxDepth + 50)));
    }

    [Fact]
    public void Redacted_NeverEmitsACredentialNestedPastTheDepthLimit()
    {
        var body = string.Concat(Enumerable.Repeat("{\"a\":", PowerAppsConnectionBody.MaxDepth + 50))
            + "\"SECRET123\""
            + new string('}', PowerAppsConnectionBody.MaxDepth + 50);

        Assert.DoesNotContain("SECRET123", PowerAppsConnectionBody.Redacted(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_BoundHowDeeplyAResponseBodyIsParsed()
    {
        Assert.Null(PowerAppsConnectionResource.Parse(Nested(PowerAppsConnectionBody.MaxDepth + 50)));
        Assert.NotNull(PowerAppsConnectionResource.Parse(Nested(PowerAppsConnectionBody.MaxDepth - 2)));
    }

    [Fact]
    public void Redacted_WithholdsABodyItCannotWriteBackOut()
    {
        var redacted = PowerAppsConnectionBody.Redacted("{\"properties\":{\"displayName\":\"abc123\\ud800\"}}");

        Assert.Equal(PowerAppsConnectionBody.Unreadable, redacted);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacted_SurvivesABodyCarryingAnUnpairedSurrogate()
    {
        Assert.NotNull(PowerAppsConnectionBody.Redacted("{\"properties\":\"a\ud800b\"}"));
    }

    [Theory]
    [InlineData("{\"properties\":\"a\ud800b\"}")]
    [InlineData("{\"properties\":{\"displayName\":\"\\ud800\"}}")]
    public void Parse_AndItsReaders_SurviveAnUnpairedSurrogate(string body)
    {
        using var document = PowerAppsConnectionResource.Parse(body);

        if (document is not null)
        {
            Assert.Null(PowerAppsConnectionResource.DisplayNameOf(document.RootElement));
        }
    }

    [Fact]
    public void Excerpt_LeavesABodyWithinTheLimitAlone()
    {
        Assert.Equal("{}", PowerAppsConnectionBody.Excerpt("{}"));
    }

    private static string Nested(int depth) =>
        string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
}
