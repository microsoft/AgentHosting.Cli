using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ODataLiteralTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("ada@contoso.com", "'ada@contoso.com'")]
    [InlineData("o'brien", "'o''brien'")]
    [InlineData("''", "''''''")]
    public void Quote_WrapsTheValueAndDoublesEveryQuote(string value, string expected) => Assert.Equal(expected, ODataLiteral.Quote(value));

    [Fact]
    public void Quote_RequiresAValue() => Assert.Throws<ArgumentNullException>(() => ODataLiteral.Quote(null!));
}
