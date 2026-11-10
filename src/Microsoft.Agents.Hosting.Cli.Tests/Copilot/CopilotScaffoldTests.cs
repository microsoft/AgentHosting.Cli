using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotSchemaNameTests
{
    private readonly CopilotSchemaName _schemaNames = new();

    [Fact]
    public void Derive_BuildsThePrefixedName()
    {
        Assert.Equal("con_contosohelpdesk", _schemaNames.Derive("Contoso Helpdesk", "con", null));
    }

    [Fact]
    public void Derive_PrefersAnExplicitSchemaName()
    {
        Assert.Equal("custom_name", _schemaNames.Derive("Contoso Helpdesk", "con", "custom_name"));
    }

    [Fact]
    public void Derive_LowercasesThePrefix()
    {
        Assert.Equal("con_agent", _schemaNames.Derive("Agent", "CON", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("c")]
    [InlineData("toolongprefix")]
    [InlineData("1con")]
    public void Derive_RejectsAnInvalidPublisherPrefix(string publisherPrefix)
    {
        Assert.Throws<UsageException>(() => _schemaNames.Derive("Agent", publisherPrefix, null));
    }

    [Fact]
    public void Derive_WithoutANameOrSchemaName_IsAUsageError()
    {
        Assert.Throws<UsageException>(() => _schemaNames.Derive(null, "con", null));
    }

    [Fact]
    public void Derive_WithANameCarryingNoLettersOrDigits_IsAUsageError()
    {
        Assert.Throws<UsageException>(() => _schemaNames.Derive("!!!", "con", null));
    }

    [Fact]
    public void Validate_RejectsANameLongerThanOneHundredCharacters()
    {
        Assert.Throws<UsageException>(() => _schemaNames.Validate(new string('a', 101)));
    }

    [Fact]
    public void Validate_RejectsUnsupportedCharacters()
    {
        Assert.Throws<UsageException>(() => _schemaNames.Validate("con agent"));
    }

    [Fact]
    public void Validate_AcceptsTheDocumentedCharacterSet()
    {
        Assert.Equal("con_agent-1.{x}!", _schemaNames.Validate("con_agent-1.{x}!"));
    }
}
