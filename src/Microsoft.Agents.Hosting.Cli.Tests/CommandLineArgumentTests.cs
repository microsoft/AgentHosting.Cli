using Microsoft.Agents.Hosting.Cli.Core;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandLineArgumentTests
{
    [Theory]
    [InlineData("dev")]
    [InlineData("f0f0f0f0-1111-2222-3333-444444444444")]
    [InlineData("cr1_agent.v2:+x")]
    [InlineData(@"C:\Users\me\fork-1")]
    [InlineData("--agent-id")]
    public void Quote_ForWindows_LeavesAValueThatNeedsNoQuotingUnchanged(string value)
    {
        Assert.Equal(value, CommandLineArgument.Quote(value, forWindows: true));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("/home/me/fork-1")]
    [InlineData("cr1_agent.v2:+x")]
    [InlineData("-a.b:c")]
    public void Quote_OutsideWindows_LeavesAValueThatNeedsNoQuotingUnchanged(string value)
    {
        Assert.Equal(value, CommandLineArgument.Quote(value, forWindows: false));
    }

    [Theory]
    [InlineData(@"C:\Users\me\My Agents", @"'C:\Users\me\My Agents'")]
    [InlineData(@"C:\work\$HOME", @"'C:\work\$HOME'")]
    [InlineData("$(whoami)", "'$(whoami)'")]
    [InlineData("agent`1", "'agent`1'")]
    [InlineData(@"say ""hi""", @"'say ""hi""'")]
    [InlineData("/tmp/O'Brien/$HOME", "'/tmp/O''Brien/$HOME'")]
    [InlineData("O\u2019Brien \u2018a\u2019 \u201Ab\u201B", "'O\u2019\u2019Brien \u2018\u2018a\u2019\u2019 \u201A\u201Ab\u201B\u201B'")]
    [InlineData("say \u201Chi\u201D \u201E$x", "'say \u201Chi\u201D \u201E$x'")]
    [InlineData(@"C:\My Agents\", @"'C:\My Agents\'")]
    [InlineData("", "''")]
    [InlineData("@team", "'@team'")]
    [InlineData("=dev", "'=dev'")]
    [InlineData("dev,", "'dev,'")]
    [InlineData("Café", "'Café'")]
    [InlineData("-a.b", "'-a.b'")]
    [InlineData("-dev:", "'-dev:'")]
    public void Quote_ForWindows_WrapsTheValueInAPowerShellLiteral(string value, string expected)
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
    [InlineData("Sales!Bot", "'Sales!Bot'")]
    public void Quote_OutsideWindows_WrapsTheValueInSingleQuotes(string value, string expected)
    {
        Assert.Equal(expected, CommandLineArgument.Quote(value, forWindows: false));
    }

    [Fact]
    public void Quote_UsesTheShellOfThisPlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "'it''s'" : @"'it'\''s'", CommandLineArgument.Quote("it's"));
    }
}
