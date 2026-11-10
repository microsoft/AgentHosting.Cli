using System.Globalization;
using System.Text.Json;
using System.Web;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

internal static class AmsMcsWire
{
    internal static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    private static readonly string[] AgentTypeNames = [AgentTypes.CodeAgent, AgentTypes.McsAgent, AgentTypes.McsWorkflow];
    private static readonly string[] NoticeKinds = [AmsMcsOperationNoticeKind.FlowLeftInDraft, AmsMcsOperationNoticeKind.CollectionSharedWithOtherBots, AmsMcsOperationNoticeKind.PushWarning];
    private static readonly string[] CloudStates = ["NotDeployed", "Deployed"];
    private static readonly string[] HolderStates = [AmsMcsHolderState.Pending, AmsMcsHolderState.Active, AmsMcsHolderState.Releasing];

    internal static AmsMcsAgentRecord ReadAgent(JsonElement root) => ReadAgent(root, null);

    internal static AmsMcsAgentRecord ReadAgent(JsonElement root, string? entityTag)
    {
        var sourceControl = OptionalObject(root, "sourceControl");
        var mcs = OptionalObject(root, "mcs");
        return new AmsMcsAgentRecord
        {
            AgentId = RequiredString(root, "id"),
            TenantId = RequiredString(root, "tenantId"),
            EnvironmentId = RequiredString(root, "environmentId"),
            DisplayName = RequiredString(root, "displayName"),
            Description = OptionalString(root, "description"),
            AgentType = EnumText(root, "agentType", AgentTypeNames) ?? throw new FormatException("agentType is missing."),
            RepositoryId = OptionalString(root, "repositoryId"),
            SourceControl = sourceControl is null ? null : new AmsMcsSourceControl
            {
                RepositoryId = OptionalString(sourceControl, "repositoryId"),
                GitUrl = OptionalString(sourceControl, "gitUrl"),
                Branch = OptionalString(sourceControl, "branch"),
                Directory = OptionalString(sourceControl, "directory"),
                RepositoryCreated = sourceControl is { } created && OptionalBoolean(created, "repositoryCreated"),
            },
            Mcs = mcs is null ? null : new AmsMcsAgentProperties
            {
                SchemaName = OptionalString(mcs, "schemaName"),
                AuthoringShape = EnumText(mcs, "authoringShape"),
                HomeDeploymentId = OptionalString(mcs, "homeDeploymentId"),
            },
            ChangedTime = OptionalDate(root, "changedTime"),
            ETag = NullIfBlank(entityTag) ?? OptionalString(root, "eTag"),
            Deployments = [.. OptionalArray(root, "deployments").Select(ReadDeployment)],
        };
    }

    internal static AmsMcsRepository ReadRepository(JsonElement root) => new()
    {
        RepositoryId = RequiredString(root, "repositoryId"),
        Name = OptionalString(root, "name"),
        GitUrl = RequiredString(root, "gitUrl"),
        Branch = OptionalString(root, "branch"),
        HeadCommitSha = OptionalString(root, "headCommitSha"),
        AgentFolderExists = OptionalBoolean(root, "agentFolderExists"),
        Agents = [.. OptionalArray(root, "agents").Select(ReadAgentListItem)],
    };

    internal static AmsMcsAgentPage ReadAgentPage(JsonElement root) => new()
    {
        Agents = [.. RequiredArray(root, "value").Select(ReadAgentListItem)],
        HasMore = HasNextLink(root),
        NextSkipToken = NextSkipToken(root),
    };

    internal static IReadOnlyList<AmsMcsHolder> ReadHolders(JsonElement root) => [.. RequiredArray(root, "value").Select(ReadHolder)];

    internal static AmsMcsOperationRecord ReadOperation(JsonElement root)
    {
        var error = OptionalObject(root, "error");
        return new AmsMcsOperationRecord
        {
            OperationId = RequiredString(root, "id"),
            Kind = EnumText(root, "kind") ?? throw new FormatException("kind is missing."),
            Status = EnumText(root, "status") ?? throw new FormatException("status is missing."),
            AgentId = RequiredString(root, "agentId"),
            DeploymentId = OptionalString(root, "deploymentId"),
            SourceCommitSha = OptionalString(root, "sourceCommitSha"),
            ResultCommitSha = OptionalString(root, "resultCommitSha"),
            CapturedCommitSha = OptionalString(root, "capturedCommitSha"),
            MergedCommitSha = OptionalString(root, "mergedCommitSha"),
            LastPublishedTime = OptionalDate(root, "lastPublishedTime"),
            StartedBy = OptionalPrincipal(root, "startedBy"),
            StartedTime = OptionalDate(root, "startedTime"),
            LastHeartbeatTime = OptionalDate(root, "lastHeartbeatTime"),
            CompletedTime = OptionalDate(root, "completedTime"),
            Publish = OptionalBoolean(root, "publish", whenAbsent: true),
            Error = error is not { } failure ? null : ReadError(failure),
            Notices = [.. OptionalArray(root, "notices").Select(notice => new AmsMcsOperationNotice
            {
                Kind = EnumText(notice, "kind", NoticeKinds) ?? throw new FormatException("A notice has no kind."),
                Target = OptionalString(notice, "target"),
                RelatedItems = OptionalStrings(notice, "relatedItems"),
                Code = OptionalString(notice, "code"),
                Detail = OptionalString(notice, "detail"),
                Message = OptionalString(notice, "message"),
            })],
        };
    }

    internal static AmsMcsOperationAccepted ReadEndedOperation(JsonElement root)
    {
        var operation = ReadOperation(root);
        return new() { OperationId = operation.OperationId, Status = operation.Status, SourceCommitSha = operation.SourceCommitSha, Finished = operation };
    }

    internal static AmsMcsOperationAccepted ReadOperationAccepted(JsonElement root) => new()
    {
        OperationId = OptionalString(root, "operationId") ?? RequiredString(root, "id"),
        Status = EnumText(root, "status") ?? throw new FormatException("status is missing."),
        SourceCommitSha = OptionalString(root, "sourceCommitSha"),
    };

    internal static AmsMcsDeploymentRecord ReadDeployment(JsonElement root)
    {
        var mcs = OptionalObject(root, "mcs");
        return new AmsMcsDeploymentRecord
        {
            DeploymentId = RequiredString(root, "id"),
            AgentId = RequiredString(root, "agentId"),
            HomeEnvironmentId = RequiredString(root, "homeEnvironmentId"),
            EnvironmentId = RequiredString(root, "environmentId"),
            DeploymentName = RequiredString(root, "deploymentName"),
            DeploymentType = EnumText(root, "deploymentType", DeploymentStages.ServiceNames) ?? throw new FormatException("deploymentType is missing."),
            AgentType = EnumText(root, "agentType", AgentTypeNames),
            State = EnumText(mcs, "state"),
            BotId = OptionalString(mcs, "botId"),
            WorkflowId = OptionalString(mcs, "workflowId"),
            SchemaName = OptionalString(mcs, "schemaName"),
            LastDeployedCommitSha = OptionalString(mcs, "lastDeployedCommitSha"),
            LastDeployOrigin = EnumText(mcs, "lastDeployOrigin"),
            CloudBaseCommitSha = OptionalString(mcs, "cloudBaseCommitSha"),
            CloudBaseExact = mcs is { } properties ? OptionalNullableBoolean(properties, "cloudBaseExact") : null,
            LastDeployedBy = OptionalPrincipal(root, "lastDeployedById"),
            LastDeployedTime = OptionalDate(root, "lastDeployedTime"),
        };
    }

    internal static string? AuthoringShape(string? shape) => shape switch
    {
        "Classic" => "empty",
        "CliCopilot" => "cliagent",
        _ => null,
    };

    internal static T Parse<T>(string body, Func<JsonElement, T> read)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Object ? read(document.RootElement) : throw new FormatException("The response is not a JSON object.");
    }

    internal static AmsMcsOperationError? ReadErrorBody(string? body)
    {
        try
        {
            return string.IsNullOrWhiteSpace(body) ? null : Parse(body, root => OptionalObject(root, "error") is { } failure ? ReadError(failure) : null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            var (code, message) = TargetRequestException.ParseTargetError(body);
            return code is null && message is null ? null : new AmsMcsOperationError { Code = code, Message = message };
        }
    }

    private static bool HasNextLink(JsonElement root) => !string.IsNullOrWhiteSpace(NextLink(root));

    private static string? NextLink(JsonElement root) => OptionalString(root, "nextLink") ?? OptionalString(root, "@odata.nextLink");

    private static string? NextSkipToken(JsonElement root) => SkipToken(NextLink(root));

    private static AmsMcsAgentListItem ReadAgentListItem(JsonElement item)
    {
        var agent = ReadAgent(item);
        return new AmsMcsAgentListItem
        {
            AgentId = agent.AgentId,
            TenantId = agent.TenantId,
            EnvironmentId = agent.EnvironmentId,
            DisplayName = agent.DisplayName,
            Description = agent.Description,
            AgentType = agent.AgentType,
            SchemaName = OptionalString(item, "schemaName") ?? agent.Mcs?.SchemaName,
            RepositoryId = agent.RepositoryId ?? agent.SourceControl?.RepositoryId,
            AgentFolder = OptionalString(item, "agentFolder") ?? agent.SourceControl?.Directory,
            Branch = OptionalString(item, "branch") ?? agent.SourceControl?.Branch,
            HomeDeploymentId = OptionalString(item, "homeDeploymentId") ?? agent.Mcs?.HomeDeploymentId,
            DeploymentCount = OptionalInt32(item, "deploymentCount"),
            CloudState = Canonical(EnumText(item, "cloudState", CloudStates), CloudStates),
            Deployments = agent.Deployments,
        };
    }

    private static AmsMcsHolder ReadHolder(JsonElement holder) => new()
    {
        AgentId = RequiredString(holder, "agentId"),
        HomeEnvironmentId = RequiredString(holder, "homeEnvironmentId"),
        DeploymentId = OptionalString(holder, "deploymentId"),
        State = Canonical(EnumText(holder, "state", HolderStates), HolderStates) ?? throw new FormatException("state is missing."),
        Agent = OptionalObject(holder, "agent") is { } agent ? ReadAgentListItem(agent) : null,
    };

    private static AmsMcsOperationError ReadError(JsonElement failure) => new()
    {
        Code = OptionalString(failure, "code"),
        Message = OptionalString(failure, "message"),
        Details = [.. OptionalArray(failure, "details").Select(detail => new AmsMcsOperationErrorDetail(OptionalString(detail, "code"), OptionalString(detail, "message")))],
    };

    private static string? SkipToken(string? nextLink)
    {
        if (nextLink is null)
        {
            return null;
        }

        var queryStart = nextLink.IndexOf('?', StringComparison.Ordinal);
        return NullIfBlank(queryStart < 0 ? null : HttpUtility.ParseQueryString(nextLink[(queryStart + 1)..])["$skiptoken"]) ?? throw new FormatException("nextLink carries no $skiptoken.");
    }

    private static JsonElement[] RequiredArray(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? Objects(value, name)
            : throw new FormatException($"{name} is missing.");

    private static JsonElement[] OptionalArray(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array ? Objects(value, name) : throw new FormatException($"{name} is invalid.");
    }

    private static JsonElement[] Objects(JsonElement array, string name) =>
        [.. array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object ? item : throw new FormatException($"{name} holds a non-object item."))];

    private static IReadOnlyList<string> OptionalStrings(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : throw new FormatException($"{name} holds a non-string item.")).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!)]
            : throw new FormatException($"{name} is invalid.");
    }

    private static JsonElement? OptionalObject(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object ? value : throw new FormatException($"{name} is invalid.");
    }

    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) ?? throw new FormatException($"{name} is missing.");

    private static string? OptionalString(JsonElement? root, string name) => root is { } value ? OptionalString(value, name) : null;

    private static string? EnumText(JsonElement? root, string name) => root is { } value ? EnumText(value, name, []) : null;

    private static string? OptionalString(JsonElement root, string name) => NullIfBlank(OptionalRawString(root, name));

    private static string? OptionalRawString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name} is invalid.");
    }

    private static bool OptionalBoolean(JsonElement root, string name, bool whenAbsent = false)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return whenAbsent;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{name} is invalid."),
        };
    }

    private static bool? OptionalNullableBoolean(JsonElement root, string name) => TryGetProperty(root, name, out var value) && value.ValueKind != JsonValueKind.Null ? OptionalBoolean(root, name) : null;

    private static int? OptionalInt32(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : throw new FormatException($"{name} is invalid.");
    }

    private static string? OptionalPrincipal(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Object ? OptionalString(value, "objectId") : OptionalString(root, name);

    private static string? Canonical(string? value, string[] names) => value is null ? null : names.FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) ?? value;

    private static string? EnumText(JsonElement root, string name, IReadOnlyList<string> namesByValue)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NullIfBlank(value.GetString()),
            JsonValueKind.Number when value.TryGetInt32(out var numeric) => numeric >= 0 && numeric < namesByValue.Count ? namesByValue[numeric] : numeric.ToString(CultureInfo.InvariantCulture),
            _ => throw new FormatException($"{name} is invalid."),
        };
    }

    private static DateTimeOffset? OptionalDate(JsonElement root, string name) =>
        OptionalString(root, name) is { } text
            ? DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : throw new FormatException($"{name} is invalid.")
            : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}