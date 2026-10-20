using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;

namespace Microsoft.Agents.Hosting.Cli.Features;

/// <summary>
/// PAC-compatible feature controls: shipped JSON supplies defaults and a semicolon-separated
/// environment list can enable named features without changing the installation.
/// </summary>
public class FeatureFlags : IFeatureFlags
{
    public const string DefaultFileName = "featureflags.json";
    public const string OverrideEnvironmentVariable = "AH_CLI_FEATURES";

    private readonly Dictionary<string, string> _fileEntries;

    private FeatureFlags(IDictionary<string, string> fileEntries)
    {
        _ = fileEntries ?? throw new ArgumentNullException(nameof(fileEntries));
        _fileEntries = new Dictionary<string, string>(fileEntries, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads the feature defaults next to the specified assembly.</summary>
    public static FeatureFlags LoadFromAssemblyFolder(Assembly assembly, string filename = DefaultFileName)
    {
        _ = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _ = !string.IsNullOrEmpty(filename) ? filename : throw new ArgumentNullException(nameof(filename));

        var assemblyPath = Path.GetDirectoryName(AppContext.BaseDirectory)!;
        var filePath = Path.Combine(assemblyPath, filename);
        return LoadFromFile(filePath);
    }

    /// <summary>Loads feature defaults from a JSON object.</summary>
    public static FeatureFlags LoadFromFile(string filePath)
    {
        _ = filePath ?? throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
        {
            var fullPath = Path.GetFullPath(filePath);
            throw new FileNotFoundException(
                $"Installation error: the required feature controls file '{fullPath}' is missing. " +
                "Please uninstall and reinstall the Microsoft.Agents.Hosting.Cli tool.",
                fullPath);
        }
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath))
            ?? throw new JsonException("Feature flags json content is null; must be an object.");

        return new FeatureFlags(entries);
    }

    /// <inheritdoc />
    public bool IsFeatureEnabled(FeatureName featureName)
    {
        if (IsEnabledByEnvironment(featureName))
        {
            return true;
        }

        return _fileEntries.TryGetValue(featureName.ToString(), out var value)
            && string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnabledByEnvironment(FeatureName featureName)
    {
        var overrideList = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overrideList))
        {
            return false;
        }

        var name = featureName.ToString();
        return overrideList
            .Split(';')
            .Any(entry => string.Equals(entry.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }
}
