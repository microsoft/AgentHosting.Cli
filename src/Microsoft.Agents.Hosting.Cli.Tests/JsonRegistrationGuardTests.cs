using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins the invariant behind baseline defect 3.1: a result type the CLI can return must resolve
/// through the JSON context, or <c>--json</c> throws *after* the command has already mutated state.
///
/// Scope is every <b>awaited</b> operation result, because those are the payloads the dispatcher
/// hands to the renderer. The one synchronous member, <c>ResolveDeploymentAgentType</c>, returns a
/// routing decision that is consumed in-process and never rendered.
/// </summary>
public sealed class JsonRegistrationGuardTests
{
    private static JsonSerializerContext Context =>
        (JsonSerializerContext)typeof(CliOutputRenderer).Assembly
            .GetType("Microsoft.Agents.Hosting.Cli.Rendering.CliJsonContext", throwOnError: true)!
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    public static TheoryData<Type> RenderableResultTypes
    {
        get
        {
            var data = new TheoryData<Type>();

            foreach (var type in OperationResultTypes())
            {
                data.Add(type);
            }

            return data;
        }
    }

    private static IEnumerable<Type> OperationResultTypes() => OperationSurface.ResultTypes();

    private static IEnumerable<Type> PartialReportTypes() => typeof(IAgentHostingOperations).Assembly
        .GetTypes()
        .Where(type => typeof(Exception).IsAssignableFrom(type))
        .Select(type => type.GetProperty("PartialReport")?.PropertyType)
        .Where(type => type is not null)
        .Select(type => type!)
        .Distinct();


    [Theory]
    [MemberData(nameof(RenderableResultTypes))]
    public void EveryOperationResultResolvesThroughTheJsonContext(Type resultType)
    {
        Assert.NotNull(Context.GetTypeInfo(resultType));
    }

    [Fact]
    public void NoOperationResultIsMissingFromTheJsonContext()
    {
        Assert.DoesNotContain(OperationResultTypes(), type => Context.GetTypeInfo(type) is null);
    }

    [Fact]
    public void PartialReportsCarriedByTypedFailuresResolve()
    {
        Assert.DoesNotContain(PartialReportTypes(), type => Context.GetTypeInfo(type) is null);
    }

    [Fact]
    public void EveryDeclaredRouteResultResolvesThroughTheJsonContext()
    {
        var unregistered = CommandReflector.Discover(typeof(EnvGroup).Assembly)
            .SelectMany(CommandTree.AllCommands)
            .SelectMany(command => command.Implementation.ResultTypes)
            .Distinct()
            .Where(type => Context.GetTypeInfo(type) is null)
            .ToArray();

        Assert.Empty(unregistered);
    }
}

