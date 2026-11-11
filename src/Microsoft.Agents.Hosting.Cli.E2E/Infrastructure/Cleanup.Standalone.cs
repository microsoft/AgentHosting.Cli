using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed partial class Cleanup
{
    private readonly Dictionary<string, Carrier> _carriers = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string?> RemoveStandaloneWorkflowAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.WorkflowId is not { Length: > 0 } workflowId || entry.Cleanup.EnvironmentId is not { Length: > 0 } environmentId)
        {
            return "the ledger entry has no workflow id or environment id";
        }

        var present = await StandaloneWorkflowPresenceAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false);

        if (present.Exists == false)
        {
            return null;
        }

        if (present.Name is { } name && !name.StartsWith(StandaloneServices.FlowNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"refused: the flow is named '{name}', a name the suite never gives a flow, so it is left for a person to check";
        }

        var typedName = present.Name ?? entry.DisplayName;
        string? recordFailure = null;

        if (entry.Cleanup.ProjectDirectory is { Length: > 0 } project && Directory.Exists(project) && entry.Cleanup.AgentId is { Length: > 0 } agentId)
        {
            recordFailure = await DeleteThroughRecordAsync(project, agentId, workflowId, entry.Cleanup.DeploymentName, typedName, cancellationToken).ConfigureAwait(false);

            if ((await StandaloneWorkflowPresenceAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false)).Exists == false)
            {
                return null;
            }
        }

        var cloneFailure = await DeleteThroughCloneAsync(environmentId, workflowId, typedName, cancellationToken).ConfigureAwait(false);

        return (await StandaloneWorkflowPresenceAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false)).Exists == false
            ? null
            : cloneFailure ?? recordFailure ?? "the flow is still present after deleting it through its record and through a clone";
    }

    private async Task<(bool? Exists, string? Name)> StandaloneWorkflowPresenceAsync(string environmentId, string workflowId, CancellationToken cancellationToken)
    {
        if (_standalone is { OracleAvailable: true } standalone)
        {
            try
            {
                var row = await standalone.Dataverse.WorkflowAsync(environmentId, workflowId, includeDefinition: false, cancellationToken).ConfigureAwait(false);
                return (row is not null, row?.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _transcript.Note($"oracle read of {workflowId} failed, falling back to the CLI listing: {exception.Message}");
            }
        }

        return await _cloud.WorkflowExistsAsync(environmentId, workflowId, cancellationToken).ConfigureAwait(false) switch
        {
            false => (false, null),
            _ => (true, null),
        };
    }

    private async Task<string?> DeleteThroughRecordAsync(string project, string agentId, string workflowId, string? deploymentName, string? typedName, CancellationToken cancellationToken)
    {
        var shown = await _msagent.RunAsync(
            ["agent", "show", "--agent-id", agentId, "--json", "--non-interactive"],
            project,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (shown.ExitCode != 0 || RecordAddressing(shown.StdOut, agentId, workflowId) is not { } recordName)
        {
            return $"the record {agentId} no longer addresses the flow";
        }

        string[] selector = deploymentName is { Length: > 0 }
            ? ["agent", "delete", "--agent-id", agentId, "--deployment-name", deploymentName, "--json"]
            : ["agent", "delete", "--agent-id", agentId, "--json"];

        var typed = await _msagent.RunAsync(selector, project, stdin: recordName.Length > 0 ? recordName : typedName ?? agentId, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (typed.ExitCode == 0 && typed.StdOut.Contains("agent-deleted", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var forced = await _msagent.RunAsync([.. selector, "--non-interactive"], project, stdin: null, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);

        return forced.ExitCode == 0 ? null : $"agent delete exited {forced.ExitCode}: {Summarize(forced)}";
    }

    private static string? RecordAddressing(string json, string agentId, string workflowId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!TryProperty(document.RootElement, "agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var agent in agents.EnumerateArray())
            {
                if (string.Equals(PropertyText(agent, "agentId"), agentId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(PropertyText(agent, "mcsWorkflowId"), workflowId, StringComparison.OrdinalIgnoreCase))
                {
                    return PropertyText(agent, "displayName") ?? string.Empty;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> DeleteThroughCloneAsync(string environmentId, string workflowId, string? typedName, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(ScratchRoot(), $"delete-{workflowId[..8]}-{Guid.NewGuid().ToString()[..4]}");
        Directory.CreateDirectory(directory);

        try
        {
            var cloned = await _msagent.RunAsync(
                ["agent", "create", "--agent-type", "MCSWorkflow", "--mcs-clone-workflow-id", workflowId, "--mcs-clone-environment-id", environmentId, "--environment-id", environmentId, "--json", "--non-interactive"],
                directory,
                timeout: TimeSpan.FromMinutes(10),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (cloned.ExitCode != 0)
            {
                return $"the clone made to delete the flow exited {cloned.ExitCode}: {Summarize(cloned)}";
            }

            using var document = JsonDocument.Parse(cloned.StdOut);
            var root = document.RootElement;
            var agentId = PropertyText(root, "agentId");
            var projectDirectory = PropertyText(root, "projectDirectory") is { Length: > 0 } written && Directory.Exists(written) ? written : directory;
            var deploymentName = PropertyText(root, "deploymentName");
            var name = PropertyText(root, "displayName") ?? typedName;

            if (agentId is not { Length: > 0 })
            {
                return "the clone made to delete the flow reported no record";
            }

            string[] selector = deploymentName is { Length: > 0 }
                ? ["agent", "delete", "--agent-id", agentId, "--deployment-name", deploymentName, "--json", "--non-interactive"]
                : ["agent", "delete", "--agent-id", agentId, "--json", "--non-interactive"];

            var deleted = await _msagent.RunAsync(selector, projectDirectory, stdin: name, TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);

            return deleted.ExitCode == 0 ? null : $"agent delete through a clone exited {deleted.ExitCode}: {Summarize(deleted)}";
        }
        catch (JsonException exception)
        {
            return $"the clone made to delete the flow printed no readable result: {exception.Message}";
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private async Task<string?> RemoveStandaloneConnectionReferenceAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Cleanup.Correlation is not { Length: > 0 } logicalName || entry.Cleanup.EnvironmentId is not { Length: > 0 } environmentId)
        {
            return "the ledger entry has no logical name or environment id";
        }

        if (await ReferencePresenceAsync(environmentId, logicalName, cancellationToken).ConfigureAwait(false) == false)
        {
            return null;
        }

        var carrier = await CarrierAsync(environmentId, cancellationToken).ConfigureAwait(false);

        if (carrier.Failure is { } carrierFailure)
        {
            return carrierFailure;
        }

        var declarations = Path.Combine(carrier.ProjectDirectory, "infrastructure", "connections");
        Directory.CreateDirectory(declarations);
        File.WriteAllText(
            Path.Combine(declarations, $"{logicalName}.sync.yaml"),
            $"connectionReferences:\n  - connectionReferenceLogicalName: {logicalName}\n    connectorId: {entry.Cleanup.ConnectorId ?? "/providers/Microsoft.PowerApps/apis/shared_msnweather"}\n");

        var removed = await _msagent.RunAsync(
            ["deployment", "connection", "remove", "--agent-id", carrier.AgentId, "--deployment-name", carrier.DeploymentName, "--connection-reference", logicalName, "--force", "--json", "--non-interactive"],
            carrier.ProjectDirectory,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var after = await ReferencePresenceAsync(environmentId, logicalName, cancellationToken).ConfigureAwait(false);

        return after == false || (after is null && removed.ExitCode == 0)
            ? null
            : removed.ExitCode != 0
                ? $"deployment connection remove exited {removed.ExitCode}: {Summarize(removed)}"
                : "the command succeeded but the reference row is still there";
    }

    private async Task<bool?> ReferencePresenceAsync(string environmentId, string logicalName, CancellationToken cancellationToken)
    {
        if (_standalone is not { OracleAvailable: true } standalone)
        {
            return null;
        }

        try
        {
            return await standalone.Dataverse.ConnectionReferenceAsync(environmentId, logicalName, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _transcript.Note($"oracle read of reference {logicalName} failed: {exception.Message}");
            return null;
        }
    }

    private async Task<Carrier> CarrierAsync(string environmentId, CancellationToken cancellationToken)
    {
        if (_carriers.TryGetValue(environmentId, out var known))
        {
            return known;
        }

        var built = await BuildCarrierAsync(environmentId, cancellationToken).ConfigureAwait(false);
        _carriers[environmentId] = built;
        return built;
    }

    private async Task<Carrier> BuildCarrierAsync(string environmentId, CancellationToken cancellationToken)
    {
        var workflowId = Guid.NewGuid();
        var name = $"{StandaloneServices.FlowNamePrefix}-carrier-{workflowId.ToString()[..6]}";
        var directory = Path.Combine(ScratchRoot(), $"carrier-{environmentId[..8]}-{workflowId.ToString()[..4]}");
        var folder = Path.Combine(directory, "workflows", $"carrier-{workflowId}");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(directory, "workflow.sync.yaml"), "workflowProjectVersion: 1\n");
        File.WriteAllText(Path.Combine(folder, "metadata.yml"), CarrierMetadata(workflowId, name));
        File.WriteAllText(Path.Combine(folder, "workflow.json"), CarrierDefinition);

        var initialised = await _msagent.RunAsync(
            ["agent", "init", "--agent-type", "MCSWorkflow", "--agent-name", name, "--json", "--non-interactive"],
            directory,
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (initialised.ExitCode != 0)
        {
            return Carrier.Failed($"the carrier workspace could not be registered: {Summarize(initialised)}");
        }

        string? agentId;
        string? homeEnvironment;

        using (var document = JsonDocument.Parse(initialised.StdOut))
        {
            agentId = PropertyText(document.RootElement, "agentId");
            homeEnvironment = PropertyText(document.RootElement, "environmentId");
        }

        if (agentId is not { Length: > 0 })
        {
            return Carrier.Failed("the carrier workspace's registration reported no record");
        }

        const string DeploymentName = "carrier";
        string[] slot = string.Equals(homeEnvironment, environmentId, StringComparison.OrdinalIgnoreCase)
            ? ["deployment", "create", "--agent-id", agentId, "--deployment-name", DeploymentName, "--deployment-type", "dev", "--json", "--non-interactive"]
            : ["deployment", "create", "--agent-id", agentId, "--deployment-name", DeploymentName, "--deployment-type", "test", "--environment-id", environmentId, "--json", "--non-interactive"];

        var created = await _msagent.RunAsync(slot, directory, timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);

        return created.ExitCode == 0
            ? new Carrier(directory, agentId, DeploymentName, null)
            : Carrier.Failed($"the carrier workspace's deployment could not be created: {Summarize(created)}");
    }

    private string ScratchRoot()
    {
        var root = _standalone?.ScratchDirectory ?? Path.Combine(Path.GetTempPath(), "ahe2e-sw-cleanup");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CarrierMetadata(Guid workflowId, string name) =>
        $"""
        jsonFileName: workflows/carrier-{workflowId}/workflow.json
        workflowId: {workflowId}
        name: {name}
        type: 1
        description: Never deployed. The e2e cleanup removes connection references through it.
        subprocess: false
        category: 5
        mode: 0
        scope: 4
        onDemand: false
        triggerOnCreate: false
        triggerOnDelete: false
        asyncAutodelete: false
        syncWorkflowLogOnFailure: false
        stateCode: 0
        statusCode: 1
        runAs: 1
        isTransacted: true
        introducedVersion: 1.0
        businessProcessType: 0
        modernFlowType: 1
        primaryEntity: none
        connectionReferences: []

        """;

    private const string CarrierDefinition =
        """
        {
          "properties": {
            "connectionReferences": {},
            "definition": {
              "$schema": "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#",
              "contentVersion": "1.0.0.0",
              "parameters": { "$connections": { "defaultValue": {}, "type": "Object" } },
              "triggers": { "manual": { "type": "Request", "kind": "Button", "inputs": { "schema": { "type": "object", "properties": {}, "required": [] } } } },
              "actions": { "Compose_noop": { "runAfter": {}, "type": "Compose", "inputs": "carrier" } },
              "outputs": {}
            }
          },
          "schemaVersion": "1.0.0.0"
        }
        """;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? PropertyText(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Carrier(string ProjectDirectory, string AgentId, string DeploymentName, string? Failure)
    {
        public static Carrier Failed(string failure) => new(string.Empty, string.Empty, string.Empty, failure);
    }
}
