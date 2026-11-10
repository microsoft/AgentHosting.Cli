using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncWorkflowStateRulesTests
{
    private static readonly SyncWorkflowStateRules Rules = new();

    [Theory]
    [InlineData(CopilotStudioStates.Activated, CopilotStudioStates.Published)]
    [InlineData(CopilotStudioStates.Draft, CopilotStudioStates.Draft)]
    [InlineData(CopilotStudioStates.Suspended, CopilotStudioStates.Draft)]
    [InlineData(CopilotStudioStates.Unknown, CopilotStudioStates.Unknown)]
    public void ThePortalLabelIsDraftForEverythingObservedNotRunning(string state, string expected) =>
        Assert.Equal(expected, Rules.PortalLabel(state));

    [Theory]
    [InlineData(true, CopilotStudioStates.Activated)]
    [InlineData(false, CopilotStudioStates.Draft)]
    [InlineData(null, CopilotStudioStates.Unknown)]
    public void EachObservationIsNamedByTheLibraryAndReadsBackTheSame(bool? running, string expected)
    {
        Assert.Equal(expected, Rules.StateOf(running));
        Assert.Equal(running, Rules.RunningOf(expected));
    }

    [Fact]
    public void ASuspendedWorkflowIsNeitherRunningNorStopped() =>
        Assert.Null(Rules.RunningOf(CopilotStudioStates.Suspended));
}
