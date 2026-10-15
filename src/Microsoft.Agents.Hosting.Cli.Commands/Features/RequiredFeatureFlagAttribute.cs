namespace Microsoft.Agents.Hosting.Cli.Commands.Features;

/// <summary>
/// Requires a feature before a command or command group is included in discovery, help, parsing, or
/// dispatch. Multiple attributes are evaluated as an AND operation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
public sealed class RequiredFeatureFlagAttribute(FeatureName feature) : Attribute
{
    /// <summary>The required feature.</summary>
    public FeatureName Feature { get; } = feature;
}
