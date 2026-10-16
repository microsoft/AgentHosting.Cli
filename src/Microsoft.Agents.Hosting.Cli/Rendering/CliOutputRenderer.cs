using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>
/// The command host's output and error contract. It owns the two
/// JSON envelope shapes and the human stdout/stderr split, so command handlers never format output
/// themselves:
///
/// <list type="bullet">
///   <item>JSON success — <c>{ "success": true, ...payload }</c> to stdout.</item>
///   <item>JSON failure — <c>{ "success": false, "exitCode", "errorMessage", "errorKind"?,
///     "remediation"? }</c> to stdout.</item>
///   <item>Human success — payload lines to stdout.</item>
///   <item>Human failure — the error (and any remediation) to stderr.</item>
/// </list>
///
/// ANSI color is suppressed when the <c>color</c> constructor argument is <see langword="false"/>
/// (<c>--no-color</c> / <c>NO_COLOR</c> / non-TTY, decided by <see cref="Hosting.ConsoleColorPolicy"/>).
/// </summary>
public sealed class CliOutputRenderer
{
    // 2-space indent plus the relaxed encoder so output matches the shipped TypeScript's
    // JSON.stringify(x, null, 2): apostrophes, <, >, & and non-ASCII are emitted raw rather than
    // \uXXXX-escaped. This keeps the JSON envelopes byte-comparable against the provisional oracle.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Red = "31";
    private const string Green = "32";
    private const string Cyan = "36";

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _color;

    public CliOutputRenderer(TextWriter stdout, TextWriter stderr, bool color)
    {
        _stdout = stdout;
        _stderr = stderr;
        _color = color;
    }

    internal TextWriter Stdout => _stdout;
    internal TextWriter Stderr => _stderr;
    internal bool Color => _color;

    /// <summary>Renders a successful command result as the JSON or human success envelope.</summary>
    public void RenderSuccess(object payload, bool json, bool debug = false)
    {
        if (debug && payload is IDeploymentResult deploymentResult)
        {
            var summary = DescribeDeploymentSummary(deploymentResult);
            if (summary is not null)
            {
                _stderr.WriteLine(summary);
            }

            if (deploymentResult.ArmResponse is { } armResponse)
            {
                if (summary is not null)
                {
                    _stderr.WriteLine();
                }

                _stderr.WriteLine("ARM response:");
                _stderr.WriteLine(JsonSerializer.Serialize(armResponse, IndentedJson));
            }

            return;
        }

        if (json)
        {
            WriteJsonSuccess(payload);
        }
        else
        {
            WriteHumanSuccess(payload);
        }
    }

    /// <summary>
    /// Renders a failure and returns the process exit code. Typed <see cref="AgentHostingException"/>
    /// failures carry their own exit code, error kind, and remediation; every other exception maps to
    /// <see cref="ExitCodes.Failure"/> with no kind or remediation (the generic guard).
    /// </summary>
    public int RenderFailure(Exception error, bool json)
    {
        int exitCode;
        string? errorKind;
        string? remediation;
        object? partialReport = null;

        if (error is AgentHostingException typed)
        {
            exitCode = typed.ExitCode;
            errorKind = typed.ErrorKind;
            remediation = typed.Remediation;
            partialReport = typed switch
            {
                BlueprintIdentityProvisioningException blueprint => blueprint.PartialReport,
                AppRegistrationIdentityProvisioningException appReg => appReg.PartialReport,
                PackagePublicationException publication => publication.PartialResult,
                _ => null,
            };
        }
        else
        {
            exitCode = ExitCodes.Failure;
            errorKind = null;
            remediation = null;
        }

        WriteFailure(exitCode, Describe(error), errorKind, remediation, partialReport, json, error is ManagedAgentException { HasMultilineGuidance: true });
        return exitCode;
    }

    private static string Describe(Exception error)
    {
        if (error is AgentHostingException)
        {
            return error.Message;
        }

        var messages = new List<string>();

        for (var current = error; current is not null; current = current.InnerException)
        {
            messages.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", messages);
    }

    /// <summary>Renders a usage failure (parse error, unknown/missing command) and returns exit 2.</summary>
    public int RenderUsageError(string message, bool json)
    {
        WriteFailure(
            ExitCodes.Usage,
            message,
            errorKind: null,
            remediation: null,
            partialReport: null,
            json);
        return ExitCodes.Usage;
    }

    private void WriteFailure(int exitCode, string message, string? errorKind, string? remediation, object? partialReport, bool json, bool multiline = false)
    {
        if (json)
        {
            var envelope = new JsonObject
            {
                ["success"] = false,
                ["exitCode"] = exitCode,
                ["errorMessage"] = message,
            };
            if (errorKind is not null)
            {
                envelope["errorKind"] = errorKind;
            }

            if (remediation is not null)
            {
                envelope["remediation"] = remediation;
            }

            if (partialReport is not null)
            {
                envelope["partialResult"] = JsonSerializer.SerializeToNode(
                    partialReport,
                    partialReport.GetType(),
                    CliJsonContext.Default);
            }

            // The JSON envelope (success or failure) is always the single stdout document, so a parser
            // reading the stdout pipe still sees the failure.
            _stdout.WriteLine(envelope.ToJsonString(IndentedJson));
        }
        else
        {
            WriteFailureLines(message, Red, multiline);
            if (remediation is not null)
            {
                WriteFailureLines(remediation, Cyan, multiline);
            }

            if (partialReport is not null)
            {
                foreach (var line in DescribeHuman(partialReport))
                {
                    WriteLine(_stderr, line);
                }
            }
        }
    }

    private void WriteFailureLines(string text, string ansiCode, bool multiline)
    {
        foreach (var line in multiline ? text.Split(Environment.NewLine) : [text])
        {
            WriteLine(_stderr, line, ansiCode);
        }
    }

    private void WriteJsonSuccess(object payload)
    {
        JsonObject node;
        if (payload is IDeploymentResult deploymentResult)
        {
            node = BuildDeploymentNode(deploymentResult);
        }
        else
        {
            var serializedPayload = JsonSerializer.SerializeToNode(
                payload,
                payload.GetType(),
                CliJsonContext.Default);
            if (serializedPayload is not JsonObject objectPayload)
            {
                var resultEnvelope = new JsonObject
                {
                    ["success"] = true,
                    ["result"] = serializedPayload,
                };
                _stdout.WriteLine(resultEnvelope.ToJsonString(IndentedJson));
                return;
            }

            node = objectPayload;
        }

        // Prepend the reserved "success" key, then move the payload's properties in declared order so
        // the envelope reads { "success": true, "schemaVersion", "status", ...payload }.
        var envelope = new JsonObject { ["success"] = true };

        // Every deployment result carries its exact human-readable view under "message", so the JSON is a
        // strict superset of the human output (JSON = the human view plus the structured fields).
        var deploymentMessage = payload is IDeploymentResult deploymentResultPayload
            ? DescribeDeploymentSummary(deploymentResultPayload)
            : null;
        if (deploymentMessage is not null)
        {
            envelope["message"] = deploymentMessage;
        }

        foreach (var key in node.Select(kv => kv.Key).ToArray())
        {
            var value = node[key];
            node.Remove(key);
            envelope[key] = value;
        }

        _stdout.WriteLine(envelope.ToJsonString(IndentedJson));
    }

    private void WriteHumanSuccess(object payload)
    {
        var first = true;
        foreach (var line in DescribeHuman(payload))
        {
            WriteLine(_stdout, line, first ? Green : null);
            first = false;
        }
    }

    /// <summary>Renders a payload's human-readable view, falling back to its string form.</summary>
    private static IReadOnlyList<string> DescribeHuman(object payload) => payload switch
    {
        IDeploymentResult result => DescribeDeploymentResult(result),
        ITextGridRenderable renderable => renderable.ToTextLines(),
        _ => DescribeJson(payload),
    };

    private static List<string> DescribeJson(object payload) =>
        (JsonSerializer.SerializeToNode(payload, payload.GetType(), CliJsonContext.Default)
             ?.ToJsonString(IndentedJson) ?? "null")
        .Replace("\r\n", "\n")
        .Split('\n')
        .ToList();

    private static JsonObject BuildDeploymentNode(IDeploymentResult result)
    {
        var node = new JsonObject();
        if (result.Payload is not null)
        {
            node[JsonNamingPolicy.CamelCase.ConvertName(result.TargetName)] = JsonSerializer.SerializeToNode(
                result.Payload,
                result.Payload.GetType(),
                CliJsonContext.Default);
        }

        return node;
    }

    private static List<string> DescribeDeploymentResult(IDeploymentResult result)
    {
        var lines = new List<string>();
        var summary = DescribeDeploymentSummary(result);
        if (summary is not null)
        {
            lines.Add(summary);
        }

        if (result.Payload is not null)
        {
            var payloadNode = JsonSerializer.SerializeToNode(
                result.Payload,
                result.Payload.GetType(),
                CliJsonContext.Default);
            lines.AddRange((payloadNode?.ToJsonString(IndentedJson) ?? "{}").Replace("\r\n", "\n").Split('\n'));
        }

        if (lines.Count == 0)
        {
            lines.Add("OK");
        }

        return lines;
    }

    private static string? DescribeDeploymentSummary(IDeploymentResult result)
    {
        var operation = result.Status switch
        {
            DeploymentOperationStatus.Created => "created",
            DeploymentOperationStatus.Existed => "already exists",
            DeploymentOperationStatus.Updated => "updated",
            DeploymentOperationStatus.Deleted => "deleted",
            _ => null,
        };
        if (operation is null)
        {
            return null;
        }

        var subject = result.TargetInstanceName is null
            ? result.TargetName
            : $"{result.TargetName} '{result.TargetInstanceName}'";
        return $"{subject} {operation}.";
    }

    private string Colorize(string text, string ansiCode) =>
        _color ? $"\u001b[{ansiCode}m{text}\u001b[0m" : text;

    private void WriteLine(TextWriter writer, string text, string? ansiCode = null)
    {
        var sanitized = TerminalText.Sanitize(text);
        writer.WriteLine(ansiCode is null ? sanitized : Colorize(sanitized, ansiCode));
    }
}
