using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class WorkspaceConnectionVerifierTests
{
    private const string Organization = "https://contoso.crm.dynamics.com/";

    private static CopilotEnvironmentContext Environment(string environmentId = "env-1") => new()
    {
        EnvironmentId = environmentId,
        TenantId = "tenant-1",
        DataverseOrganization = new Uri(Organization),
    };

    private static CopilotWorkspaceInfo Workspace(string organization = Organization, string environmentId = "env-1", string agentId = "agent-1") => new()
    {
        WorkspacePath = "C:/workspace",
        AgentId = agentId,
        EnvironmentId = environmentId,
        DataverseOrganization = new Uri(organization),
    };

    [Fact]
    public void RequireOriginMatches_WithTheEnvironmentsOrganization_Passes()
    {
        WorkspaceConnectionVerifier.RequireOriginMatches(Workspace(), Environment());
    }

    [Fact]
    public void RequireOriginMatches_IgnoresHostCasing()
    {
        WorkspaceConnectionVerifier.RequireOriginMatches(Workspace("https://CONTOSO.crm.dynamics.com/"), Environment());
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("http://contoso.crm.dynamics.com/")]
    [InlineData("https://contoso.crm.dynamics.com:8443/")]
    public void RequireOriginMatches_WithAnotherOrigin_Fails(string organization)
    {
        var failure = Assert.Throws<CopilotStudioException>(
            () => WorkspaceConnectionVerifier.RequireOriginMatches(Workspace(organization), Environment()));

        Assert.Equal(CopilotErrorKinds.UntrustedDataverseOrigin, failure.ErrorKind);
    }

    [Fact]
    public void RequireBoundAgent_WhenTheyAgree_Passes()
    {
        WorkspaceConnectionVerifier.RequireBoundAgent(Workspace(agentId: "agent-1"), Environment(), "agent-1");
    }

    [Fact]
    public void RequireBoundAgent_IgnoresIdentifierCasing()
    {
        WorkspaceConnectionVerifier.RequireBoundAgent(Workspace(agentId: "AGENT-1"), Environment(), "agent-1");
    }

    [Fact]
    public void RequireBoundAgent_WithNoWorkspace_Passes()
    {
        WorkspaceConnectionVerifier.RequireBoundAgent(null, Environment(), "agent-1");
    }

    [Fact]
    public void RequireBoundAgent_InAnotherEnvironment_Passes()
    {
        WorkspaceConnectionVerifier.RequireBoundAgent(Workspace(environmentId: "env-elsewhere", agentId: "agent-1"), Environment(), "agent-2");
    }

    [Fact]
    public void RequireBoundAgent_WhenTheSameEnvironmentNamesAnotherAgent_Fails()
    {
        var failure = Assert.Throws<ManagedAgentException>(
            () => WorkspaceConnectionVerifier.RequireBoundAgent(Workspace(agentId: "agent-1"), Environment(), "agent-2"));

        Assert.Equal("workspace-identity-mismatch", failure.ErrorKind);
        Assert.Contains("agent-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("agent-2", failure.Message, StringComparison.Ordinal);
    }
}
