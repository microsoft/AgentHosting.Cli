using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.StandaloneWorkflow;

public static class SwChecks
{
    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SettlePoll = TimeSpan.FromSeconds(5);

    public static IReadOnlyDictionary<string, string> Snapshot(string directory)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(directory))
        {
            return files;
        }

        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(directory, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return files;
    }

    public static IReadOnlyDictionary<string, string> FilesSnapshot(string directory) =>
        Snapshot(directory).Where(pair => !IsRecordFile(pair.Key) && !IsCacheFile(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public static bool IsRecordFile(string relativePath) =>
        relativePath.StartsWith(".config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsCacheFile(string relativePath) =>
        relativePath.StartsWith(".mcs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string? Difference(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var added = after.Keys.Where(path => !before.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var removed = before.Keys.Where(path => !after.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var changed = before.Where(pair => after.TryGetValue(pair.Key, out var hash) && !string.Equals(hash, pair.Value, StringComparison.Ordinal))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();

        if (added.Length + removed.Length + changed.Length == 0)
        {
            return null;
        }

        var parts = new List<string>();

        if (added.Length > 0)
        {
            parts.Add($"added {string.Join(", ", added.Take(6))}{(added.Length > 6 ? "…" : string.Empty)}");
        }

        if (removed.Length > 0)
        {
            parts.Add($"removed {string.Join(", ", removed.Take(6))}{(removed.Length > 6 ? "…" : string.Empty)}");
        }

        if (changed.Length > 0)
        {
            parts.Add($"changed {string.Join(", ", changed.Take(6))}{(changed.Length > 6 ? "…" : string.Empty)}");
        }

        return string.Join("; ", parts);
    }

    public static void ExpectUnchanged(SwRun run, IReadOnlyDictionary<string, string> before, string directory, string check, string severity = "P1", string? reference = null)
    {
        var difference = Difference(before, Snapshot(directory));
        run.Expect(difference is null, check, "no file written, changed or removed", difference ?? string.Empty, severity, reference);
    }

    public static void ExpectFilesUnchanged(SwRun run, IReadOnlyDictionary<string, string> before, string directory, string check, string severity = "P1", string? reference = null)
    {
        var difference = Difference(before, FilesSnapshot(directory));
        run.Expect(difference is null, check, "no file outside .config and the .mcs cache written, changed or removed", difference ?? string.Empty, severity, reference);
    }

    public static void ExpectNothingOutside(SwRun run, IReadOnlyDictionary<string, string> before, string scenarioRoot, string allowedDirectory, string check)
    {
        var after = Snapshot(scenarioRoot);
        var allowed = Path.GetRelativePath(scenarioRoot, allowedDirectory);
        var escaped = after.Keys
            .Where(path => !before.ContainsKey(path))
            .Where(path => !path.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        run.Expect(escaped.Length == 0, check, $"every new file under {allowed}", string.Join(", ", escaped.Take(6)), "P0");
    }

    public static string? ConfigProblem(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;

            var agents = Items(root, "managedAgents").Select(agent => Text(agent, "agentId") ?? string.Empty).ToArray();
            var duplicateAgent = agents.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);

            if (duplicateAgent is not null)
            {
                return $"record {duplicateAgent.Key} appears {duplicateAgent.Count()} times";
            }

            foreach (var deployment in Items(root, "agentDeployments"))
            {
                var owner = Text(deployment, "agentId");

                if (!agents.Contains(owner, StringComparer.OrdinalIgnoreCase))
                {
                    return $"deployment {Text(deployment, "deploymentName")} names record {owner}, which the file does not hold";
                }
            }

            return null;
        }
        catch (JsonException exception)
        {
            return $"the configuration does not parse: {exception.Message}";
        }
    }

    public static void ExpectConfigIntact(SwRun run, string projectDirectory, string check, string severity = "P0")
    {
        var problem = ConfigProblem(Path.Combine(projectDirectory, ".config", "agent.config.json"));
        run.Expect(problem is null, check, "a well-formed agent.config.json", problem ?? string.Empty, severity);
    }

    public static IReadOnlyList<(string AgentId, string? DisplayName, string? WorkflowId, string? Location)> Records(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, ".config", "agent.config.json");

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            return [.. Items(document.RootElement, "managedAgents").Select(agent => (
                Text(agent, "agentId") ?? string.Empty,
                Text(agent, "displayName"),
                Text(agent, "mcsWorkflowId"),
                Text(agent, "agentLocation")))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<(string AgentId, string? DeploymentName, string? DeploymentType, string? EnvironmentId)> Deployments(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, ".config", "agent.config.json");

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            return [.. Items(document.RootElement, "agentDeployments").Select(deployment => (
                Text(deployment, "agentId") ?? string.Empty,
                Text(deployment, "deploymentName"),
                Text(deployment, "deploymentType"),
                Text(deployment, "environmentId")))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<DataverseWorkflow?> CloudAsync(SwRun run, string workflowId, string? environmentId = null, bool definition = false) =>
        run.Oracle
            ? await run.Services.Dataverse.WorkflowAsync(environmentId ?? run.Dev, workflowId, definition, run.Token).ConfigureAwait(false)
            : null;

    public static async Task<DataverseWorkflow?> ExpectCloudAsync(SwRun run, string workflowId, bool? activated, string check, string? environmentId = null, string severity = "P1", string? reference = null)
    {
        if (!run.Oracle)
        {
            run.Note($"skipped (no oracle): {check}");
            return null;
        }

        var deadline = DateTimeOffset.UtcNow + SettleWait;
        DataverseWorkflow? row;

        while (true)
        {
            row = await CloudAsync(run, workflowId, environmentId).ConfigureAwait(false);

            if ((row is not null && (activated is null || row.IsActivated == activated)) || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(SettlePoll, run.Token).ConfigureAwait(false);
        }

        var wanted = activated switch { true => "an activated flow", false => "a draft flow", _ => "the flow to exist" };
        var actual = row is null ? "no flow" : $"statecode {row.StateCode} statuscode {row.StatusCode}";

        run.Expect(row is not null && (activated is null || row.IsActivated == activated), check, wanted, actual, severity, reference);
        return row;
    }

    public static async Task ExpectCloudAbsentAsync(SwRun run, string workflowId, string check, string? environmentId = null, string severity = "P1", string? reference = null)
    {
        if (!run.Oracle)
        {
            run.Note($"skipped (no oracle): {check}");
            return;
        }

        var deadline = DateTimeOffset.UtcNow + SettleWait;
        DataverseWorkflow? row;

        while (true)
        {
            row = await CloudAsync(run, workflowId, environmentId).ConfigureAwait(false);

            if (row is null || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(SettlePoll, run.Token).ConfigureAwait(false);
        }

        run.Expect(row is null, check, "no flow", row is null ? string.Empty : $"'{row.Name}' statecode {row.StateCode}", severity, reference);
    }

    public static async Task ExpectSingleFlowAsync(SwRun run, string name, string check, string? environmentId = null)
    {
        if (!run.Oracle)
        {
            run.Note($"skipped (no oracle): {check}");
            return;
        }

        var flows = await run.Services.Dataverse.WorkflowsNamedAsync(environmentId ?? run.Dev, name, run.Token).ConfigureAwait(false);
        run.Expect(flows.Count == 1, check, "exactly one flow with the name", $"{flows.Count} flows: {string.Join(", ", flows.Select(flow => flow.WorkflowId))}", "P1");
    }

    public static async Task ExpectHelpAsync(SwRun run, IReadOnlyList<string> command, string? option, string description, string check, string reference)
    {
        var result = await run.Context.Msagent.RunAsync([.. command, "--help"], timeout: TimeSpan.FromMinutes(1), cancellationToken: run.Token).ConfigureAwait(false);
        var help = string.Join(' ', result.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var start = option is null ? -1 : help.IndexOf($"{option} ", StringComparison.Ordinal);
        var described = start < 0 ? help : help[start..];

        run.Expect(result.ExitCode == 0 && described.Contains(description, StringComparison.Ordinal), check, $"{option ?? string.Join(' ', command)} described with \"{description}\"", $"exit {result.ExitCode}: {described}", "P3", reference, result);
    }

    public static async Task<IReadOnlyList<string>?> UnboundAsync(SwRun run, IEnumerable<string> logicalNames, string? environmentId = null)
    {
        if (!run.Oracle)
        {
            return null;
        }

        var unbound = new List<string>();

        foreach (var logicalName in logicalNames)
        {
            var row = await run.Services.Dataverse.ConnectionReferenceAsync(environmentId ?? run.Dev, logicalName, run.Token).ConfigureAwait(false);

            if (row is not { Bound: true })
            {
                unbound.Add(logicalName);
            }
        }

        return unbound;
    }

    public static IReadOnlyList<string> Structure(string definitionJson)
    {
        using var document = JsonDocument.Parse(definitionJson);
        var root = document.RootElement;
        var properties = root.TryGetProperty("properties", out var found) ? found : root;
        var names = new SortedSet<string>(StringComparer.Ordinal);

        if (properties.TryGetProperty("connectionReferences", out var references) && references.ValueKind == JsonValueKind.Object)
        {
            foreach (var reference in references.EnumerateObject())
            {
                names.Add($"reference:{reference.Name}");
            }
        }

        if (properties.TryGetProperty("definition", out var definition))
        {
            Collect(definition, names);
        }

        return [.. names];
    }

    public static string? StructureDifference(IReadOnlyList<string> local, IReadOnlyList<string> cloud)
    {
        var missing = local.Except(cloud, StringComparer.Ordinal).ToArray();
        var extra = cloud.Except(local, StringComparer.Ordinal).ToArray();

        return missing.Length + extra.Length == 0
            ? null
            : $"missing in the cloud: {string.Join(", ", missing.Take(8))}; only in the cloud: {string.Join(", ", extra.Take(8))}";
    }

    private static void Collect(JsonElement element, SortedSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "triggers" or "actions" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        var type = child.Value.ValueKind == JsonValueKind.Object && child.Value.TryGetProperty("type", out var typed) && typed.ValueKind == JsonValueKind.String
                            ? typed.GetString()
                            : null;

                        names.Add($"{(property.Name == "triggers" ? "trigger" : "action")}:{child.Name}:{type}");
                    }
                }

                Collect(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Collect(item, names);
            }
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
            {
                return [.. property.Value.EnumerateArray().Select(element => element.Clone())];
            }
        }

        return [];
    }

    private static string? Text(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
