using Microsoft.Agents.Hosting.Cli.Commands.Common;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class GlobalOptionsTests
{
    [Theory]
    [InlineData("--non-interactive")]
    [InlineData("-ni")]
    public void NonInteractiveIsRecognisedInEverySpelling(string token)
    {
        Assert.True(GlobalOptions.IsPresent(["agent", "list", token], "--non-interactive"));
    }

    [Theory]
    [InlineData("--no-color")]
    [InlineData("-nc")]
    public void NoColorIsRecognisedInEverySpelling(string token)
    {
        Assert.True(GlobalOptions.IsPresent(["agent", "list", token], "--no-color"));
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("-j")]
    public void AnUnrelatedOptionDoesNotSatisfyAnother(string token)
    {
        Assert.False(GlobalOptions.IsPresent(["agent", "list", token], "--non-interactive"));
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void HelpAndVersionAreRecognisedInEverySpelling(string token)
    {
        Assert.True(GlobalOptions.AsksForHelpOrVersion([token]));
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void TheVersionTokensAreNotTreatedAsHelp(string token)
    {
        Assert.False(GlobalOptions.AsksForHelp([token]));
    }

    [Fact]
    public void AnEmptyCommandLineAsksForNeither()
    {
        Assert.False(GlobalOptions.AsksForHelpOrVersion([]));
        Assert.False(GlobalOptions.IsPresent([], "--non-interactive"));
    }
}
