using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class ScenarioDeploymentNameTests
{
    [Fact]
    public void EveryDeploymentNameTheScenariosDeclareIsOneDeploymentCreateAccepts()
    {
        var names = typeof(McsAgentScenarios).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("DeploymentName", StringComparison.Ordinal))
            .ToDictionary(field => $"{field.DeclaringType!.Name}.{field.Name}", field => (string)field.GetRawConstantValue()!);

        Assert.Contains("McsAgentScenarios.SchemaNameOverrideDeploymentName", names.Keys);
        Assert.All(names, name => DeploymentNameRule.Validate("--deployment-name", name.Value));
    }
}
