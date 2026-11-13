using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins the legacy constructor signatures and the distinct explicit profile-injection arity.
/// The Auth composition root supplies optional services explicitly rather than relying on
/// automatic longest-constructor selection.
/// </summary>
public sealed class OperationsConstructorSelectionTests
{
    private static ConstructorInfo[] Constructors => [.. typeof(AgentHostingOperations)
        .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(constructor => constructor.GetParameters().Length)];

    [Fact]
    public void TheFacadeOffersTheLegacyConstructorAndOneCarryingEvaluations()
    {
        Assert.Equal(3, Constructors.Length);
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
        var explicitParameters = Constructors[^1].GetParameters();
        Assert.True(explicitParameters.Length > Constructors[1].GetParameters().Length);
        Assert.DoesNotContain(explicitParameters, parameter => parameter.HasDefaultValue);
    }
}
