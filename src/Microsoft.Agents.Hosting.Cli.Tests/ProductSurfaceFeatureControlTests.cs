using Microsoft.Agents.Hosting.Cli.Commands.Commands.Agent;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Auth;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProductSurfaceFeatureControlTests
{
    private static readonly IFeatureFlags AllProductSurfacesDisabled =
        new TestFeatureFlags();

    [Fact]
    public async Task DirectApplicationDispatchRequiresOptIn()
    {
        foreach (var row in AuthProfileCommandHostTests.ApplicationOptions())
        {
            var value = (string?)row[2];
            var input = new DictionaryCommandInput(new Dictionary<string, string?>
            {
                [(string)row[0]] = value ?? "true",
            });
            var services = new SingleServiceProvider(typeof(IFeatureFlags), AllProductSurfacesDisabled);

            var failure = await Assert.ThrowsAsync<CommandUsageException>(() =>
                new AuthCreateCommand().ExecuteAsync(input, services, default));

            Assert.Contains("AppUserSupport", failure.Message);
            if (value is not null) Assert.DoesNotContain(value, failure.Message);
        }
    }

    [Fact]
    public void DisabledProductSurfaces_AreAbsentFromGroupAndOptionHelp()
    {
        var root = Run(new FakeOperations(), AllProductSurfacesDisabled, "--help");
        var agent = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "--help");
        var create = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "create", "--help");
        var deploy = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "deploy", "--help");
        var deploymentCreate = Run(new FakeOperations(), AllProductSurfacesDisabled, "deployment", "create", "--help");
        var deploymentUpdate = Run(new FakeOperations(), AllProductSurfacesDisabled, "deployment", "update", "--help");
        var deployment = Run(new FakeOperations(), AllProductSurfacesDisabled, "deployment", "--help");

        Assert.Equal(ExitCodes.Success, root.ExitCode);
        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "abs"));
        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "auth"));
        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "env"));
        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "identity"));

        Assert.DoesNotContain(Lines(agent.Stdout), line => StartsWithCommand(line, "package"));
        Assert.Contains(Lines(agent.Stdout), line => StartsWithCommand(line, "create"));
        Assert.Contains(Lines(agent.Stdout), line => StartsWithCommand(line, "init"));

        Assert.DoesNotContain("--language", create.Stdout, StringComparison.Ordinal);
        Assert.Contains("--initial-prompt", create.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeAgent", create.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--package-revision", deploy.Stdout, StringComparison.Ordinal);
        Assert.Contains("--publish", deploy.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("AMS", deploy.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--entra-id", deploymentCreate.Stdout, StringComparison.Ordinal);
        Assert.Contains("--environment-id", deploymentCreate.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Azure Bot Service", deploymentCreate.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--package-revision", deploymentUpdate.Stdout, StringComparison.Ordinal);
        Assert.Contains(Lines(deployment.Stdout), line => StartsWithCommand(line, "channel"));
    }

    [Fact]
    public void DisabledPureProductSurfaces_CannotBeInvokedDirectly()
    {
        var abs = Run(new FakeOperations(), AllProductSurfacesDisabled, "abs", "preview");
        var package = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "package", "upload");
        var identity = Run(new FakeOperations(), AllProductSurfacesDisabled, "identity", "create", "app");
        var defaultCreate = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "create");
        var channel = Run(
            new FakeOperations(),
            AllProductSurfacesDisabled,
            "deployment", "channel", "list",
            "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Usage, abs.ExitCode);
        Assert.Equal(ExitCodes.Usage, package.ExitCode);
        Assert.Equal(ExitCodes.Usage, identity.ExitCode);
        Assert.Equal(ExitCodes.Usage, defaultCreate.ExitCode);
        Assert.Contains(nameof(FeatureName.AmsFeatures), defaultCreate.Stderr, StringComparison.Ordinal);
        Assert.Contains("--agent-type MCSAgent", defaultCreate.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, channel.ExitCode);
    }

    [Fact]
    public void DisabledAmsFeature_DoesNotMaskAnUnknownAgentType()
    {
        var result = Run(
            new FakeOperations(),
            AllProductSurfacesDisabled,
            "agent", "create",
            "--agent-type", "NotAnAgent");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a known agent type", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledAmsFeature_CloningAnMcsAgentNeedsNoAgentType()
    {
        var operations = new FakeOperations();

        var result = Run(
            operations,
            AllProductSurfacesDisabled,
            "agent", "create",
            "-an", "NB1",
            "--mcs-clone-agent-id", "f054a54e-35c1-f111-aaad-000d3a367518",
            "--mcs-clone-environment-id", "d3e21992-3138-ec9e-855f-94ae510c3a1e");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(nameof(FeatureName.AmsFeatures), result.Stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastCreateRequest);
        var request = operations.LastManagedAgentCreateRequest!;
        Assert.Equal(AgentTypes.McsAgent, request.AgentType);
        Assert.Equal("NB1", request.Name);
        Assert.Equal("f054a54e-35c1-f111-aaad-000d3a367518", request.CloneAgentId);
        Assert.Equal("d3e21992-3138-ec9e-855f-94ae510c3a1e", request.CloneEnvironmentId);
    }

    [Theory]
    [InlineData("--mcs-clone-agent-id", "f054a54e-35c1-f111-aaad-000d3a367518")]
    [InlineData("-mcaid", "f054a54e-35c1-f111-aaad-000d3a367518")]
    [InlineData("--mcs-clone-agent-schemaname", "cr_agent")]
    [InlineData("-mcas", "cr_agent")]
    [InlineData("--mcs-clone-environment-id", "env-2")]
    [InlineData("-mceid", "env-2")]
    public void DisabledAmsFeature_EachCloneOptionSelectsAnMcsAgent(string option, string value)
    {
        var operations = new FakeOperations();

        var result = Run(operations, AllProductSurfacesDisabled, "agent", "create", option, value);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(AgentTypes.McsAgent, operations.LastManagedAgentCreateRequest?.AgentType);
        Assert.Null(operations.LastCreateRequest);
    }

    [Fact]
    public void DisabledAmsFeature_StillGatesACreateWithNoAgentTypeOrCloneOption()
    {
        var result = Run(new FakeOperations(), AllProductSurfacesDisabled, "agent", "create", "-an", "NB1", "--initial-prompt", "Help the user.");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(nameof(FeatureName.AmsFeatures), result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledProductSurfaces_PreserveMcsAndLocalRoutes()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.McsAgent,
                Source = DeploymentAgentTypeSource.DeploymentRecord,
            },
        };

        var create = Run(
            operations,
            AllProductSurfacesDisabled,
            "agent", "create",
            "--agent-type", "MCSAgent",
            "--agent-name", "MCS Agent",
            "--description", "description",
            "--initial-prompt", "Help the user.");
        var deploy = Run(
            operations,
            AllProductSurfacesDisabled,
            "agent", "deploy",
            "--agent-id", "agent-1",
            "--deployment-id", "deployment-1");
        var show = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "show",
            "--deployment-id", "deployment-1");
        var deploymentCreate = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "create",
            "--agent-id", "agent-1",
            "--deployment-name", "dev-slot",
            "--deployment-type", "dev");
        var update = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "update",
            "--deployment-id", "deployment-1",
            "--new-name", "renamed-slot");
        var delete = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "delete",
            "--deployment-id", "deployment-1");
        var list = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "list",
            "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Success, create.ExitCode);
        Assert.Equal(ExitCodes.Success, deploy.ExitCode);
        Assert.Equal(ExitCodes.Success, show.ExitCode);
        Assert.Equal(ExitCodes.Success, deploymentCreate.ExitCode);
        Assert.Equal(ExitCodes.Success, update.ExitCode);
        Assert.Equal(ExitCodes.Success, delete.ExitCode);
        Assert.Equal(ExitCodes.Success, list.ExitCode);
        Assert.NotNull(operations.LastManagedAgentCreateRequest);
        Assert.NotNull(operations.LastManagedAgentDeployRequest);
        Assert.NotNull(operations.LastAgentDeploymentShowRequest);
        Assert.NotNull(operations.LastAgentDeploymentCreateRequest);
        Assert.NotNull(operations.LastAgentDeploymentUpdateRequest);
        Assert.NotNull(operations.LastAgentDeploymentDeleteRequest);
        Assert.NotNull(operations.LastAgentDeploymentListRequest);
    }

    [Fact]
    public void DisabledAbsFeature_DoesNotMaskAnUnresolvedAgentOnCreate()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.CodeAgent,
                Source = DeploymentAgentTypeSource.Unresolved,
            },
        };

        var result = Run(
            operations,
            AllProductSurfacesDisabled,
            "deployment", "create",
            "--deployment-name", "dev",
            "--deployment-type", "dev");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("Option '--agent-id or --agent-name' is required.", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FeatureName.AzureBotServiceFeatures), result.Stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastDeploymentRequest);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void DisabledAbsFeature_RejectsResolvedCodeAgentRouteBeforeDispatch()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.CodeAgent,
                Source = DeploymentAgentTypeSource.DeploymentRecord,
            },
        };

        var result = Run(
            operations,
            new TestFeatureFlags(FeatureName.AmsFeatures),
            "deployment", "show",
            "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(nameof(FeatureName.AzureBotServiceFeatures), result.Stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastGetDeploymentRequest);
    }

    [Fact]
    public async Task DisabledAmsFeature_RejectsCodeAgentRouteBeforeHandlerResolution()
    {
        var input = new DictionaryCommandInput(new Dictionary<string, string?>
        {
            ["--agent-type"] = AgentTypes.CodeAgent,
            ["--agent-name"] = "Code Agent",
            ["--language"] = "dotnet",
            ["--language-version"] = "net10.0",
        });
        var services = new SingleServiceProvider(typeof(IFeatureFlags), AllProductSurfacesDisabled);

        var failure = await Assert.ThrowsAsync<CommandUsageException>(
            () => new AgentCreateCommand().ExecuteAsync(input, services, CancellationToken.None));

        Assert.Contains(nameof(FeatureName.AmsFeatures), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingAllProductSurfaces_RestoresExistingCommandsAndOptions()
    {
        var flags = TestFeatureFlags.AllProductSurfaces;

        var root = Run(new FakeOperations(), flags, "--help");
        var agent = Run(new FakeOperations(), flags, "agent", "--help");
        var create = Run(new FakeOperations(), flags, "agent", "create", "--help");
        var deploymentCreate = Run(new FakeOperations(), flags, "deployment", "create", "--help");

        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "abs"));
        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "identity"));
        Assert.Contains(Lines(agent.Stdout), line => StartsWithCommand(line, "package"));
        Assert.Contains("--language", create.Stdout, StringComparison.Ordinal);
        Assert.Contains("--entra-id", deploymentCreate.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingOnlyAms_RestoresEveryAmsOperationWithoutOtherSurfaces()
    {
        var operations = new FakeOperations();
        var flags = new TestFeatureFlags(FeatureName.AmsFeatures);
        var root = Run(operations, flags, "--help");
        var agent = Run(operations, flags, "agent", "--help");
        var createHelp = Run(operations, flags, "agent", "create", "--help");
        var deploymentUpdateHelp = Run(operations, flags, "deployment", "update", "--help");
        var package = Run(
            operations,
            flags,
            "agent", "package", "upload",
            "--agent-id", "agent-1",
            "--package", "agent.zip");
        var create = Run(
            operations,
            flags,
            "agent", "create",
            "--agent-name", "Code Agent",
            "--language", "dotnet",
            "--language-version", "net10.0");
        var deploy = Run(
            operations,
            flags,
            "agent", "deploy",
            "--agent-id", "agent-1",
            "--package-revision", "revision-1",
            "--target-environment-id", "env-1",
            "--deployment-name", "dev-slot",
            "--deployment-type", "dev",
            "--entra-id", "app-1",
            "--entra-id-type", "app-reg");

        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "abs"));
        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "identity"));
        Assert.Contains(Lines(agent.Stdout), line => StartsWithCommand(line, "package"));
        Assert.Contains("--language", createHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("--package-revision", deploymentUpdateHelp.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, package.ExitCode);
        Assert.Equal(ExitCodes.Success, create.ExitCode);
        Assert.Equal(ExitCodes.Success, deploy.ExitCode);
        Assert.NotNull(operations.LastPackageUploadRequest);
        Assert.NotNull(operations.LastCreateRequest);
        Assert.NotNull(operations.LastDeployRequest);
    }

    [Fact]
    public void EnablingOnlyAbs_RestoresAbsAndCodeAgentDeploymentRoutes()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.CodeAgent,
                Source = DeploymentAgentTypeSource.DeploymentRecord,
            },
        };
        var flags = new TestFeatureFlags(FeatureName.AzureBotServiceFeatures);
        var root = Run(operations, flags, "--help");
        var agent = Run(operations, flags, "agent", "--help");
        var deploymentHelp = Run(operations, flags, "deployment", "create", "--help");
        var deployment = Run(operations, flags, "deployment", "--help");
        var channel = Run(operations, flags, "deployment", "channel", "--help");
        var preview = Run(operations, flags, "abs", "preview");
        var show = Run(operations, flags, "deployment", "show", "--deployment-id", "deployment-1");

        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "abs"));
        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "identity"));
        Assert.DoesNotContain(Lines(agent.Stdout), line => StartsWithCommand(line, "package"));
        Assert.Contains("--entra-id", deploymentHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains(Lines(deployment.Stdout), line => StartsWithCommand(line, "channel"));
        Assert.Equal(ExitCodes.Success, channel.ExitCode);
        Assert.Contains(Lines(channel.Stdout), line => StartsWithCommand(line, "create"));
        Assert.Equal(ExitCodes.Success, preview.ExitCode);
        Assert.Equal(ExitCodes.Success, show.ExitCode);
        Assert.NotNull(operations.LastAbsRequest);
        Assert.NotNull(operations.LastGetDeploymentRequest);
    }

    [Fact]
    public void EnablingOnlyIdentity_RestoresUserFacingIdentityCommands()
    {
        var operations = new FakeOperations();
        var flags = new TestFeatureFlags(FeatureName.IdentityFeatures);
        var root = Run(operations, flags, "--help");
        var agent = Run(operations, flags, "agent", "--help");
        var create = Run(
            operations,
            flags,
            "identity", "create", "app",
            "--tenant", "tenant-1",
            "--display-name", "Agent Identity",
            "--auth-type", "ClientSecret",
            "--config-format", "dotnet");

        Assert.Contains(Lines(root.Stdout), line => StartsWithCommand(line, "identity"));
        Assert.DoesNotContain(Lines(root.Stdout), line => StartsWithCommand(line, "abs"));
        Assert.DoesNotContain(Lines(agent.Stdout), line => StartsWithCommand(line, "package"));
        Assert.Equal(ExitCodes.Success, create.ExitCode);
        Assert.NotNull(operations.LastAppRegistrationIdentityRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        IFeatureFlags featureFlags,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(operations, featureFlags: featureFlags);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static bool StartsWithCommand(string line, string command) =>
        line.TrimStart().StartsWith($"{command} ", StringComparison.Ordinal);

    private static string[] Lines(string value) => value.Split('\n');

    private sealed class DictionaryCommandInput(IReadOnlyDictionary<string, string?> values) : ICommandInput
    {
        public string? GetString(string name) => values.GetValueOrDefault(name);

        public bool GetSwitch(string name) => bool.TryParse(values.GetValueOrDefault(name), out var value) && value;

        public bool? GetOptionalSwitch(string name) => null;

        public int? GetInt(string name) => null;

        public string[] GetStrings(string name) => [];

        public string? GetArgument(string name) => null;

        public bool WasProvided(string name) => values.ContainsKey(name);

        public bool Json => false;

        public bool NonInteractive => false;
    }

    private sealed class SingleServiceProvider(Type serviceType, object service) : IServiceProvider
    {
        public object? GetService(Type requestedType) => requestedType == serviceType ? service : null;
    }
}
