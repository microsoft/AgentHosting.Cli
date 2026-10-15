using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins the shape that decides which constructor dependency injection picks.
///
/// <para>The facade carries two constructors: the one embedders were already compiled against, and
/// one that also takes the evaluation stack. Container constructor selection resolves this by taking
/// the longest constructor whose parameters it can all supply, and it is unambiguous only while one
/// parameter list is a prefix of the other. Were that to stop holding, the container could silently
/// choose the shorter one and every evaluation command would fail at runtime with a configuration
/// error, which no compile-time check would catch.</para>
/// </summary>
public sealed class OperationsConstructorSelectionTests
{
    private static ConstructorInfo[] Constructors => [.. typeof(AgentHostingOperations)
        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(constructor => constructor.GetParameters().Length)];

    [Fact]
    public void TheFacadeOffersTheLegacyConstructorAndOneCarryingEvaluations()
    {
        Assert.Equal(2, Constructors.Length);
        Assert.DoesNotContain(Constructors[0].GetParameters(), parameter => parameter.ParameterType == typeof(EvaluationServices));
        Assert.Contains(Constructors[1].GetParameters(), parameter => parameter.ParameterType == typeof(EvaluationServices));
    }

    [Fact]
    public void TheShorterConstructorIsAPrefixOfTheLongerOne()
    {
        var shorter = Constructors[0].GetParameters();
        var longer = Constructors[1].GetParameters();

        Assert.Equal(shorter.Length + 1, longer.Length);
        Assert.Equal(
            shorter.Select(parameter => (parameter.Name, parameter.ParameterType)),
            longer.Take(shorter.Length).Select(parameter => (parameter.Name, parameter.ParameterType)));
    }

    [Fact]
    public void TheLongestConstructorLeavesNoParameterTheContainerMustGuessAt()
    {
        Assert.DoesNotContain(Constructors[1].GetParameters(), parameter => parameter.HasDefaultValue);
    }
}
