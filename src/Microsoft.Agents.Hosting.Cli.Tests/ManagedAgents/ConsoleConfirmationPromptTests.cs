using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ConsoleConfirmationPromptTests
{
    private static async Task<ConfirmationOutcome> Confirm(string typed, string expected = "tmpl 3") =>
        await new ConsoleConfirmationPrompt(new StringReader(typed), new StringWriter())
            .ConfirmAsync("about to delete", expected, CancellationToken.None);

    [Fact]
    public async Task Confirm_WithTheExactName_Confirms()
    {
        Assert.Equal(ConfirmationOutcome.Confirmed, await Confirm("tmpl 3"));
    }

    [Theory]
    [InlineData("\"tmpl 3\"")]
    [InlineData("'tmpl 3'")]
    public async Task Confirm_WithAQuotedName_Confirms(string typed)
    {
        Assert.Equal(ConfirmationOutcome.Confirmed, await Confirm(typed));
    }

    [Theory]
    [InlineData("\"tmpl 3")]
    [InlineData("tmpl 3\"")]
    [InlineData("'tmpl 3\"")]
    public async Task Confirm_WithAnUnbalancedQuote_DoesNotConfirm(string typed)
    {
        Assert.Equal(ConfirmationOutcome.Mismatched, await Confirm(typed));
    }

    [Fact]
    public async Task Confirm_WithSurroundingWhitespace_Confirms()
    {
        Assert.Equal(ConfirmationOutcome.Confirmed, await Confirm("   tmpl 3   "));
    }

    [Fact]
    public async Task Confirm_WithATypo_ReportsAMismatchRatherThanADecline()
    {
        Assert.Equal(ConfirmationOutcome.Mismatched, await Confirm("templ 3"));
    }

    [Fact]
    public async Task Confirm_WithADifferentCase_ReportsAMismatch()
    {
        Assert.Equal(ConfirmationOutcome.Mismatched, await Confirm("TMPL 3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Confirm_WithNoAnswer_ReportsADecline(string typed)
    {
        Assert.Equal(ConfirmationOutcome.Declined, await Confirm(typed));
    }

    [Fact]
    public async Task Confirm_WhenTheNameIsItselfQuoted_StillConfirmsOnTheExactText()
    {
        Assert.Equal(ConfirmationOutcome.Confirmed, await Confirm("\"quoted\"", "\"quoted\""));
    }

    [Fact]
    public async Task Confirm_WritesTheDetailAndTheExpectedName()
    {
        var output = new StringWriter();

        await new ConsoleConfirmationPrompt(new StringReader("tmpl 3"), output)
            .ConfirmAsync("about to delete", "tmpl 3", CancellationToken.None);

        Assert.Contains("about to delete", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("(tmpl 3)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_NamesTheExactAnswerItWantsForAShortResponse()
    {
        var output = new StringWriter();

        await new ConsoleConfirmationPrompt(new StringReader("yes"), output)
            .ConfirmAsync("accept this connection", "yes", CancellationToken.None);

        Assert.Contains("(yes)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Type the name", output.ToString(), StringComparison.Ordinal);
    }
}
