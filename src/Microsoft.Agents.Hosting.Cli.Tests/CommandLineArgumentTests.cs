using Microsoft.Agents.Hosting.Cli.Core;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandLineArgumentTests
{
    [Theory]
    [InlineData("dev")]
    [InlineData("f0f0f0f0-1111-2222-3333-444444444444")]
    [InlineData("cr1_agent.v2:+x")]
    [InlineData(@"C:\Users\me\fork-1")]
    public void Quote_ForWindows_LeavesAValueThatNeedsNoQuotingUnchanged(string value)
    {
        Assert.Equal(value, CommandLineArgument.Quote(value, forWindows: true));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("/home/me/fork-1")]
    [InlineData("cr1_agent.v2:+x")]
    public void Quote_OutsideWindows_LeavesAValueThatNeedsNoQuotingUnchanged(string value)
    {
        Assert.Equal(value, CommandLineArgument.Quote(value, forWindows: false));
    }

    [Theory]
    [InlineData(@"C:\Users\me\My Agents", @"""C:\Users\me\My Agents""")]
    [InlineData(@"say ""hi""", @"""say """"hi""""""")]
    [InlineData("it's", @"""it's""")]
    [InlineData(@"C:\My Agents\", @"""C:\My Agents\\""")]
    [InlineData(@"a\""b", @"""a\\""""b""")]
    [InlineData(@"a\\b c", @"""a\\b c""")]
    [InlineData("", @"""""")]
    [InlineData("@team", @"""@team""")]
    [InlineData("=dev", @"""=dev""")]
    [InlineData("dev,", @"""dev,""")]
    [InlineData("Café", @"""Café""")]
    public void Quote_ForWindows_WrapsTheValueInDoubleQuotesTheWayMsagentParsesThem(string value, string expected)
    {
        Assert.Equal(expected, CommandLineArgument.Quote(value, forWindows: true));
    }

    [Theory]
    [InlineData("My Agents", "'My Agents'")]
    [InlineData(@"say ""hi""", @"'say ""hi""'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("", "''")]
    [InlineData("Cost $5 and `whoami`", "'Cost $5 and `whoami`'")]
    [InlineData(@"C:\dir", @"'C:\dir'")]
    [InlineData("=dev", "'=dev'")]
    public void Quote_OutsideWindows_WrapsTheValueInSingleQuotes(string value, string expected)
    {
        Assert.Equal(expected, CommandLineArgument.Quote(value, forWindows: false));
    }

    [Fact]
    public void Quote_UsesTheShellOfThisPlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? @"""a b""" : "'a b'", CommandLineArgument.Quote("a b"));
    }
}
