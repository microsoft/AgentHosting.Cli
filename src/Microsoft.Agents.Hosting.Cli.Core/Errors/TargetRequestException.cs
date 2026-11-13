using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>
/// Category of a direct-target REST failure (ARM or Graph). A faithful C# port of the shipped
/// TypeScript <c>TargetErrorKind</c>: it distinguishes the operator-facing
/// conditions the CLI must surface clearly, plus a catch-all, and each maps to a frozen 3/2/1 exit
/// code so both implementations classify the same HTTP condition identically.
/// </summary>
public enum TargetErrorKind
{
    /// <summary>401 — token missing/invalid/expired; re-auth required (exit 3).</summary>
    AuthExpired,

    /// <summary>401 — token issued for the wrong audience/resource; a wiring/config fix (exit 2).</summary>
    WrongAudience,

    /// <summary>403 — the operator lacks the required RBAC/directory permission (exit 1).</summary>
    PermissionDenied,

    /// <summary>403 — admin consent for the required scope is missing (exit 1).</summary>
    ConsentRequired,

    /// <summary>Any other non-2xx response (exit 1).</summary>
    RequestFailed,
}

/// <summary>
/// A failure returned by a direct-target REST call (ARM or Graph). Extends
/// <see cref="AgentHostingException"/> so it carries the shared 3/2/1 exit-code taxonomy;
/// <see cref="Kind"/> and <see cref="Status"/> let callers and tests branch without string-matching a
/// message. Messages are secret-free by construction: they never include the bearer token, the
/// request body, or any returned credential material. A faithful C# port of the shipped TypeScript
/// <c>TargetRequestError</c> and <c>mapTargetResponseError</c>.
/// </summary>
public sealed class TargetRequestException : AgentHostingException
{
    public TargetRequestException(string message, int exitCode, TargetErrorKind kind, int status, string? serviceMessage = null)
        : base(message, exitCode, ToErrorKind(kind), errorCode: ToErrorCode(kind))
    {
        Kind = kind;
        Status = status;
        ServiceMessage = serviceMessage;
    }

    /// <summary>The classified failure category.</summary>
    public TargetErrorKind Kind { get; }

    /// <summary>The HTTP status code of the failing response.</summary>
    public int Status { get; }

    /// <summary>
    /// The verbatim free-text <c>message</c> from the ARM/Graph error envelope
    /// (<c>{ "error": { code, message } }</c>), when present. It is not part of the default
    /// <see cref="System.Exception.Message"/> (which stays terse and echoes only the short <c>code</c>);
    /// callers that want to relay the service's own explanation (for example the exact
    /// <c>ResourceNotFound</c> text) can surface this. It never contains bearer tokens or credential
    /// material — only the service's error text, which may include resource/subscription ids.
    /// </summary>
    public string? ServiceMessage { get; }

    private static int ToErrorCode(TargetErrorKind kind) => kind switch
    {
        TargetErrorKind.AuthExpired => ErrorCodes.Environment.TargetAuthExpired,
        TargetErrorKind.WrongAudience => ErrorCodes.Environment.TargetWrongAudience,
        TargetErrorKind.PermissionDenied => ErrorCodes.Environment.TargetPermissionDenied,
        TargetErrorKind.ConsentRequired => ErrorCodes.Environment.TargetConsentRequired,
        TargetErrorKind.RequestFailed => ErrorCodes.Environment.TargetRequestFailed,
        _ => ErrorCodes.Environment.TargetRequestFailed,
    };

    private static string ToErrorKind(TargetErrorKind kind) => kind switch
    {
        TargetErrorKind.AuthExpired => "auth-expired",
        TargetErrorKind.WrongAudience => "wrong-audience",
        TargetErrorKind.PermissionDenied => "permission-denied",
        TargetErrorKind.ConsentRequired => "consent-required",
        _ => "request-failed",
    };

    /// <summary>
    /// Extracts the <c>code</c>/<c>message</c> from an ARM or Graph error envelope
    /// (<c>{ "error": { code, message } }</c>), tolerating any non-conforming or non-JSON body. Only
    /// the short <c>code</c> is echoed into user-facing messages; the free-text <c>message</c> is used
    /// solely for classification, never surfaced verbatim (it can contain object/subscription ids).
    /// </summary>
    public static (string? Code, string? Message) ParseTargetError(string? bodyText)
    {
        if (string.IsNullOrEmpty(bodyText))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(bodyText);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                TryGetProperty(document.RootElement, "error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var code = TryGetProperty(error, "code", out var codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString()
                        : null;
                var message = TryGetProperty(error, "message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;
                return (code, message);
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — fall through to the empty result.
        }

        return (null, null);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
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

    /// <summary>
    /// Maps a non-2xx ARM/Graph response to a typed <see cref="TargetRequestException"/>. The mapping
    /// keys off the HTTP status plus the ARM/Graph error <c>code</c>/<c>message</c>, and produces an
    /// actionable, secret-free message that names the audience and (for 403s) the operation.
    /// </summary>
    public static TargetRequestException MapTargetResponseError(
        int status,
        string? bodyText,
        string method,
        string url,
        string audienceLabel,
        string binaryName)
    {
        var (code, message) = ParseTargetError(bodyText);
        var haystack = $"{code ?? string.Empty} {message ?? string.Empty}".ToLowerInvariant();
        var where = $"{method} {url}";
        var detail = code is not null
            ? $" ({audienceLabel} error code: {code})"
            : $" ({audienceLabel} HTTP {status})";

        if (status == 401)
        {
            if (haystack.Contains("audience", StringComparison.Ordinal))
            {
                return new TargetRequestException(
                    $"The access token acquired for {audienceLabel} was rejected: wrong audience/resource.{detail} " +
                    $"The adapter requested a token for the wrong resource — check the {audienceLabel} resource in the target configuration.",
                    ExitCodes.Usage,
                    TargetErrorKind.WrongAudience,
                    status,
                    message);
            }

            return new TargetRequestException(
                $"Authentication for {audienceLabel} was rejected (token missing, invalid, or expired).{detail} " +
                $"Run `{binaryName} auth create` for the intended account and cloud, then retry.",
                ExitCodes.NotSignedIn,
                TargetErrorKind.AuthExpired,
                status,
                message);
        }

        if (status == 403)
        {
            if (haystack.Contains("consent", StringComparison.Ordinal))
            {
                return new TargetRequestException(
                    $"Admin consent is required for the {audienceLabel} scope.{detail} " +
                    $"Ask a tenant administrator to grant consent for the {binaryName} application, then retry.",
                    ExitCodes.Failure,
                    TargetErrorKind.ConsentRequired,
                    status,
                    message);
            }

            return new TargetRequestException(
                $"The signed-in account lacks permission for this {audienceLabel} operation ({where}).{detail} " +
                $"Grant the operator the required {audienceLabel} role/permission " +
                "(e.g. an ARM role such as Contributor, or the Graph app-registration permission), then retry.",
                ExitCodes.Failure,
                TargetErrorKind.PermissionDenied,
                status,
                message);
        }

        if (haystack.Contains("locationnotavailableforresourcetype", StringComparison.Ordinal))
        {
            return new TargetRequestException(
                $"The resource type is not available in the requested location.{detail} " +
                $"Azure Bot Service is a global resource: pass `--location global`.",
                ExitCodes.Usage,
                TargetErrorKind.RequestFailed,
                status,
                message);
        }

        return new TargetRequestException(
            $"{audienceLabel} request failed ({where}) with HTTP {status}.{(code is not null ? $" Error code: {code}." : string.Empty)}",
            ExitCodes.Failure,
            TargetErrorKind.RequestFailed,
            status,
            message);
    }
}
