using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[CollectionDefinition(nameof(HarnessVariablesCollection), DisableParallelization = true)]
public sealed class HarnessVariablesCollection;

internal sealed class HarnessVariableScope : IDisposable
{
    private static readonly string[] Suffixes = ["TENANT_ID", "MCS_DEV_ENV", "AZURE_BOT_PREFIX", "AMS_ENDPOINT", "AMS_CONTROL_PLANE_ENDPOINT", "AMS_DATA_PLANE_ENDPOINT", "AMS_CLOUD", "AMS_DEV_ENVIRONMENT_ID", "AMS_TEST_ENVIRONMENT_ID", "AMS_PROD_ENVIRONMENT_ID", "AMS_ALLOW_CREATE", "AMS_PACKAGE_PATH"];

    private readonly Dictionary<string, string?> _prior = [];

    public HarnessVariableScope()
    {
        foreach (var name in new[] { E2EConfig.EnvironmentPrefix, E2EConfig.LegacyEnvironmentPrefix }.SelectMany(prefix => Suffixes.Select(suffix => prefix + suffix)))
        {
            _prior[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    public void Dispose()
    {
        foreach (var entry in _prior)
        {
            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
