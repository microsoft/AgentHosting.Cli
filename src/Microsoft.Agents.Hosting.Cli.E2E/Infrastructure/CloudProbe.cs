using System.Diagnostics;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Shells out to <c>az</c>, which the harness needs because <c>ah</c> cannot delete a bot or an app.</summary>
public sealed class AzureCli
{
    private static readonly Lazy<string?> Executable = new(Locate);

    private readonly Transcript _transcript;

    public AzureCli(Transcript transcript) => _transcript = transcript;

    /// <summary>Whether a working <c>az</c> was found.</summary>
    public static bool IsPresent() => Executable.Value is not null;

    /// <summary>The resolved executable, reported by <c>plan</c> so a missing one is diagnosable.</summary>
    public static string? Path => Executable.Value;

    /// <summary>Runs an <c>az</c> command, returning the result rather than throwing on failure.</summary>
    public async Task<MsagentResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var executable = Executable.Value
            ?? throw new E2EException("az was not found; it is required for Azure and Entra cleanup.");

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await ProcessRunner
            .RunAsync(startInfo, arguments, stdin: null, TimeSpan.FromMinutes(5), _transcript, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? Locate() => Candidates().FirstOrDefault(Works);

    /// <summary>
    /// Yields absolute paths only. The Windows launcher is a batch file that resolves its bundled
    /// Python through <c>%~dp0</c>, so invoking it by bare name fails with "Failed to load python
    /// executable".
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "az.cmd", "az.bat", "az.exe" } : ["az"];

        foreach (var directory in SearchDirectories())
        {
            foreach (var name in names)
            {
                var candidate = System.IO.Path.Combine(directory, name);

                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return directory;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        })
        {
            if (root is { Length: > 0 })
            {
                yield return System.IO.Path.Combine(root, "Microsoft SDKs", "Azure", "CLI2", "wbin");
            }
        }
    }

    private static bool Works(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(120000) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Reads cloud state directly, for assertions a command's own output cannot settle.</summary>
public sealed class CloudProbe
{
    private readonly MsagentCli _msagent;
    private readonly AzureCli _azure;

    public CloudProbe(MsagentCli msagent, AzureCli azure)
    {
        _msagent = msagent;
        _azure = azure;
    }

    /// <summary>Whether an agent is absent from an environment, polled because deletion is eventually consistent.</summary>
    public async Task<bool> AgentGoneAsync(string environmentId, string agentId, CancellationToken cancellationToken = default)
    {
        var result = await _msagent
            .RunAsync(["agent", "list", "--environment-id", environmentId, "--json"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode != 0 || !MentionsId(result.StdOut, agentId);
    }

    /// <summary>Whether an agent is present in an environment.</summary>
    public async Task<bool> AgentExistsAsync(string environmentId, string agentId, CancellationToken cancellationToken = default)
    {
        var result = await _msagent
            .RunAsync(["agent", "list", "--environment-id", environmentId, "--json"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && MentionsId(result.StdOut, agentId);
    }

    /// <summary>Whether an agent with a schema name already exists, which a deploy would silently adopt.</summary>
    public async Task<bool> SchemaNameExistsAsync(string environmentId, string schemaName, CancellationToken cancellationToken = default)
    {
        var result = await _msagent
            .RunAsync(["agent", "list", "--environment-id", environmentId, "--json"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && result.StdOut.Contains($"\"{schemaName}\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a standalone workflow is present in an environment.
    ///
    /// Only a listing that parses, carries an array of rows and says it was not truncated can prove
    /// a workflow is gone. Anything else is unknown, and cleanup deletes on unknown: removing a flow
    /// that is already gone is stated and harmless, where skipping one that is still there leaks it.
    /// </summary>
    public async Task<bool?> WorkflowExistsAsync(string environmentId, string workflowId, CancellationToken cancellationToken = default)
    {
        var result = await _msagent
            .RunAsync(["agent", "list", "--agent-type", "MCSWorkflow", "--environment-id", environmentId, "--limit", ListingLimit, "--json"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode != 0 ? null : ReadListing(result.StdOut, "mcsWorkflowId", workflowId);
    }

    /// <summary>The ceiling the probes list under, high enough that a busy environment still fits.</summary>
    private const string ListingLimit = "500";

    private static bool? ReadListing(string json, string property, string id)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            if (agents.EnumerateArray().Any(element => Matches(element, property, id)))
            {
                return true;
            }

            return root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? truncated.ValueKind == JsonValueKind.True ? null : false
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>az bot show</c> arguments, scoped to the subscription the bot was created in.</summary>
    /// <param name="botName">The bot resource name.</param>
    /// <param name="resourceGroup">The resource group holding it.</param>
    /// <param name="subscriptionId">The subscription it lives in, or null for the Azure CLI default.</param>
    private static IReadOnlyList<string> BotArguments(string botName, string resourceGroup, string? subscriptionId) =>
        subscriptionId is { Length: > 0 }
            ? ["bot", "show", "--name", botName, "--resource-group", resourceGroup, "--subscription", subscriptionId]
            : ["bot", "show", "--name", botName, "--resource-group", resourceGroup];

    /// <summary>Reads a bot's messaging endpoint from ARM, for the round-trip assertions.</summary>
    public async Task<string?> BotEndpointAsync(string botName, string resourceGroup, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var result = await _azure
            .RunAsync([.. BotArguments(botName, resourceGroup, subscriptionId), "--query", "properties.endpoint", "-o", "tsv"], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } endpoint ? endpoint : null;
    }

    /// <summary>Whether a bot exists in a resource group.</summary>
    public async Task<bool> BotExistsAsync(string botName, string resourceGroup, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var result = await _azure
            .RunAsync([.. BotArguments(botName, resourceGroup, subscriptionId), "-o", "none"], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0;
    }

    /// <summary>Reads a bot property from ARM, for assertions about SKU and app id.</summary>
    public async Task<string?> BotPropertyAsync(string botName, string resourceGroup, string query, string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var result = await _azure
            .RunAsync([.. BotArguments(botName, resourceGroup, subscriptionId), "--query", query, "-o", "tsv"], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } value ? value : null;
    }

    /// <summary>Creates a throwaway app registration, because Azure permits only one bot per app id.</summary>
    public async Task<string> CreateAppRegistrationAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var result = await _azure
            .RunAsync(["ad", "app", "create", "--display-name", displayName, "--sign-in-audience", "AzureADMyOrg", "--query", "appId", "-o", "tsv"], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } applicationId
            ? applicationId
            : throw new E2EException($"Could not create the app registration {displayName}: {result.StdErr.Trim()}");
    }

    private static bool MentionsId(string json, string agentId) => Mentions(json, "mcsAgentId", agentId);

    /// <summary>
    /// Matches a listed row on the service-assigned id named by <paramref name="property"/>. A
    /// cloud-listed row carries a null <c>agentId</c> — that is the local record id, and only a
    /// locally registered agent has one.
    /// </summary>
    private static bool Mentions(string json, string property, string id)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.TryGetProperty("agents", out var agents)
                && agents.EnumerateArray().Any(element => Matches(element, property, id));
        }
        catch (JsonException)
        {
            return json.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return json.Contains(id, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool Matches(JsonElement element, string property, string id) =>
        element.TryGetProperty(property, out var cloudId)
        && string.Equals(cloudId.GetString(), id, StringComparison.OrdinalIgnoreCase);
}
