using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentCopyValidationTests
{
    private const string CloudAgentId = "50505050-1111-2222-3333-444444444444";

    private const string CloudWorkflowId = "a1a1a1a1-1111-2222-3333-444444444444";

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Problem_WithoutAName_AsksForOne(string? name)
    {
        var problem = ManagedAgentCopyValidation.Problem(new ManagedAgentCopyRequest { Name = name, CloneAgentId = CloudAgentId, Project = "source-project" });

        Assert.Equal("`msagent agent copy` requires --agent-name: the copy needs a display name of its own.", problem?.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "NB1")]
    [InlineData(CloudAgentId, null)]
    [InlineData(CloudAgentId, "destination-project")]
    public void Problem_WithAName_IsNull(string? cloneAgentId, string? project)
    {
        Assert.Null(ManagedAgentCopyValidation.Problem(new ManagedAgentCopyRequest { Name = "Copy Agent", CloneAgentId = cloneAgentId, Project = project }));
    }

    [Fact]
    public void Problem_WithBothCloudIds_NamesOneCloudSource()
    {
        Assert.Equal("Pass one cloud source: --mcs-clone-agent-id for an agent or --mcs-clone-workflow-id for a workflow.", ManagedAgentCopyValidation.Problem(new ManagedAgentCopyRequest { Name = "Copy Agent", CloneAgentId = CloudAgentId, CloneWorkflowId = CloudWorkflowId })?.Message);
    }

    [Fact]
    public void Problem_WithACloudWorkflowAndASchemaName_RefusesTheSchemaName()
    {
        Assert.Equal("--schema-name names an agent's schema; a workflow has none. Remove --schema-name.", ManagedAgentCopyValidation.Problem(new ManagedAgentCopyRequest { Name = "Copy Flow", CloneWorkflowId = CloudWorkflowId, SchemaName = "con_copyflow" })?.Message);
    }

    [Fact]
    public void Problem_WithACloudWorkflowAlone_FindsNothing()
    {
        Assert.Null(ManagedAgentCopyValidation.Problem(new ManagedAgentCopyRequest { Name = "Copy Flow", CloneWorkflowId = CloudWorkflowId, EnvironmentId = "env-1", Project = "destination-project" }));
    }
}
