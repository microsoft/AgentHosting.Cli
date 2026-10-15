using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class AgentShareCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("--share-with-org", true)]
    [InlineData("--share-with-org true", true)]
    [InlineData("--share-with-org false", false)]
    [InlineData("--share-with-org:true", true)]
    [InlineData("--share-with-org:false", false)]
    public void Share_BindsTheOrgWideSwitchInEverySpelling(string spelling, bool expected)
    {
        var operations = new FakeOperations();

        Run(operations, ["agent", "share", "-aid", "a1", .. spelling.Split(' ')]);

        Assert.Equal(expected, operations.LastManagedAgentShareRequest?.ShareWithOrg);
    }

    [Fact]
    public void Share_LeavesTheOrgWideSwitchUnsetWhenItIsAbsent()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "share", "-aid", "a1", "-u", "ada@contoso.com", "-rl", "use");

        Assert.Null(operations.LastManagedAgentShareRequest?.ShareWithOrg);
    }

    [Fact]
    public void Share_BindsEveryOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations,
            "agent",
            "share",
            "--project", "C:\\src\\agent",
            "--agent-id", "agent-9",
            "--agent-name", "Contoso",
            "--user", "ada@contoso.com",
            "--role", "edit");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("C:\\src\\agent", operations.LastManagedAgentShareRequest?.Project);
        Assert.Equal("agent-9", operations.LastManagedAgentShareRequest?.AgentId);
        Assert.Equal("Contoso", operations.LastManagedAgentShareRequest?.AgentName);
        Assert.Equal("ada@contoso.com", operations.LastManagedAgentShareRequest?.User);
        Assert.Equal("edit", operations.LastManagedAgentShareRequest?.Role);
    }

    [Fact]
    public void Share_BindsShortAliases()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "share", "-aid", "agent-9", "-u", "ada@contoso.com", "-rl", "use");

        Assert.Equal("agent-9", operations.LastManagedAgentShareRequest?.AgentId);
        Assert.Equal("ada@contoso.com", operations.LastManagedAgentShareRequest?.User);
        Assert.Equal("use", operations.LastManagedAgentShareRequest?.Role);
    }

    [Fact]
    public void ShareShow_RoutesUnderShareAndBindsItsSelectors()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "share", "show", "-aid", "agent-9");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastManagedAgentShareRequest);
        Assert.Equal("agent-9", operations.LastManagedAgentShareShowRequest?.AgentId);
    }

    [Theory]
    [InlineData(new[] { "agent", "share", "-aid", "a1", "--role", "use" }, "--user is required.")]
    [InlineData(new[] { "agent", "share", "-aid", "a1", "-u", "a@b.com", "--role", "viewer" }, "is not a share role. Valid roles are: use, edit, view.")]
    [InlineData(new[] { "agent", "share", "-aid", "a1", "--share-with-org", "--role", "edit" }, "applies to --user.")]
    [InlineData(new[] { "agent", "share", "-aid", "a1", "--share-with-org", "--role", "view" }, "applies to --user.")]
    [InlineData(new[] { "agent", "share", "-aid", "a1", "--share-with-org", "--role", "edti" }, "applies to --user.")]
    [InlineData(new[] { "agent", "unshare", "-aid", "a1" }, "--user is required.")]
    [InlineData(new[] { "agent", "share", "-u", "a@b.com", "--role", "use" }, "--agent-id or --agent-name is required.")]
    [InlineData(new[] { "agent", "share", "show" }, "--agent-id or --agent-name is required.")]
    [InlineData(new[] { "agent", "unshare", "-u", "a@b.com" }, "--agent-id or --agent-name is required.")]
    public void EveryBrokenSelectorCombinationIsAUsageError(string[] args, string expected)
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), args);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(expected, stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("use")]
    [InlineData(null)]
    public void OrgWideSharingTakesNoRoleBeyondTheUseAccessItAlreadyGrants(string? role)
    {
        var operations = new FakeOperations();
        string[] args = role is null
            ? ["agent", "share", "-aid", "a1", "--share-with-org"]
            : ["agent", "share", "-aid", "a1", "--share-with-org", "--role", role];

        var (exitCode, _, _) = Run(operations, args);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void Share_RendersHumanOutputWithTheLinkCaveat()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentShare = _ => new ManagedAgentShareResult
            {
                Status = "shared",
                AgentId = "agent-1",
                DisplayName = "Contoso Agent",
                McsAgentId = "bot-1",
                EnvironmentId = "env-1",
                Principal = new ManagedAgentShareEntry { ObjectId = "user-1", PrincipalName = "Ada Lovelace", PrincipalType = "User", Role = "edit" },
                Role = "edit",
                Changed = true,
                PortalUrl = "https://copilotstudio.microsoft.com/environments/env-1/agents/bot-1",
            },
        };

        var (exitCode, stdout, _) = Run(operations, "agent", "share", "-aid", "a1", "-u", "ada@contoso.com", "-rl", "edit");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Shared Contoso Agent with Ada Lovelace as 'edit'.", stdout, StringComparison.Ordinal);
                Assert.Contains("https://copilotstudio.microsoft.com/environments/env-1/agents/bot-1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_RendersJson()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "share", "-aid", "a1", "-u", "ada@contoso.com", "-rl", "use", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal("shared", root.GetProperty("status").GetString());
        Assert.Equal("bot-1", root.GetProperty("mcsAgentId").GetString());
        Assert.Equal("use", root.GetProperty("role").GetString());
        Assert.Equal("Ada Lovelace", root.GetProperty("principal").GetProperty("principalName").GetString());
    }

    [Fact]
    public void ShareShow_RendersAnEmptyGridRatherThanFailing()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "share", "show", "-aid", "a1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("is not shared with anyone", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareShow_DoesNotClaimNobodyHasAccessWhileTheWholeOrganizationDoes()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentShareShow = _ => new ManagedAgentShareShowResult
            {
                AgentId = "agent-1",
                DisplayName = "Contoso Agent",
                McsAgentId = "bot-1",
                EnvironmentId = "env-1",
                ShareWithOrg = true,
            },
        };

        var (_, stdout, _) = Run(operations, "agent", "share", "show", "-aid", "a1");

        Assert.Contains("is not shared with anyone by name.", stdout, StringComparison.Ordinal);
        Assert.Contains("Everyone in the organization can use this agent.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Unshare_SaysOrgWideAccessSurvivesTheRemoval()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentUnshare = _ => new ManagedAgentUnshareResult
            {
                Status = "unshared",
                AgentId = "agent-1",
                DisplayName = "Contoso Agent",
                McsAgentId = "bot-1",
                EnvironmentId = "env-1",
                Principal = new ManagedAgentShareEntry { ObjectId = "user-1", PrincipalName = "Ada Lovelace", PrincipalType = "User", Role = "use" },
                ShareWithOrg = true,
                Changed = true,
            },
        };

        var (_, stdout, _) = Run(operations, "agent", "unshare", "-aid", "a1", "-u", "ada@contoso.com");

        Assert.Contains("Removed the direct share of Contoso Agent (agent-1) with Ada Lovelace.", stdout, StringComparison.Ordinal);
        Assert.Contains("Everyone in the organization can still use this agent", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareShow_RendersJsonWithItsShares()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentShareShow = _ => new ManagedAgentShareShowResult
            {
                AgentId = "agent-1",
                DisplayName = "Contoso Agent",
                McsAgentId = "bot-1",
                EnvironmentId = "env-1",
                Shares = [new ManagedAgentShareEntry { ObjectId = "user-1", PrincipalName = "Ada Lovelace", PrincipalType = "User", Role = "use" }],
            },
        };

        var (_, stdout, _) = Run(operations, "agent", "share", "show", "-aid", "a1", "--json");

        using var document = JsonDocument.Parse(stdout);

        Assert.Equal("shares-shown", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("use", document.RootElement.GetProperty("shares")[0].GetProperty("role").GetString());
    }

    [Fact]
    public void Unshare_RendersJson()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "agent", "unshare", "-aid", "a1", "-u", "ada@contoso.com", "--json");

        using var document = JsonDocument.Parse(stdout);

        Assert.Equal("unshared", document.RootElement.GetProperty("status").GetString());
    }

}
