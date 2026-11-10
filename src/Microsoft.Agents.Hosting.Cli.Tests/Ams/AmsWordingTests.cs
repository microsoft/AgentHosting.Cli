using System.Reflection;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsWordingTests
{
    public static TheoryData<string> Codes() => new(AmsMcsOperationErrorMapTests.ContractCodes.Select(row => (string)row[0]));

    public static TheoryData<string> Factories() => new(typeof(AmsAgentException).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.ReturnType == typeof(AmsAgentException)).Select(method => method.Name).Distinct());

    [Theory]
    [MemberData(nameof(Codes))]
    public void NoMappedAmsError_NamesARevision(string code)
    {
        var mapped = AmsMcsOperationErrorMap.Map(409, AmsMcsOperationAdapterTests.Error(code, "m"), "POST", new Uri("https://env-1.api.powerplatform.com/agenthosting/agents/mcs"), new ServiceCorrelation(null, null));
        Assert.DoesNotContain("revision", $"{mapped.Message} {mapped.Remediation}", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void NoAmsAgentFailure_NamesARevision(string factory)
    {
        foreach (var method in typeof(AmsAgentException).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == factory))
        {
            var failure = (AmsAgentException)method.Invoke(null, [.. method.GetParameters().Select(Sample)])!;

            Assert.DoesNotContain("revision", $"{failure.Message} {failure.Remediation}", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object? Sample(ParameterInfo parameter) => parameter.ParameterType switch
    {
        var type when type == typeof(string) => "x",
        var type when type == typeof(int) => ErrorCodes.AmsAgent.OperationFailed,
        var type when type == typeof(bool) => false,
        var type when type == typeof(IReadOnlyList<string>) => new[] { "x" },
        var type when type == typeof(Exception) => new InvalidOperationException("x"),
        _ => parameter.HasDefaultValue ? parameter.DefaultValue : null,
    };
}
