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

    [Theory]
    [InlineData("logout")]
    [InlineData("login")]
    public void TheAuthVerbsThatReplaceTheSignInRunWithoutIt(string verb)
    {
        Assert.True(GlobalOptions.IsRingIndependent(["auth", verb]));
        Assert.True(GlobalOptions.IsRingIndependent(["--json", "auth", verb, "--non-interactive"]));
    }

    [Fact]
    public void AnAuthLoginCarryingACloudStillRunsWithoutTheStoredSignIn()
    {
        Assert.True(GlobalOptions.IsRingIndependent(["auth", "login", "--cloud", "preprod"]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("--version")]
    [InlineData("-v")]
    public void DescribingTheToolNeedsNoStoredSignIn(string token)
    {
        Assert.True(GlobalOptions.IsRingIndependent([token]));
        Assert.True(GlobalOptions.IsRingIndependent(["agent", "list", token]));
    }

    [Fact]
    public void AnEmptyCommandLineNeedsNoStoredSignIn()
    {
        Assert.True(GlobalOptions.IsRingIndependent([]));
        Assert.True(GlobalOptions.IsRingIndependent(["--json"]));
    }

    public static TheoryData<string[]> CommandsNeedingTheStoredSignIn() =>
    [
        ["auth", "status"],
        ["auth", "switch", "--cloud", "preprod"],
        ["env", "list"],
        ["agent", "deploy"],
        ["deployment", "connection", "update"],
        ["auth"],
    ];

    [Theory]
    [MemberData(nameof(CommandsNeedingTheStoredSignIn))]
    public void EveryOtherCommandStillReadsTheStoredSignIn(string[] args)
    {
        Assert.False(GlobalOptions.IsRingIndependent(args));
    }

    public static TheoryData<string[]> CommandsCarryingAVerbNameAsAValue() =>
    [
        ["env", "select", "--environment-id", "auth", "login"],
        ["agent", "create", "--display-name", "auth"],
    ];

    [Theory]
    [MemberData(nameof(CommandsCarryingAVerbNameAsAValue))]
    public void AVerbNameAppearingAsAnOptionValueIsNotMistakenForTheCommand(string[] args)
    {
        Assert.False(GlobalOptions.IsRingIndependent(args));
    }

    [Fact]
    public void ANullCommandLineIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => GlobalOptions.IsRingIndependent(null!));
    }
}
