using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentCreateCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        IAgentHostingOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AmsFeatures));
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Create_BindsTheExplicitCloudRecordFields()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations,
            "agent", "create",
            "--agent-name", "My Agent",
            "--description", "description",
            "--environment-id", "env-2",
            "--language", "dotnet",
            "--language-version", "net10.0",
            "--idempotency-key", "retry-key-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentCreateRequest>(operations.LastCreateRequest);
        Assert.Equal("My Agent", request.DisplayName);
        Assert.Equal("description", request.Description);
        Assert.Equal("env-2", request.EnvironmentId);
        Assert.Equal("dotnet", request.Language);
        Assert.Equal("retry-key-1", request.IdempotencyKey);
    }

    [Fact]
    public void Create_WithoutEnvironmentOption_LeavesActiveSelectionFallbackUnspecified()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations,
            "agent", "create",
            "--agent-name", "My Agent",
            "--language", "dotnet",
            "--language-version", "net10.0");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastCreateRequest!.EnvironmentId);
    }

    [Fact]
    public void Create_WithoutRequiredOptions_IsAUsageErrorAndDispatchesNothing()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "create", "--agent-name", "My Agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
    }

    [Fact]
    public void Create_JsonEnvelope_ReportsOnlySafeServiceOwnedCoordinates()
    {
        var (exitCode, stdout, _) = Run(
            new FakeOperations(),
            "agent", "create",
            "--agent-name", "My Agent",
            "--language", "dotnet",
            "--language-version", "net10.0",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("agent-created", root.GetProperty("status").GetString());
        Assert.Equal("agent-1", root.GetProperty("agentId").GetString());
        Assert.Equal("repo-1", root.GetProperty("repositoryId").GetString());

        var names = root.EnumerateObject().Select(property => property.Name).ToList();
        Assert.DoesNotContain(names, name => name.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_HumanOutput_DoesNotClaimPublicationOrReadiness()
    {
        var (_, stdout, _) = Run(
            new FakeOperations(),
            "agent", "create",
            "--agent-name", "My Agent",
            "--language", "dotnet",
            "--language-version", "net10.0");

        Assert.Contains("Agent and service-owned repository created", stdout, StringComparison.Ordinal);
        Assert.Contains("does not report package publication or runtime readiness", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("running", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--identity-type")]
    [InlineData("--entra-object-id")]
    [InlineData("--endpoint")]
    [InlineData("--repository-id")]
    [InlineData("--storage-account")]
    public void Create_RejectsFieldsOutsideTheCreateContract(string option)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations,
            "agent", "create",
            "--agent-name", "My Agent",
            "--language", "dotnet",
            "--language-version", "net10.0",
            option, "caller-value");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
    }
}
