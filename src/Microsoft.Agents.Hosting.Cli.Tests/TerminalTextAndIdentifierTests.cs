using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class TerminalTextTests
{
    [Fact]
    public void PlainTextIsUnchanged() =>
        Assert.Equal("Contoso Agent", TerminalText.Sanitize("Contoso Agent"));

    [Fact]
    public void AnEscapeSequenceIsNeutralized()
    {
        var sanitized = TerminalText.Sanitize("\u001b[31mDANGER\u001b[0m");

        Assert.DoesNotContain('\u001b', sanitized);
        Assert.Contains("DANGER", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\u0007")]
    [InlineData("\u0008")]
    [InlineData("\u0085")]
    [InlineData("\u009b")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void ControlCharactersAreReplaced(string control) =>
        Assert.DoesNotContain(control, TerminalText.Sanitize($"before{control}after"), StringComparison.Ordinal);

    [Theory]
    [InlineData("\u202e")]
    [InlineData("\u202d")]
    [InlineData("\u202a")]
    [InlineData("\u2066")]
    [InlineData("\u200b")]
    [InlineData("\u200e")]
    public void BidirectionalAndInvisibleFormattingIsReplaced(string formatting) =>
        Assert.DoesNotContain(formatting, TerminalText.Sanitize($"Dev{formatting}dnetorp"), StringComparison.Ordinal);

    [Fact]
    public void ARightToLeftOverrideCannotDisguiseAName()
    {
        var sanitized = TerminalText.Sanitize("Dev\u202Ednetorp");

        Assert.DoesNotContain('\u202E', sanitized);
        Assert.Equal("Dev\uFFFDdnetorp", sanitized);
    }

    [Fact]
    public void TabsArePreserved() =>
        Assert.Equal("a\tb", TerminalText.Sanitize("a\tb"));

    [Theory]
    [InlineData("\u200c")]
    [InlineData("\u200d")]
    public void TextShapingJoinersArePreserved(string joiner) =>
        Assert.Equal($"a{joiner}b", TerminalText.Sanitize($"a{joiner}b"));

    [Fact]
    public void AJoinedEmojiSequenceSurvivesIntact()
    {
        var family = "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67";

        Assert.Equal(family, TerminalText.Sanitize(family));
    }

    [Fact]
    public void APersianWordUsingTheNonJoinerSurvivesIntact()
    {
        var word = "\u0645\u06CC\u200C\u0631\u0648\u062F";

        Assert.Equal(word, TerminalText.Sanitize(word));
    }

    [Fact]
    public void BidiControlsAreStillReplacedAlongsideThePreservedJoiners()
    {
        var sanitized = TerminalText.Sanitize("a\u200db\u202Ec");

        Assert.Contains('\u200d', sanitized);
        Assert.DoesNotContain('\u202E', sanitized);
    }

    [Fact]
    public void LengthIsPreservedSoColumnsStillAlign() =>
        Assert.Equal(10, TerminalText.Sanitize("agent\u001bname").Length);

    [Fact]
    public void NullAndEmptyAreSafe()
    {
        Assert.Equal(string.Empty, TerminalText.Sanitize(null));
        Assert.Equal(string.Empty, TerminalText.Sanitize(string.Empty));
    }

    [Fact]
    public void CursorMovementCannotOverwritePreviousOutput()
    {
        var sanitized = TerminalText.Sanitize("Deleted 0 agents\u001b[2A\u001b[2KDeleted 99 agents");

        Assert.DoesNotContain('\u001b', sanitized);
    }

    [Fact]
    public void AServiceDetailOverTheLimitIsCutToItAndEndsWithThreeDots() =>
        Assert.Equal(new string('a', 600) + "...", TerminalText.ServiceDetail(new string('a', 601)));
}

public sealed class ServiceIdentifierTests
{
    [Theory]
    [InlineData("env-1")]
    [InlineData("Default-72f988bf-86f1-41af-91ab-2d7cd011db47")]
    [InlineData("e3f97c4c-413b-ee65-b37e-b0545ce1d527")]
    [InlineData("a.b_c-d")]
    public void ABareIdentifierIsAccepted(string value) => Assert.True(ServiceIdentifier.IsWellFormed(value));

    [Theory]
    [InlineData("../../other-env")]
    [InlineData("env/1")]
    [InlineData("env?query=1")]
    [InlineData("env#fragment")]
    [InlineData("env%2f1")]
    [InlineData("env 1")]
    [InlineData("env\u001b[31m")]
    [InlineData("env\\1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnIdentifierCarryingPathOrQueryCharactersIsRejected(string? value) =>
        Assert.False(ServiceIdentifier.IsWellFormed(value));

    [Fact]
    public void AnOverlongIdentifierIsRejected() =>
        Assert.False(ServiceIdentifier.IsWellFormed(new string('a', 129)));

    [Theory]
    [InlineData("/providers/Microsoft.PowerApps/environments/env-1")]
    [InlineData("env-1")]
    public void AResourcePathOfWellFormedSegmentsIsAccepted(string value) =>
        Assert.True(ServiceIdentifier.IsWellFormedResource(value));

    [Theory]
    [InlineData("/providers/../../../etc/passwd")]
    [InlineData("/providers/env-1?x=1")]
    [InlineData("/providers/env 1")]
    [InlineData("..")]
    public void AResourcePathCarryingTraversalOrInjectionIsRejected(string value) =>
        Assert.False(ServiceIdentifier.IsWellFormedResource(value));

    [Theory]
    [InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47", true)]
    [InlineData("not-a-guid", false)]
    [InlineData("", false)]
    public void GuidRecognitionIsExact(string value, bool expected) =>
        Assert.Equal(expected, ServiceIdentifier.IsGuid(value));
}
