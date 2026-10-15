using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

/// <summary>
/// Pins which deploy warnings the activation pass's counts replace. Only a state claim is restated by
/// a count; a service error is not, so it has to survive.
/// </summary>
public sealed class DeployWarningTextTests
{
    [Fact]
    public void AStateClaimIsRestatedByTheCounts()
    {
        Assert.True(DeployWarningText.IsRestatedByWorkflowCounts(DeployWarningText.WorkflowNotRunning("Escalate")));
    }

    [Fact]
    public void AServiceErrorIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts(
            DeployWarningText.WorkflowNotActivated("Escalate", "Dataverse request failed (400).")));
    }

    [Fact]
    public void AWarningAboutSomethingElseIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts("AI prompt 'Summarize' was not saved: nope."));
    }

    [Fact]
    public void ANullWarningIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => DeployWarningText.IsRestatedByWorkflowCounts(null!));
    }

    [Fact]
    public void AConnectionReferenceCollisionIsNotRestatedByTheCounts()
    {
        Assert.False(DeployWarningText.IsRestatedByWorkflowCounts(
            "A connection reference this push declares already exists in env-1."));
    }
}
