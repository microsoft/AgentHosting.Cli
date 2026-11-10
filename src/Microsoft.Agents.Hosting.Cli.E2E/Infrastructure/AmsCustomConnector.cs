using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed record AmsCustomConnector(Guid ConnectorId, string Name, string DisplayName, string Description, string InternalId, string ConnectionReference)
{
    public const string FixtureFolder = "CustomConnector";

    public string Folder => $"{CustomConnectorLayout.FolderName}/{Name}-{ConnectorId}";

    public string ToolEntry => $"actions/{Name}.mcs.yml";

    public static AmsCustomConnector For(string publisherPrefix, string botSchemaName, string displayName)
    {
        var unique = new string([.. displayName.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
        var internalId = $"shared_{publisherPrefix.ToLowerInvariant()}-5f{unique}-5f{Guid.NewGuid().ToString("N")[..16]}";

        return new(Guid.NewGuid(), $"{publisherPrefix}_5F{unique}", displayName, $"{displayName}: a custom connector the MCS-on-AMS E2E checks create and delete.", internalId, $"{botSchemaName}.{internalId}.{Guid.NewGuid():N}");
    }

    public static string? DescriptionOf(string? metadata)
    {
        try
        {
            return metadata is not null && JsonNode.Parse(metadata.StartsWith('\uFEFF') ? metadata[1..] : metadata) is JsonObject root && root["description"] is JsonValue value && value.TryGetValue<string>(out var description) ? description : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void AddTo(string fixture, string agentFolder)
    {
        var folder = Path.Combine(agentFolder, Folder);
        var tool = Path.Combine(agentFolder, ToolEntry);
        FixtureCopies.CopyTree(fixture, folder, keepLocalState: false);
        File.WriteAllText(Path.Combine(folder, CustomConnectorLayout.MetadataFileName), Metadata().ToJsonString(ConfigJson.Options));
        AmsMcsFiles.AddConnectionReference(agentFolder, ConnectionReference, DataverseConnector.Api(InternalId));
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, string.Join('\n', "mcs.metadata:", $"  componentName: {FixtureCopies.Yaml(DisplayName)}", $"  description: {FixtureCopies.Yaml(Description)}", "kind: TaskDialog", $"modelDisplayName: {FixtureCopies.Yaml(DisplayName)}", $"modelDescription: {FixtureCopies.Yaml(Description)}", "action:", "  kind: InvokeExternalAgentTaskAction", $"  connectionReference: {ConnectionReference}", "  connectionProperties:", "    mode: Invoker", string.Empty, "  operationDetails:", "    kind: ActivityProtocolMetadata", "    operationId: SendActivity", string.Empty));
    }

    private JsonObject Metadata() => new()
    {
        ["connectorid"] = ConnectorId.ToString(),
        ["name"] = Name,
        ["displayname"] = DisplayName,
        ["description"] = Description,
        ["connectorinternalid"] = InternalId,
        ["openapidefinition"] = $"{Folder}/{CustomConnectorLayout.OpenApiDefinitionFileName}",
        ["connectionparameters"] = $"{Folder}/{CustomConnectorLayout.ConnectionParametersFileName}",
        ["connectionparametersets"] = null,
        ["policytemplateinstances"] = $"{Folder}/{CustomConnectorLayout.PolicyTemplateInstancesFileName}",
        ["iconbrandcolor"] = "#007ee5",
        ["iconblob"] = $"{Folder}/{CustomConnectorLayout.IconFileName}",
        ["connectortype"] = 1,
        ["statecode"] = 0,
        ["statuscode"] = 1,
        ["ismanaged"] = false,
        ["componentstate"] = 0,
    };
}
