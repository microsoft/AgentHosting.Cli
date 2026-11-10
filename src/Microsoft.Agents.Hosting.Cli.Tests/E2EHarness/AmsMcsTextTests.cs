using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsTextTests
{
    private const string Held = "0123456789abcdef0123456789abcdef01234567";

    private const string Deployed = "89abcde0123456789abcdef0123456789abcdef0";

    private static readonly DateTimeOffset Earlier = new(2026, 11, 8, 5, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Earlier.AddMinutes(1);

    [Fact]
    public void ALabelledValueIsTheRestOfItsIndentedLineAndAWordIsItsFirstToken()
    {
        const string text = "Agent ahe2e Home A\r\n  RepositoryId   22222222-aaaa-4000-8000-00000000000b  (main)\r\n  folder  ahe2e Home A x  \n  gitUrl https://git.example.invalid/r extra\nfolder unindented\n";

        Assert.Equal("22222222-aaaa-4000-8000-00000000000b", AmsMcsText.Word(text, "repositoryId"));
        Assert.Equal("22222222-aaaa-4000-8000-00000000000b  (main)", AmsMcsText.Value(text, "repositoryId"));
        Assert.Equal("ahe2e Home A x", AmsMcsText.Value(text, "folder"));
        Assert.Equal("https://git.example.invalid/r", AmsMcsText.Word(text, "gitUrl"));
        Assert.Null(AmsMcsText.Value(text, "branch"));
        Assert.Null(AmsMcsText.Word("folder unindented", "folder"));
    }

    [Theory]
    [InlineData("kind: Bot\ndisplayName: ahe2e Portal x\n", "ahe2e Portal x")]
    [InlineData("displayName: \"ahe2e Portal x\"\r\n", "ahe2e Portal x")]
    [InlineData("displayName: 'ahe2e Portal x'", "ahe2e Portal x")]
    [InlineData("displayName:\t  ahe2e Portal x   \n", "ahe2e Portal x")]
    [InlineData("displayName: \"half'\n", "\"half'")]
    [InlineData("displayName: x Portal x\n", "x Portal x")]
    [InlineData("displayName: \"\n", "\"")]
    [InlineData("displayName:\n", "")]
    [InlineData("  displayName: nested\n", null)]
    [InlineData("name: other\n", null)]
    [InlineData(null, null)]
    public void TheDisplayNameIsTheTopLevelLineUnquoted(string? yaml, string? displayName)
    {
        Assert.Equal(displayName, AmsMcsText.DisplayName(yaml));
    }

    [Theory]
    [InlineData("workflowId: w-1\nname: 'ahe2e Renamed35 x'\nstatecode: 0\n", "ahe2e Renamed35 x")]
    [InlineData("name: ahe2e Flow x\r\n", "ahe2e Flow x")]
    [InlineData("displayName: other\n  name: nested\n", null)]
    [InlineData(null, null)]
    public void TheWorkflowNameIsTheTopLevelNameLineUnquoted(string? yaml, string? name)
    {
        Assert.Equal(name, AmsMcsText.WorkflowName(yaml));
    }

    [Fact]
    public void OnlyTheWarningsAboutAmsAreKept()
    {
        string[] about = ["AMS doesn't know deployment d-1", "the folder holds no git commit", "HEAD shares no commit with main", "AMS wasn't told about the deploy", "ams wasn't updated"];

        Assert.Equal(about, AmsMcsText.AmsWarnings([about[0], "a connection is unbound", .. about[1..]]));
        Assert.Empty(AmsMcsText.AmsWarnings(null));
    }

    [Theory]
    [InlineData("Clone it? [Y/n]", true)]
    [InlineData("Continue? [y/N] ", true)]
    [InlineData("nothing asked", false)]
    [InlineData("[Y/n", false)]
    public void AQuestionIsAYesNoPrompt(string text, bool asked)
    {
        Assert.Equal(asked, AmsMcsText.HasQuestion(text));
    }

    [Theory]
    [InlineData("Clone repository r-1 into C:\\work\\x and add agent 'ahe2e Decline x' to it? [Y/n]", "ahe2e Decline x", true)]
    [InlineData("clone REPOSITORY r-1 INTO c:\\x AND add agent 'AHE2E decline X' TO IT? [y/N]", "ahe2e Decline x", true)]
    [InlineData("Clone repository r-1 into C:\\work\\x and add agent 'other' to it? [Y/n]", "ahe2e Decline x", false)]
    [InlineData("Clone repository r-1 into C:\\work\\x and add agent 'axb' to it? [Y/n]", "a.b", false)]
    [InlineData("Add agent 'ahe2e Decline x'? [Y/n]", "ahe2e Decline x", false)]
    public void TheCloneQuestionNamesTheAgent(string text, string agentName, bool asks)
    {
        Assert.Equal(asks, AmsMcsText.AsksToClone(text, agentName));
    }

    [Fact]
    public void AgentShowsDetailsAreFoundByAgentIdWithoutCase()
    {
        var details = new AmsAgentDetails("AGENT-1", "repository-1", "Folder A", "https://git.example.invalid/r", [new AmsDeploymentState("B-2", null, null), new AmsDeploymentState("a-1", "abc1234", "Deployed")]);
        var shown = new ManagedAgentShowResult { ProjectDirectory = "project", ConfigPath = "config", Ams = [new AmsAgentDetails("agent-2", "repository-1", "Folder B", null, []), details] };

        Assert.Same(details, AmsMcsResults.Details(shown, "agent-1"));
        Assert.Null(AmsMcsResults.Details(shown, "agent-3"));
        Assert.Null(AmsMcsResults.Details(shown with { Ams = null }, "agent-1"));
        Assert.Equal(["a-1", "b-2"], AmsMcsResults.DeploymentIds(details));
        Assert.Empty(AmsMcsResults.DeploymentIds(null));
    }

    [Fact]
    public void ASlotIsFoundByItsDeploymentIdWithoutCase()
    {
        var slot = new AgentDeploymentListItem { DeploymentId = "D-1", DeploymentName = "development", DeploymentType = "dev", State = "Deployed" };
        var listed = new AgentDeploymentListResult { Slots = [new AgentDeploymentListItem { DeploymentId = "d-2", DeploymentName = "test-ring", DeploymentType = "test" }, slot] };

        Assert.Same(slot, AmsMcsResults.Slot(listed, "d-1"));
        Assert.Null(AmsMcsResults.Slot(listed, "d-3"));
    }

    [Fact]
    public void AnAgentListIsFilteredByAgentAndFolderOrByName()
    {
        ManagedAgentListItem[] rows = [Row("AGENT-1", "Folder A", "ahe2e Home A"), Row("agent-1", "Folder X", "ahe2e Home A"), Row("agent-2", "Folder B", "ahe2e Home B")];
        var listed = new ManagedAgentListResult { Scope = "environment", Agents = rows };

        Assert.Equal([rows[0], rows[1]], AmsMcsResults.Holding(listed, "agent-1"));
        Assert.Equal([rows[0]], AmsMcsResults.Holding(listed, "agent-1", "folder a"));
        Assert.Empty(AmsMcsResults.Holding(listed, null));
        Assert.Equal([rows[2]], AmsMcsResults.Named(listed, "AHE2E home b"));
        Assert.Empty(AmsMcsResults.Named(listed, "ahe2e Home"));
    }

    [Fact]
    public void ATimeIsLaterOnlyWhenItExistsAndFollowsAnyEarlierOne()
    {
        Assert.True(AmsMcsResults.Later(Later, Earlier));
        Assert.True(AmsMcsResults.Later(Earlier, null));
        Assert.False(AmsMcsResults.Later(Earlier, Earlier));
        Assert.False(AmsMcsResults.Later(Earlier, Later));
        Assert.False(AmsMcsResults.Later(null, Earlier));
    }

    [Fact]
    public void ASuccessfulResultGivesItsIdsAndRepository()
    {
        const string gitUrl = "https://git.example.invalid/r";

        var outcome = MsagentOutcome.Read(Result($$$"""{"success":true,"status":"agent-created","agentId":"agent-1","displayName":"ahe2e Home A","schemaName":"cr123_ahe2eHomeA","ams":{"repositoryId":"repository-1","gitUrl":"{{{gitUrl}}}","repositoryCreated":true,"folder":"ahe2e Home A"}}"""));

        Assert.True(outcome.Success);
        Assert.Equal("agent-created", outcome.Status);
        Assert.Equal("agent-1", outcome.AgentId);
        Assert.Equal("ahe2e Home A", outcome.DisplayName);
        Assert.Equal("cr123_ahe2eHomeA", outcome.SchemaName);
        Assert.Equal(new MsagentRepositoryOutcome("repository-1", gitUrl, true), outcome.Ams);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public void AFailedResultGivesItsCodeMessageAndRemediation()
    {
        var outcome = MsagentOutcome.Read(Result("""{"success":false,"exitCode":2,"errorCode":99002,"errorMessage":"Unrecognized command or argument 'capture'.","remediation":"Run msagent agent --help."}""", exitCode: 2));

        Assert.False(outcome.Success);
        Assert.Equal(99002, outcome.ErrorCode);
        Assert.Equal("Unrecognized command or argument 'capture'. Run msagent agent --help.", outcome.FailureText);
        Assert.Null(outcome.Ams);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("Unhandled exception.")]
    public void AnUnreadableResultKeepsItsOutputAsTheMessage(string stdout)
    {
        var outcome = MsagentOutcome.Read(new MsagentResult(["agent", "create"], 1, stdout, "  fatal: no JSON\n", TimeSpan.Zero));

        Assert.False(outcome.Success);
        Assert.Null(outcome.ErrorCode);
        Assert.Equal((stdout + "  fatal: no JSON\n").Trim(), outcome.ErrorMessage);
        Assert.Equal($"{(stdout + "  fatal: no JSON\n").Trim()} ", outcome.FailureText);
    }

    [Theory]
    [InlineData("kind: Bot\ndisplayName: ahe2e Old x\nname: kept\n", "kind: Bot\ndisplayName: ahe2e New x\nname: kept\n")]
    [InlineData("displayName: \"ahe2e Old x\"\r\nname: kept\r\n", "displayName: \"ahe2e New x\"\r\nname: kept\r\n")]
    [InlineData("displayName: 'ahe2e Old x'", "displayName: 'ahe2e New x'")]
    [InlineData("displayName: \"half'\n", "displayName: ahe2e New x\n")]
    [InlineData("displayName:\n", "displayName: ahe2e New x\n")]
    [InlineData("  displayName: nested\n", "  displayName: nested\n")]
    public void ANewDisplayNameReplacesTheTopLevelOneAndKeepsItsQuotes(string yaml, string renamed)
    {
        Assert.Equal(renamed, AmsMcsText.WithDisplayName(yaml, "ahe2e New x"));
    }

    [Theory]
    [InlineData("mcs.metadata:\n  componentName: Greeting\n  description: ahe2e Dev edit x\nkind: AdaptiveDialog\n", "ahe2e Dev edit x")]
    [InlineData("mcs.metadata:\r\n  description: 'ahe2e Dev edit x'\r\n", "ahe2e Dev edit x")]
    [InlineData("description: top level\n", null)]
    [InlineData("    description: nested\n", null)]
    [InlineData(null, null)]
    public void TheDescriptionIsTheMetadataLineUnquoted(string? yaml, string? description)
    {
        Assert.Equal(description, AmsMcsText.Description(yaml));
    }

    [Theory]
    [InlineData("mcs.metadata:\n  componentName: Greeting\n  description: Greets.\nkind: AdaptiveDialog\n  description: kept\n", "mcs.metadata:\n  componentName: Greeting\n  description: ahe2e New x\nkind: AdaptiveDialog\n  description: kept\n")]
    [InlineData("mcs.metadata:\r\n  description: \"Greets.\"\r\n", "mcs.metadata:\r\n  description: \"ahe2e New x\"\r\n")]
    [InlineData("description: top level\n", "description: top level\n")]
    public void ANewDescriptionReplacesTheFirstMetadataOneAndKeepsItsQuotes(string yaml, string described)
    {
        Assert.Equal(described, AmsMcsText.WithDescription(yaml, "ahe2e New x"));
    }

    [Theory]
    [InlineData("a\n<<<<<<< ours\n  description: one\n=======\n  description: two\n>>>>>>> theirs\nb\n", true)]
    [InlineData("<<<<<<< ours\r\nx\r\n=======\r\ny\r\n>>>>>>> theirs\r\n", true)]
    [InlineData("=======\n<<<<<<< ours\n>>>>>>> theirs\n", false)]
    [InlineData("  <<<<<<< ours\n=======\n>>>>>>> theirs\n", false)]
    [InlineData(null, false)]
    public void ConflictMarkersAreALeftAMiddleAndARightMarkerInOrderAtTheStartOfTheirLines(string? text, bool marked)
    {
        Assert.Equal(marked, AmsMcsText.HasConflictMarkers(text));
    }

    [Theory]
    [InlineData("Clone repository ahe2e Home (r-1.x) into C:\\work\\mate, restore its agents and connect them? [Y/n]", "r-1.x", true)]
    [InlineData("clone REPOSITORY r (R-1.X) into c:\\m, RESTORE ITS AGENTS AND CONNECT THEM? [y/N]", "r-1.x", true)]
    [InlineData("Clone repository ahe2e Home (r-1ax) into C:\\work\\mate, restore its agents and connect them? [Y/n]", "r-1.x", false)]
    [InlineData("Clone repository ahe2e Home (r-2) into C:\\work\\mate, restore its agents and connect them? [Y/n]", "r-1.x", false)]
    [InlineData("Clone repository ahe2e Home (r-1.x) into C:\\work\\mate and restore its agents? [Y/n]", "r-1.x", false)]
    [InlineData("Clone repository ahe2e Home (r-1.x) into C:\\work\\mate and add agent 'a' to it? [Y/n]", "r-1.x", false)]
    public void TheRestoreQuestionNamesTheRepository(string text, string repositoryId, bool asks)
    {
        Assert.Equal(asks, AmsMcsText.AsksToRestore(text, repositoryId));
    }

    [Theory]
    [InlineData("Run git pull first.", "GIT PULL", true)]
    [InlineData("Run git pull first.", "git push", false)]
    [InlineData(null, "git pull", false)]
    public void ATextSaysAPhraseWithoutCase(string? text, string phrase, bool says)
    {
        Assert.Equal(says, AmsMcsText.Says(text, phrase));
    }

    [Fact]
    public void LinesMentionAPhraseWhenAnyOfThemSaysIt()
    {
        Assert.True(AmsMcsText.Mentions(["first", "the folder HOLDS NO GIT COMMIT"], "holds no git commit"));
        Assert.False(AmsMcsText.Mentions(["first", "second"], "holds no git commit"));
        Assert.False(AmsMcsText.Mentions(null, "holds no git commit"));
    }

    [Theory]
    [InlineData("deployed 0123456 to Dev", Held, true)]
    [InlineData("deployed 0123456789ABCDEF to Dev", Held, true)]
    [InlineData("deployed x0123456, then", Held, true)]
    [InlineData("deployed 0123457 to Dev", Held, false)]
    [InlineData("deployed 012345 to Dev", Held, false)]
    [InlineData("deployed a0123456 to Dev", Held, false)]
    [InlineData("deployed 0123456 to Dev", "0123456", false)]
    [InlineData(null, Held, false)]
    public void ATextNamesACommitByAnyLengthOfItsShortId(string? text, string? sha, bool names)
    {
        Assert.Equal(names, AmsMcsText.NamesCommit(text, sha));
    }

    [Theory]
    [InlineData("The folder holds 0123456, not the deployed 89abcde, so msagent left it alone.", AmsNoticeReason.OtherCommit, Held, Deployed, true)]
    [InlineData("The folder holds 89abcde, not the deployed 0123456.", AmsNoticeReason.OtherCommit, Held, Deployed, false)]
    [InlineData("The folder holds 0123456, not the deployed 0123456.", AmsNoticeReason.OtherCommit, Held, Deployed, false)]
    [InlineData("The folder has uncommitted changes.", AmsNoticeReason.OtherCommit, Held, Deployed, false)]
    [InlineData(null, AmsNoticeReason.OtherCommit, Held, Deployed, false)]
    [InlineData("The agent's folder HAS UNCOMMITTED CHANGES, so msagent left it alone.", AmsNoticeReason.Uncommitted, null, null, true)]
    [InlineData("The folder holds commits that aren't pushed to GRS.", AmsNoticeReason.Uncommitted, null, null, false)]
    [InlineData("The folder holds commits that aren't pushed to GRS.", AmsNoticeReason.Unpushed, null, null, true)]
    [InlineData("The folder has uncommitted changes.", AmsNoticeReason.Unpushed, null, null, false)]
    [InlineData("Git can't tell which commit the agent's folder holds.", AmsNoticeReason.GitCannotTell, null, null, true)]
    [InlineData("The folder has uncommitted changes.", AmsNoticeReason.GitCannotTell, null, null, false)]
    [InlineData(null, AmsNoticeReason.GitCannotTell, null, null, false)]
    public void ANoticeExplainsEachReasonTheFolderWasLeftAlone(string? notice, AmsNoticeReason reason, string? held, string? deployed, bool explains)
    {
        Assert.Equal(explains, AmsMcsText.Explains(notice, reason, held, deployed));
    }

    [Theory]
    [InlineData("msagent left the folder's connection to Dev as it was; `msagent agent pull` brings the deployed changes into it.", true, true)]
    [InlineData("msagent left the folder's connection to Dev as it was; `msagent agent pull` brings the deployed changes into it.", false, false)]
    [InlineData("So msagent didn't connect the folder to Dev.", false, true)]
    [InlineData("So msagent didn't connect the folder to Dev.", true, false)]
    [InlineData(null, false, false)]
    public void ANoticeAdvisesPullForAConnectedFolderAndSaysNothingConnectedOtherwise(string? notice, bool connected, bool advises)
    {
        Assert.Equal(advises, AmsMcsText.AdvisesPull(notice, connected));
    }

    [Theory]
    [InlineData("--remote deploys 0123456 from GRS; your local changes aren't in it.", Held, true)]
    [InlineData("--REMOTE DEPLOYS 0123456 FROM GRS; YOUR LOCAL CHANGES AREN'T IN IT.", Held, true)]
    [InlineData("--remote deploys 0123456 from GRS; your local changes aren't in it.", Deployed, false)]
    [InlineData("--remote deploys 0123456 from GRS; your local changes aren't in it. More.", Held, false)]
    [InlineData("--remote deploys 012345 from GRS; your local changes aren't in it.", "012345", false)]
    [InlineData("--remote deploys 0123456 from GRS; your local changes aren't in it.", null, false)]
    public void TheLocalChangesWarningNamesTheDeployedCommitExactly(string warning, string? deployed, bool warns)
    {
        Assert.Equal(warns, AmsMcsText.WarnsLocalChanges(["an unrelated warning", warning], deployed));
    }

    [Fact]
    public void NoWarningsWarnOfNoLocalChanges()
    {
        Assert.False(AmsMcsText.WarnsLocalChanges(null, Held));
    }

    [Theory]
    [InlineData("Nothing was deployed: Dev had edits that commit 0123456 doesn't hold. They were saved on main as commit 89abcde01.", "89abcde01")]
    [InlineData("nothing was deployed: dev had edits that commit 0123456 doesn't hold. they were saved on main as commit ABCDEF0.", "ABCDEF0")]
    [InlineData("Nothing was deployed: Dev had edits. They were saved on main.", null)]
    [InlineData(null, null)]
    public void TheEditsSavedOnMainAreNamedByTheirCommit(string? message, string? saved)
    {
        Assert.Equal(saved, AmsMcsText.SavedOnMain(message));
    }

    [Theory]
    [InlineData("dep-b", "2222222")]
    [InlineData("DEP-A", "1111111")]
    [InlineData("dep-c", null)]
    [InlineData("dep-z", null)]
    public void TheLastCommitOfADeploymentIsTheFirstOneListedAfterItsHeader(string deploymentId, string? lastCommit)
    {
        const string text = "Agent ahe2e Home A\r\n  lastCommit  0000000\r\nDeployments\r\n  development (dep-a)\r\n    state       Deployed\r\n    lastCommit  1111111\r\n  test-ring (dep-b)  \r\n    lastCommit  2222222\r\n  production (dep-z)\r\n";

        Assert.Equal(lastCommit, AmsMcsText.LastCommit(text, deploymentId));
    }

    [Theory]
    [InlineData("Run `msagent agent capture`, then `git pull`.", true, false)]
    [InlineData("The workspace is not connected to Copilot Studio. Run msagent agent pull.", false, true)]
    [InlineData("The workspace is not connected to Copilot Studio; run agent capture.", false, false)]
    [InlineData(null, false, false)]
    public void TheRemedyNamesCaptureAndGitPullWithAmsOnAndMainsTextOtherwise(string? text, bool amsRemedy, bool mainRemedy)
    {
        Assert.Equal((amsRemedy, mainRemedy), (AmsMcsText.NamesAmsRemedy(text), AmsMcsText.NamesMainRemedy(text)));
    }

    private static MsagentResult Result(string stdout, int exitCode = 0) => new(["agent", "create"], exitCode, stdout, string.Empty, TimeSpan.Zero);

    private static ManagedAgentListItem Row(string agentId, string folder, string displayName) => new() { AgentId = agentId, AgentFolder = folder, DisplayName = displayName, TenantId = AmsMcsTestKit.Tenant, EnvironmentId = AmsMcsTestKit.Dev, AgentType = "MCSAgent" };
}
