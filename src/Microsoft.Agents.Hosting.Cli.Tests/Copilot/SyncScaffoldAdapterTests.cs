using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncScaffoldAdapterTests
{
    private readonly SyncScaffoldAdapter _scaffolds = new();

    public static TheoryData<string> AgentTypes => new(CopilotAuthoringModes.SupportedAgentTypes);

    [Fact]
    public void Scaffold_WritesTheResolvedTemplateAndReportsItsFiles()
    {
        using var directory = new TemporaryDirectory();

        var created = _scaffolds.Scaffold(new CopilotScaffoldCommand { ProjectDirectory = directory.Path, TemplateFolder = CopilotAuthoringModes.Resolve(null).TemplateFolder, DisplayName = "Contoso", SchemaName = "cr_contoso", Instructions = "Be helpful." });

        Assert.Equal(["agent.sync.yaml", "settings.mcs.yml"], created);
        Assert.All(created, file => Assert.True(File.Exists(Path.Combine(directory.Path, file))));
    }

    [Fact]
    public void Scaffold_AnUnknownTemplateFolder_IsTemplateNotFoundAndWritesNothing()
    {
        using var directory = new TemporaryDirectory();

        Assert.Equal(CopilotErrorKinds.TemplateNotFound, Assert.Throws<CopilotStudioException>(() => _scaffolds.Scaffold(new CopilotScaffoldCommand { ProjectDirectory = directory.Path, TemplateFolder = "missing", DisplayName = "Contoso", SchemaName = "cr_contoso", Instructions = string.Empty })).ErrorKind);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Theory]
    [MemberData(nameof(AgentTypes))]
    public void Scaffold_TheTemplateEveryAgentTypeResolvesTo_IsOneTheLibraryShips(string mcsAgentType)
    {
        using var directory = new TemporaryDirectory();

        var created = _scaffolds.Scaffold(new CopilotScaffoldCommand { ProjectDirectory = directory.Path, TemplateFolder = CopilotAuthoringModes.Resolve(mcsAgentType).TemplateFolder, DisplayName = "Contoso", SchemaName = "cr_contoso", Instructions = "Be helpful." });

        Assert.NotEmpty(created);
        Assert.All(created, file => Assert.True(File.Exists(Path.Combine(directory.Path, file))));
    }

    [Fact]
    public void Scaffold_ANameNoAgentCanTake_IsAUsageErrorGivingTheDisplayNameChecksReasonAndWritesNothing()
    {
        using var directory = new TemporaryDirectory();

        var failure = Assert.Throws<UsageException>(() => _scaffolds.Scaffold(new CopilotScaffoldCommand { ProjectDirectory = directory.Path, TemplateFolder = CopilotAuthoringModes.CliCopilot, DisplayName = "!!!", SchemaName = "cr_contoso", Instructions = string.Empty }));

        Assert.Equal(Assert.Throws<UsageException>(() => _scaffolds.ValidateDisplayName("!!!")).Message, failure.Message);
        Assert.False(Directory.Exists(directory.Path));
    }
}