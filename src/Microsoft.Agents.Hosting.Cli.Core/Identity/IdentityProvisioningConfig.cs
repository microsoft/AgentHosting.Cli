namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>Microsoft public-cloud coordinates used by Blueprint identity provisioning.</summary>
public static class IdentityProvisioningConfig
{
    public const string GraphEndpoint = "https://graph.microsoft.com";
    public const string GraphReadWriteScope =
        "https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All";
    public const string GraphPrincipalCreateScope =
        "https://graph.microsoft.com/AgentIdentityBlueprintPrincipal.Create";
    public const string GraphUserReadScope = "https://graph.microsoft.com/User.Read";
    public const string GraphApplicationReadScope = "https://graph.microsoft.com/Application.Read.All";
    public const string GraphApplicationReadWriteScope =
        "https://graph.microsoft.com/Application.ReadWrite.All";
    public const string GraphAppRoleAssignmentReadWriteScope =
        "https://graph.microsoft.com/AppRoleAssignment.ReadWrite.All";
    public const string ArmResource = "https://management.azure.com";
    public const string ManagerApplicationId = "e8be65d6-d430-4289-a665-51bf2a194bda";
    public const string ManagedIdentityApiVersion = "2024-11-30";

    public static BlueprintPermission MessagingBotPermission { get; } =
        new(
            "MessagingBot",
            "5a807f24-c9de-44ee-a3a7-329e88a00ffc",
            "AgentData.ReadWrite",
            ResourceIdentifierUri: "https://botapi.skype.com");

    public static BlueprintPermission PowerPlatformPermission { get; } =
        new(
            "PowerPlatform",
            "8578e004-a5c6-46e7-913e-12f58912df43",
            "Connectivity.Connections.Read",
            ResourceIdentifierUri: "https://api.powerplatform.com");

    public static BlueprintPermission ObservabilityPermission { get; } =
        new(
            "Observability",
            "9b975845-388f-4429-889e-eab1ef63949c",
            "Agent365.Observability.OtelWrite",
            "Agent365.Observability.OtelWrite",
            "api://9b975845-388f-4429-889e-eab1ef63949c");

    public static BlueprintPermission McpDiscoveryPermission { get; } =
        new(
            "McpDiscovery",
            "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1",
            "McpServersMetadata.Read.All",
            ResourceIdentifierUri: "https://agent365.svc.cloud.microsoft");

    public static IReadOnlyList<string> TeamsHostApplicationIds { get; } =
    [
        "1fec8e78-bce4-4aaf-ab1b-5451cc387264",
        "5e3ce6c0-2b1f-4285-8d4b-75ee78787346",
    ];
}
