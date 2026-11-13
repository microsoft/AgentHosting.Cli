using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins that every result has an explicit human-rendering contract. Most results render themselves;
/// deployment results are data-only and use the shared renderer boundary.
/// </summary>
public sealed class TextRenderingGuardTests
{
    public static TheoryData<Type> RenderableResultTypes
    {
        get
        {
            var data = new TheoryData<Type>();

            foreach (var type in RenderedResultTypes())
            {
                data.Add(type);
            }

            return data;
        }
    }

    private static IEnumerable<Type> OperationResultTypes() => OperationSurface.ResultTypes();

    private static IEnumerable<Type> DeclaredRouteResultTypes() => CommandReflector.Discover(typeof(EnvGroup).Assembly)
        .SelectMany(CommandTree.AllCommands)
        .SelectMany(command => command.Implementation.ResultTypes)
        .Distinct();

    private static IEnumerable<Type> RenderedResultTypes() => OperationResultTypes().Concat(DeclaredRouteResultTypes()).Distinct();

    [Theory]
    [MemberData(nameof(RenderableResultTypes))]
    public void EveryOperationResultHasAnExplicitRenderer(Type resultType)
    {
        Assert.True(IsRenderSupported(resultType), $"{resultType.Name} must have an explicit renderer.");
    }

    [Fact]
    public void NoOperationResultFallsBackToToString()
    {
        Assert.DoesNotContain(OperationResultTypes(), type => !IsRenderSupported(type));
    }

    [Fact]
    public void NoDeclaredRouteResultFallsBackToToString()
    {
        Assert.DoesNotContain(DeclaredRouteResultTypes(), type => !IsRenderSupported(type));
    }

    [Fact]
    public void TheRendererHasOnlyExplicitDeploymentBranches()
    {
        var textMethods = typeof(Microsoft.Agents.Hosting.Cli.Rendering.CliOutputRenderer)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(IReadOnlyList<string>))
            .Select(method => method.Name);

        Assert.Equal(["DescribeHuman"], textMethods);
    }

    private static bool IsRenderSupported(Type type) =>
        typeof(ITextGridRenderable).IsAssignableFrom(type)
        || typeof(IDeploymentResult).IsAssignableFrom(type)
        || type == typeof(AuthProfileListResult)
        || type == typeof(AuthProfileOperationResult)
        || type == typeof(AuthWhoResult)
        || type.IsArray;
}
