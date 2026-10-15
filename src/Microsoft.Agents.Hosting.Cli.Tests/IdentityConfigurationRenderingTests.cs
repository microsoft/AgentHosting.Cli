using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class IdentityConfigurationRenderingTests
{
    private const string DotNetConfiguration = """
        {
          "Connections": {
            "ServiceConnection": {
              "Settings": {
                "ClientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
              }
            }
          }
        }
        """;

    private static AppRegistrationIdentityReport Report(string format, string content) => new()
    {
        Status = "created",
        TenantId = EnvironmentFixtures.ContosoTenant,
        DisplayName = "Contoso Agent",
        Changes = [],
        NextActions = [],
        Configuration = new IdentityGeneratedConfig { Format = format, Content = content },
    };

    private static string Render(object payload)
    {
        var stdout = new StringWriter();

        new CliOutputRenderer(stdout, new StringWriter(), color: false).RenderSuccess(payload, json: false);

        return stdout.ToString();
    }

    [Fact]
    public void GeneratedConfigurationKeepsItsLineBreaks()
    {
        var rendered = Render(Report("dotnet", DotNetConfiguration));

        Assert.DoesNotContain('\uFFFD', rendered);
        Assert.Contains("  \"Connections\": {", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedDotNetConfigurationRemainsParseableJson()
    {
        var rendered = Render(Report("dotnet", DotNetConfiguration));
        var body = rendered[(rendered.IndexOf('{', StringComparison.Ordinal))..].TrimEnd();

        using var document = JsonDocument.Parse(body);

        Assert.Equal(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            document.RootElement.GetProperty("Connections").GetProperty("ServiceConnection").GetProperty("Settings").GetProperty("ClientId").GetString());
    }

    [Fact]
    public void GeneratedNodeConfigurationRendersOneAssignmentPerLine()
    {
        var lines = Render(Report("node", "applications__userAuth__clientId=app-1\napplications__userAuth__tenantId=tenant-1")).Split(Environment.NewLine);

        Assert.Contains("applications__userAuth__clientId=app-1", lines);
        Assert.Contains("applications__userAuth__tenantId=tenant-1", lines);
    }

    [Fact]
    public void AWindowsLineEndingDoesNotLeaveAStrandedCarriageReturn()
    {
        var rendered = Render(Report("node", "first=1\r\nsecond=2"));

        Assert.DoesNotContain('\uFFFD', rendered);
        Assert.Contains("first=1", rendered.Split(Environment.NewLine));
        Assert.Contains("second=2", rendered.Split(Environment.NewLine));
    }

    [Fact]
    public void AControlCharacterInsideGeneratedConfigurationIsStillNeutralized()
    {
        var rendered = Render(Report("node", "first=\u001b[31mred"));

        Assert.DoesNotContain('\u001b', rendered);
    }
}
