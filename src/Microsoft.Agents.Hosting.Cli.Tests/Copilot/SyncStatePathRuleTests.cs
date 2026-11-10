using Microsoft.Agents.Hosting.Cli.CopilotStudio;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class SyncStatePathRuleTests
{
    [Theory]
    [InlineData("ContosoHR/.mcs/botdefinition.json", true)]
    [InlineData(".mcs/conn.json", true)]
    [InlineData("ContosoHR/topics/.MCS/changetoken.txt", true)]
    [InlineData("ContosoHR/.mcsx/botdefinition.json", false)]
    [InlineData("ContosoHR/topics/Greeting.mcs.yml", false)]
    [InlineData("", false)]
    public void IsSyncStatePath_AnswersAsSyncDoes(string path, bool expected) => Assert.Equal(expected, new SyncStatePathRule().IsSyncStatePath(path));
}
