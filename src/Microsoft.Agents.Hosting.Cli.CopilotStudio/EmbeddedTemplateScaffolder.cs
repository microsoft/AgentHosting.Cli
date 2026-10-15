using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Writes a new workspace from templates embedded in this assembly.
/// </summary>
public sealed partial class EmbeddedTemplateScaffolder : ICopilotScaffoldPort
{
    private const string ResourceRoot = "Templates/";
    private const string SubstitutionPrefix = "template_";

    private readonly Assembly _assembly = typeof(EmbeddedTemplateScaffolder).Assembly;

    /// <inheritdoc />
    public IReadOnlyList<string> Scaffold(CopilotScaffoldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resourcePrefix = $"{ResourceRoot}{command.TemplateFolder}/";
        var resources = _assembly.GetManifestResourceNames().Where(name => NormalizeSeparators(name).StartsWith(resourcePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (resources.Length == 0)
        {
            throw CopilotStudioException.TemplateNotFound(command.TemplateFolder);
        }

        Directory.CreateDirectory(command.ProjectDirectory);

        var substitutions = BuildSubstitutions(command);
        var created = new List<string>();

        foreach (var resource in resources)
        {
            var relativePath = ToWorkspacePath(NormalizeSeparators(resource)[resourcePrefix.Length..], out var substitute);
            var targetPath = Path.Combine(command.ProjectDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Write(resource, targetPath, substitute ? substitutions : null);
            created.Add(relativePath);
        }

        return [.. created.OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static string NormalizeSeparators(string resourceName) => resourceName.Replace('\\', '/');

    private static string ToWorkspacePath(string resourceRelativePath, out bool substitute)
    {
        var separator = resourceRelativePath.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : resourceRelativePath[..(separator + 1)];
        var fileName = resourceRelativePath[(separator + 1)..];

        substitute = fileName.StartsWith(SubstitutionPrefix, StringComparison.OrdinalIgnoreCase);

        return substitute ? directory + fileName[SubstitutionPrefix.Length..] : resourceRelativePath;
    }

    private void Write(string resourceName, string targetPath, IReadOnlyDictionary<string, string>? substitutions)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName) ?? throw CopilotStudioException.TemplateNotFound(resourceName);

        if (substitutions is null)
        {
            using var file = File.Create(targetPath);
            stream.CopyTo(file);

            return;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = ApplySubstitutions(reader.ReadToEnd(), substitutions);

        File.WriteAllText(targetPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Applies the template's substitutions, quoting anything a <c>$yaml{...}$</c> wrapper encloses.
    ///
    /// A template value built from a token and a literal suffix — <c>$agentSchemaName$.topic.Escalate</c>
    /// — cannot be quoted by substituting a pre-quoted token, because the quotes have to wrap the whole
    /// scalar. Left unquoted, a schema name beginning with <c>!</c> or <c>{</c> turns the value into a
    /// YAML tag or flow mapping and the generated file no longer parses. Wrappers are resolved against
    /// the raw template, where no user data has landed yet, so their bounds are unambiguous.
    /// </summary>
    private static string ApplySubstitutions(string template, IReadOnlyDictionary<string, string> substitutions)
    {
        var content = YamlScalarWrapper().Replace(template, match => ToYamlScalar(Replace(match.Groups["value"].Value, substitutions)));

        return Replace(content, substitutions);
    }

    private static string Replace(string content, IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var substitution in substitutions)
        {
            content = content.Replace(substitution.Key, substitution.Value, StringComparison.Ordinal);
        }

        return content;
    }

    private static Dictionary<string, string> BuildSubstitutions(CopilotScaffoldCommand command) => new(StringComparer.Ordinal)
    {
        ["$agentDisplayName$"] = command.DisplayName,
        ["$agentSchemaName$"] = command.SchemaName,
        ["$agentInstructions$"] = command.Instructions,
        ["$workspaceLayoutVersion$"] = AgentClassifier.CurrentLayoutVersion.ToString(CultureInfo.InvariantCulture),
        ["$agentDisplayNameYaml$"] = ToYamlScalar(command.DisplayName),
        ["$agentSchemaNameYaml$"] = ToYamlScalar(command.SchemaName),
        ["$agentInstructionsYaml$"] = ToYamlScalar(command.Instructions),
    };

    private static string ToYamlScalar(string value) =>
        string.IsNullOrEmpty(value) ? "\"\"" : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\"";

    [GeneratedRegex(@"\$yaml\{(?<value>[^\r\n]*?)\}\$", RegexOptions.CultureInvariant)]
    private static partial Regex YamlScalarWrapper();
}
