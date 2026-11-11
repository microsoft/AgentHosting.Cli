using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotSchemaNameTests
{
    private readonly CopilotSchemaName _schemaNames = new(new SyncNamingRulesAdapter());

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
    public void Derive_KeepsOnlyTheAsciiLettersAndDigitsOfTheName()
    {
        Assert.Equal("con_cafagent2", _schemaNames.Derive("Caf\u00E9 Agent 2", "con", null));
    }

    [Fact]
    public void Derive_WithANameCarryingNoAsciiLettersOrDigits_AsksForASchemaName()
    {
        var name = "\u65E5\u672C\u8A9E";

        var failure = Assert.Throws<UsageException>(() => _schemaNames.Derive(name, "con", null));

        Assert.Equal($"The name '{name}' contains no ASCII letters or digits, so no schema name can be derived. Supply --schema-name.", failure.Message);
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

public sealed class CopilotScaffoldServiceTests
{
    private static CopilotScaffoldService Create(
        RecordingScaffoldPort scaffolder,
        FakeCopilotFileSystem? fileSystem = null)
    {
        return new CopilotScaffoldService(
            scaffolder,
            new CopilotSchemaName(new SyncNamingRulesAdapter()),
            fileSystem ?? new FakeCopilotFileSystem());
    }

    [Fact]
    public void Init_ScaffoldsIntoTheResolvedDirectory()
    {
        var scaffolder = new RecordingScaffoldPort();

        var result = Create(scaffolder).Init(
            new CopilotInitRequest { Name = "Contoso Helpdesk", PublisherPrefix = "con" },
            CancellationToken.None);

        Assert.Equal("initialized", result.Status);
        Assert.Equal("con_contosohelpdesk", result.SchemaName);
        Assert.Equal("cli-copilot", result.Template);
        Assert.Equal("cli-copilot", result.AuthoringMode);
        Assert.Equal("/work", result.ProjectDirectory);
    }

    [Fact]
    public void Init_DefaultsToTheCliCopilotShape()
    {
        var scaffolder = new RecordingScaffoldPort();

        Create(scaffolder).Init(
            new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con" },
            CancellationToken.None);

        Assert.Equal("cli-copilot", scaffolder.LastCommand?.TemplateFolder);
    }

    [Fact]
    public void Init_ClassicScaffoldsItsDefaultTemplate()
    {
        var scaffolder = new RecordingScaffoldPort();

        var result = Create(scaffolder).Init(
            new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con", AuthoringMode = "classic" },
            CancellationToken.None);

        Assert.Equal("default", result.Template);
        Assert.Equal("classic", result.AuthoringMode);
        Assert.Equal("classic", scaffolder.LastCommand?.TemplateFolder);
    }

    [Theory]
    [InlineData("CLI", "cli-copilot")]
    [InlineData("cli", "cli-copilot")]
    [InlineData("clicopilot", "cli-copilot")]
    [InlineData("cli-copilot", "cli-copilot")]
    [InlineData("Classic", "classic")]
    [InlineData("classic", "classic")]
    public void Init_AcceptsEveryDocumentedAgentTypeSpelling(string agentType, string expected)
    {
        var result = Create(new RecordingScaffoldPort()).Init(
            new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con", AuthoringMode = agentType },
            CancellationToken.None);

        Assert.Equal(expected, result.AuthoringMode);
    }

    [Fact]
    public void Init_WithAnUnknownAuthoringMode_IsRejectedAndScaffoldsNothing()
    {
        var scaffolder = new RecordingScaffoldPort();

        var error = Assert.Throws<UsageException>(
            () => Create(scaffolder).Init(
                new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con", AuthoringMode = "nonsense" },
                CancellationToken.None));

        Assert.Contains("--mcs-agent-type", error.Message, StringComparison.Ordinal);
        Assert.Contains("Classic", error.Message, StringComparison.Ordinal);
        Assert.Null(scaffolder.LastCommand);
    }

    [Theory]
    [InlineData("Classic", "classic")]
    [InlineData("CliCopilot", "cli-copilot")]
    public void Init_AcceptsTheShapeNameAsAnAlias(string authoringMode, string expected)
    {
        var result = Create(new RecordingScaffoldPort()).Init(
            new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con", AuthoringMode = authoringMode },
            CancellationToken.None);

        Assert.Equal(expected, result.AuthoringMode);
    }

    [Fact]
    public void Init_PassesTheDisplayNameAndInstructionsToTheScaffolder()
    {
        var scaffolder = new RecordingScaffoldPort();

        Create(scaffolder).Init(
            new CopilotInitRequest { Name = "Contoso Helpdesk", PublisherPrefix = "con", Instructions = "Be helpful." },
            CancellationToken.None);

        Assert.Equal("Contoso Helpdesk", scaffolder.LastCommand?.DisplayName);
        Assert.Equal("Be helpful.", scaffolder.LastCommand?.Instructions);
    }

    [Fact]
    public void Init_IntoANonEmptyDirectory_IsRejected()
    {
        var scaffolder = new RecordingScaffoldPort();
        var service = Create(scaffolder, new FakeCopilotFileSystem { EmptyDirectory = false });

        var error = Assert.Throws<CopilotStudioException>(
            () => service.Init(new CopilotInitRequest { Name = "Contoso", PublisherPrefix = "con" }, CancellationToken.None));

        Assert.Equal(CopilotErrorKinds.WorkspaceNotEmpty, error.ErrorKind);
        Assert.Null(scaffolder.LastCommand);
    }

    [Fact]
    public void Init_WithoutAName_IsAUsageError()
    {
        Assert.Throws<UsageException>(
            () => Create(new RecordingScaffoldPort()).Init(new CopilotInitRequest { PublisherPrefix = "con" }, CancellationToken.None));
    }
}

public sealed class EmbeddedTemplateScaffolderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ah-copilot-init-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CopilotScaffoldCommand Command(string templateFolder = "cli-copilot", string instructions = "Be helpful.") => new()
    {
        ProjectDirectory = _directory,
        TemplateFolder = templateFolder,
        DisplayName = "Contoso Helpdesk",
        SchemaName = "con_contosohelpdesk",
        Instructions = instructions,
    };

    private string Read(string relativePath) => File.ReadAllText(Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Scaffold_CliCopilot_WritesExactlyTheTwoFilesPacDoes()
    {
        var created = new EmbeddedTemplateScaffolder().Scaffold(Command());

        Assert.Equal(["agent.sync.yaml", "settings.mcs.yml"], created);
    }

    [Fact]
    public void Scaffold_CliCopilot_WritesNoAgentMcsYml()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        Assert.False(File.Exists(Path.Combine(_directory, "agent.mcs.yml")));
    }

    [Theory]
    [InlineData("authoringModel: CliCopilot")]
    [InlineData("kind: CLICopilotRecognizer")]
    [InlineData("series: Sonnet46")]
    [InlineData("accessControlPolicy: GroupMembership")]
    [InlineData("authenticationTrigger: Always")]
    [InlineData("template: cliagent-1.0.0")]
    [InlineData("language: 1033")]
    public void Scaffold_CliCopilot_DeclaresTheShape(string expected)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        Assert.Contains(expected, Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_CliCopilot_WritesInstructionsAsAStaticSegment()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        var settings = Read("settings.mcs.yml");

        Assert.Contains("segments:", settings, StringComparison.Ordinal);
        Assert.Contains("kind: StaticSegment", settings, StringComparison.Ordinal);
        Assert.Contains("value: \"Be helpful.\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_CliCopilot_UsesTheEscapedTokens()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        var settings = Read("settings.mcs.yml");

        Assert.Contains("displayName: \"Contoso Helpdesk\"", settings, StringComparison.Ordinal);
        Assert.Contains("schemaName: \"con_contosohelpdesk\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_ResolvesTheLayoutVersionFromTheSdk()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        Assert.Contains($"layoutVersion: {AgentClassifier.CurrentLayoutVersion}", Read("agent.sync.yaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("$workspaceLayoutVersion$", Read("agent.sync.yaml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Say \"hello\"\nthen stop.")]
    [InlineData("Escalate: always, #1 priority")]
    [InlineData("- leading dash")]
    [InlineData("back\\slash")]
    public void Scaffold_CliCopilot_EscapesInputThatWouldBreakTheDocument(string instructions)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command(instructions: instructions));

        var value = Read("settings.mcs.yml").Split('\n').Single(line => line.Contains("value:", StringComparison.Ordinal));

        Assert.StartsWith("          value: \"", value.TrimEnd('\r'), StringComparison.Ordinal);
        Assert.EndsWith("\"", value.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_WithoutInstructions_StillWritesAValidScalar()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command(instructions: string.Empty));

        Assert.Contains("value: \"\"", Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Say \"hello\"\nthen stop.")]
    [InlineData("Escalate: always, #1 priority")]
    [InlineData("- leading dash")]
    [InlineData("back\\slash")]
    public void Scaffold_Classic_EscapesInputThatWouldBreakTheDocument(string instructions)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic", instructions));

        var line = Read("agent.mcs.yml").Split('\n').Single(candidate => candidate.StartsWith("instructions:", StringComparison.Ordinal)).TrimEnd('\r');

        Assert.StartsWith("instructions: \"", line, StringComparison.Ordinal);
        Assert.EndsWith("\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Theory]
    [InlineData("Contoso: Helpdesk")]
    [InlineData("Contoso \"Helpdesk\"")]
    [InlineData("Contoso #1")]
    public void Scaffold_Classic_EscapesADisplayNameThatWouldBreakTheDocument(string displayName)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic") with { DisplayName = displayName });

        var line = Read("settings.mcs.yml").Split('\n').Single(candidate => candidate.StartsWith("displayName:", StringComparison.Ordinal)).TrimEnd('\r');

        Assert.Equal($"displayName: \"{displayName.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", line);
    }

    [Fact]
    public void Scaffold_Classic_WithoutInstructions_StillWritesAValidScalar()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic", instructions: string.Empty));

        Assert.Contains("instructions: \"\"", Read("agent.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_Classic_QuotesComputedSchemaNameValues()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic") with { SchemaName = "con_agent" });

        Assert.Contains("defaultSchemaName: \"con_agent.gpt.default\"", Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!agent")]
    [InlineData("{agent}")]
    [InlineData("con_a{x}!")]
    [InlineData("-agent")]
    public void Scaffold_Classic_QuotesASchemaNameThatWouldBreakTheDocument(string schemaName)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic") with { SchemaName = schemaName });

        var line = Read("settings.mcs.yml").Split('\n').Single(candidate => candidate.TrimStart().StartsWith("defaultSchemaName:", StringComparison.Ordinal)).TrimEnd('\r');

        Assert.EndsWith($"defaultSchemaName: \"{schemaName}.gpt.default\"", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!agent")]
    [InlineData("{agent}")]
    public void Scaffold_Classic_QuotesTopicDialogReferences(string schemaName)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic") with { SchemaName = schemaName });

        var line = Read("topics/Fallback.mcs.yml").Split('\n').Single(candidate => candidate.TrimStart().StartsWith("dialog:", StringComparison.Ordinal)).TrimEnd('\r');

        Assert.EndsWith($"dialog: \"{schemaName}.topic.Escalate\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_LeavesNoUnsubstitutedTokens()
    {
        foreach (var templateFolder in new[] { "cli-copilot", "classic" })
        {
            using var scope = new TemporaryDirectory();
            new EmbeddedTemplateScaffolder().Scaffold(Command(templateFolder) with { ProjectDirectory = scope.Path });

            foreach (var file in Directory.GetFiles(scope.Path, "*.yml", SearchOption.AllDirectories).Concat(Directory.GetFiles(scope.Path, "*.yaml", SearchOption.AllDirectories)))
            {
                Assert.DoesNotContain('$', File.ReadAllText(file));
            }
        }
    }

    [Fact]
    public void Scaffold_Classic_WritesTheFullTemplateWithNestedTopics()
    {
        var created = new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.Equal(16, created.Count);
        Assert.Contains("icon.png", created);
        Assert.Contains("agent.mcs.yml", created);
        Assert.Contains("settings.mcs.yml", created);
        Assert.Equal(13, created.Count(path => path.StartsWith("topics/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Scaffold_Classic_StripsTheSubstitutionPrefixFromNestedFiles()
    {
        var created = new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.DoesNotContain(created, path => path.Contains("template_", StringComparison.Ordinal));
        Assert.Contains("topics/Fallback.mcs.yml", created);
        Assert.Contains("topics/Greeting.mcs.yml", created);
        Assert.True(File.Exists(Path.Combine(_directory, "topics", "Fallback.mcs.yml")));
    }

    [Fact]
    public void Scaffold_Classic_SubstitutesNestedTopicTokens()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.Contains("con_contosohelpdesk", Read("topics/Fallback.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_Classic_EscapesUserSuppliedScalars()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        var agent = Read("agent.mcs.yml");

        Assert.Contains("componentName: \"Contoso Helpdesk\"", agent, StringComparison.Ordinal);
        Assert.Contains("instructions: \"Be helpful.\"", agent, StringComparison.Ordinal);
        Assert.Contains("displayName: \"Contoso Helpdesk\"", Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: GptComponentMetadata")]
    [InlineData("webBrowsing: true")]
    public void Scaffold_Classic_KeepsTheGptComponent(string expected)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.Contains(expected, Read("agent.mcs.yml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: GenerativeAIRecognizer")]
    [InlineData("template: default-2.1.0")]
    [InlineData("GenerativeActionsEnabled: true")]
    public void Scaffold_Classic_DeclaresTheClassicSettings(string expected)
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.Contains(expected, Read("settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Scaffold_Classic_CopiesTheIconVerbatim()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        var icon = File.ReadAllBytes(Path.Combine(_directory, "icon.png"));

        Assert.Equal(14577, icon.Length);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], icon[..4]);
    }

    [Fact]
    public void Scaffold_CliCopilot_ClassifiesAsACliAuthoredWorkspace()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command());

        Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(_directory));
        Assert.Equal(AuthoringShape.CliCopilot, AgentClassifier.DetectAuthoringShapeFromFolder(_directory));
    }

    [Fact]
    public void Scaffold_Classic_ClassifiesAsAClassicWorkspace()
    {
        new EmbeddedTemplateScaffolder().Scaffold(Command("classic"));

        Assert.Equal(AuthoringShape.Classic, AgentClassifier.DetectAuthoringShapeFromFolder(_directory));
    }

    [Fact]
    public void Scaffold_WithAnUnknownTemplate_IsRejected()
    {
        var error = Assert.Throws<CopilotStudioException>(
            () => new EmbeddedTemplateScaffolder().Scaffold(Command("does-not-exist")));

        Assert.Equal(CopilotErrorKinds.TemplateNotFound, error.ErrorKind);
        Assert.False(Directory.Exists(_directory));
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ah-copilot-tmp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
