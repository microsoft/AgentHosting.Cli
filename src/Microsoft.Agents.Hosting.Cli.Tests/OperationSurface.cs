using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// The result types the operations facade can hand to a renderer, across every interface it offers.
///
/// <para>The guards pinning JSON registration and human rendering both read this. Reflecting over a
/// single interface would quietly stop covering a result the moment its operation moved to another
/// one, which is the kind of silent loss those guards exist to prevent.</para>
/// </summary>
internal static class OperationSurface
{
    private static readonly Type[] Facades = [typeof(IAgentHostingOperations), typeof(IAgentEvaluationOperations)];

    public static IEnumerable<Type> ResultTypes() => Facades
        .SelectMany(facade => facade.GetMethods())
        .Where(method => method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        .Select(method => method.ReturnType.GetGenericArguments()[0])
        .Distinct();
}
