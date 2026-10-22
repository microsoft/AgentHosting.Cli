using CommonOAuthParameters = Microsoft.Agents.Hosting.Cli.Core.Deployments.DeploymentOAuthParameters.CommonOAuthParameters;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

public sealed class DeploymentException : AgentHostingException
{
    private DeploymentException(int errorCode, string message, int exitCode, string errorKind, string? remediation = null, System.Exception? innerException = null)
        : base(message, exitCode, errorKind, remediation, innerException, errorCode)
    {
    }

    /// <summary>The deployment not found locally or its backing Azure Bot Service returned <c>404 Not Found</c>.</summary>
    public static DeploymentException NotFound(string deploymentId, TargetRequestException? ex = null) =>
        new(
            ErrorCodes.Deployment.DeploymentNotFound,
            $"No deployment found with id '{deploymentId}': {ex?.ServiceMessage ?? ex?.Message}",
            ExitCodes.Usage,
            "deployment-not-found",
            "Create the deployment with `msagent deployment create` or pass an existing deployment id.");

    /// <summary>Retrieve the deployment failed for a reason other than <c>404 Not Found</c>.</summary>
    public static DeploymentException ReadFailed(string deploymentId, TargetRequestException ex) =>
        new(
            ErrorCodes.Deployment.DeploymentReadFailed,
            $"Deployment with id '{deploymentId}' exists but could not be read from Azure Bot Service: {ex.ServiceMessage ?? ex.Message}",
            ex.ExitCode,
            "deployment-read-failed",
            ex.Remediation,
            ex);

    /// <summary>Azure Bot Service resource for the deployment is invalid or missing.</summary>
    public static DeploymentException InvalidBotResource(string? deploymentId = null) =>
        new(
            ErrorCodes.Deployment.DeploymentInvalidBotResource,
            deploymentId is null
                ? "The Azure Bot Service resource id is invalid or missing."
                : $"Azure Bot Service resource for the deployment '{deploymentId}' is invalid or missing.",
            ExitCodes.Usage,
            "deployment-invalid-bot",
            "Re-run `msagent deployment create` to provision the backing bot, then retry.");

    public static DeploymentException OAuthConnectionNotFound(string deploymentId, string connectionName) =>
        new(
            ErrorCodes.Deployment.DeploymentOAuthConnectionNotFound,
            $"The OAuth connection named '{connectionName}' is not configured on deployment '{deploymentId}'.",
            ExitCodes.Usage,
            "oauth-connection-not-found",
            "Run `msagent deployment oauth list` to see configured connections, or configure it with `msagent deployment oauth create`.");

    public static DeploymentException OAuthServiceProviderNotFound(string serviceProvider) =>
        new(
            ErrorCodes.Deployment.DeploymentOAuthServiceProviderNotFound,
            $"The OAuth service provider '{serviceProvider}' was not found in the Azure Bot Service provider catalogue.",
            ExitCodes.Usage,
            "oauth-service-provider-not-found",
            "Run `msagent deployment oauth list-providers` to inspect the live provider catalogue.");

    public static DeploymentException ChannelNotFound(string deploymentId, string channelName) =>
        new(
            ErrorCodes.Deployment.DeploymentChannelNotFound,
            $"The {channelName} channel is not configured on deployment '{deploymentId}'.",
            ExitCodes.Usage,
            "channel-not-found",
            "Run `msagent deployment channel list` to see configured channels, or configure it with `msagent deployment channel create`.");

    public static DeploymentException ChannelDefinitionNotFound(string channelName) =>
        new(
            ErrorCodes.Deployment.DeploymentChannelDefinitionNotFound,
            $"The channel definition for '{channelName}' does not exist.",
            ExitCodes.Usage,
            "channel-definition-not-found",
            "Use a supported channel name.");

    public static DeploymentException ChannelOperationNotSupported(string channelName, string operation) =>
        new(
            ErrorCodes.Deployment.DeploymentChannelOperationNotSupported,
            $"{channelName} does not support {operation}.",
            ExitCodes.Usage,
            "channel-operation-not-supported");

    public static UsageException ParameterNotDeclared(
        string parameterName,
        string definitionName,
        bool useOAuthOption = false,
        string? guidance = null) =>
        new(WithGuidance(
            $"{OptionFor(parameterName, useOAuthOption)} is not declared by '{definitionName}'.",
            guidance), ErrorCodes.Deployment.DeploymentParameterNotDeclared);

    public static UsageException RequiredParameter(
        string parameterName,
        string definitionName,
        bool useOAuthOption = false,
        string? guidance = null) =>
        new(WithGuidance(
            $"{OptionFor(parameterName, useOAuthOption)} is required for '{definitionName}'.",
            guidance), ErrorCodes.Deployment.DeploymentRequiredParameter);

    public static UsageException ReadOnlyParameter(
        string parameterName,
        string definitionName) =>
        new($"{OptionFor(parameterName, useOAuthOption: false)} is read-only for '{definitionName}' and cannot be created or updated.", ErrorCodes.Deployment.DeploymentReadOnlyParameter);

    public static UsageException CannotClearRequiredParameter(
        string parameterName,
        string definitionName,
        bool useOAuthOption = false,
        string? guidance = null) =>
        new(WithGuidance(
            $"{OptionFor(parameterName, useOAuthOption)} is required for '{definitionName}' and cannot be cleared.",
            guidance), ErrorCodes.Deployment.DeploymentCannotClearRequiredParameter);

    public static UsageException InvalidParameterValue(
        string parameterName,
        string definitionName,
        bool useOAuthOption = false,
        string? guidance = null) =>
        new(WithGuidance(
            $"{OptionFor(parameterName, useOAuthOption)} for '{definitionName}' must be true or false.",
            guidance), ErrorCodes.Deployment.DeploymentInvalidParameterValue);

    private static string OptionFor(
        string parameterName,
        bool useOAuthOption) =>
        useOAuthOption
            ? CommonOAuthParameters.OptionFor(parameterName)
            : $"--parameters {parameterName}=...";

    private static string WithGuidance(string message, string? guidance) =>
        guidance is null ? message : $"{message} {guidance}";
}
