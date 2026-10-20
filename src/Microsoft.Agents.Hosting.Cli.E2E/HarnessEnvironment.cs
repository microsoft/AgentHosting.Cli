using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.E2E;

/// <summary>Assembles a run: locates the project, checks pre-flight, primes consent, and schedules scenarios.</summary>
public sealed class HarnessEnvironment
{
    private const string ProjectFileName = "Microsoft.Agents.Hosting.Cli.E2E.Tests.csproj";

    private HarnessEnvironment(RunContext run, Scenario[] scheduled)
    {
        Run = run;
        Scheduled = scheduled;
    }

    /// <summary>Everything the run shares.</summary>
    public RunContext Run { get; }

    /// <summary>The scenarios this invocation addresses, before capability filtering.</summary>
    public Scenario[] Scheduled { get; }

    /// <summary>The harness project directory, which holds the config and the fixtures.</summary>
    public static string ProjectDirectory { get; } = FindProjectDirectory(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;

    /// <summary>Where run folders are written.</summary>
    public static string RunsRoot { get; } = Path.Combine(ProjectDirectory, ".runs");

    /// <summary>
    /// Where a run installs the tool.
    ///
    /// The directory sits outside the run folder and is deliberately short. <c>dotnet tool install</c>
    /// nests a package under <c>.store/id/version/id/version/tools/...</c>, and the Windows loader
    /// refuses a native dependency it cannot reach inside <c>MAX_PATH</c>. It does not fail the
    /// process: it falls back to the OS search path, so an unrelated <c>msalruntime.dll</c> on
    /// <c>PATH</c> loads in place of the one the tool shipped, and the mismatch faults the process
    /// during shutdown.
    /// </summary>
    /// <param name="runId">The run whose tool directory is wanted.</param>
    public static string ToolDirectory(string runId) => Path.Combine(Path.GetTempPath(), "ahe2e", runId[^4..]);

    /// <summary>Finds the tool a previous run installed, so cleanup can reuse it.</summary>
    /// <param name="runId">The run that installed the tool.</param>
    public static string? InstalledBinary(string runId) => FindInstalledBinary(ToolDirectory(runId));

    public static string? FindInstalledBinary(string toolDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolDirectory);

        return MsagentCliBuilder.ExecutableNames
            .Select(name => Path.Combine(toolDirectory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Prepares a run, returning null when a pre-flight check fails.</summary>
    public static async Task<HarnessEnvironment?> PrepareAsync(CommandOptions options, bool plan, CancellationToken cancellationToken)
    {
        AnnounceCreatedConfig();

        var config = E2EConfig.Load(ProjectDirectory);
        var runId = options.RunId ?? $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..4]}";
        var runSuffix = runId[^4..];
        var runDirectory = Path.Combine(RunsRoot, runId);
        Directory.CreateDirectory(runDirectory);

        var transcript = new Transcript(runDirectory);

        if (!ValidateEnvironments(config))
        {
            return null;
        }

        var binary = await new MsagentCliBuilder(SourceRoot(ProjectDirectory), transcript).AcquireAsync(runDirectory, ToolDirectory(runId), options.MsagentPath, cancellationToken).ConfigureAwait(false);
        var msagent = new MsagentCli(binary.Path, transcript, CrashDumps.DirectoryFor(runDirectory));
        var azure = new AzureCli(transcript);
        var cloud = new CloudProbe(msagent, azure);

        var (signedIn, signedInDetail) = await ReadSignInAsync(msagent, cancellationToken).ConfigureAwait(false);
        var azureCliPresent = AzureCli.IsPresent();
        var capabilities = Capabilities.Resolve(config, Path.Combine(ProjectDirectory, "fixtures"), signedIn, signedInDetail, azureCliPresent);

        if (!signedIn)
        {
            Console.Error.WriteLine($"Not signed in: {signedInDetail}");
            Console.Error.WriteLine("Run `msagent auth login`, then try again.");
            return null;
        }

        WarnAboutPreviousRuns(runId);

        if (capabilities.Has(Capability.AzureRead)
            && !await ArmConsentGrantedAsync(msagent, config, cancellationToken).ConfigureAwait(false))
        {
            capabilities = Capabilities.Resolve(config, Path.Combine(ProjectDirectory, "fixtures"), signedIn, signedInDetail, azureCliPresent, armConsentGranted: false);
        }

        var run = new RunContext
        {
            RunId = runId,
            RunSuffix = runSuffix,
            RunDirectory = runDirectory,
            WorkspaceRoot = Path.Combine(runDirectory, "ws"),
            Config = config,
            Capabilities = capabilities,
            Binary = binary,
            Msagent = msagent,
            Azure = azure,
            Cloud = cloud,
            Ledger = Ledger.Create(runDirectory, runId),
            Workspaces = new Workspaces(Path.Combine(ProjectDirectory, "fixtures"), Path.Combine(runDirectory, "ws"), config, runSuffix),
            Session = new SessionResources(),
            Transcript = transcript,
            Reporter = new Reporter(!options.NoColour),
        };

        return new HarnessEnvironment(run, [.. ScenarioCatalogue.Select(options.Filters)]);
    }

    private static void AnnounceCreatedConfig()
    {
        if (E2EConfig.CreateFromTemplate(ProjectDirectory) is not { } created)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Created {created} from the template.");
        Console.WriteLine("It is gitignored. Fill in tenantId and mcs.devEnvironmentId, then run again —");
        Console.WriteLine("`msagent env list` prints both. Every field is documented in the file.");
        Console.WriteLine();
    }

    private static bool ValidateEnvironments(E2EConfig config)    {
        if (config.Mcs.DevEnvironmentId is { Length: > 0 } dev
            && config.Mcs.TestEnvironmentId is { Length: > 0 } test
            && string.Equals(dev, test, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("mcs.devEnvironmentId and mcs.testEnvironmentId are the same; promotion needs two environments.");
            return false;
        }

        return ValidateWorkspaces(config);
    }

    /// <summary>Refuses a custom workspace override, which the suite cannot yet rebrand safely.</summary>
    /// <param name="config">The loaded configuration.</param>
    private static bool ValidateWorkspaces(E2EConfig config)
    {
        var overridden = new[]
        {
            ("mcs.workspaces.cliAgent", config.Mcs.Workspaces.CliAgent),
            ("mcs.workspaces.classicAgent", config.Mcs.Workspaces.ClassicAgent),
        };

        if (overridden.FirstOrDefault(candidate => candidate.Item2 is { Length: > 0 }) is not { Item2: { Length: > 0 } } supplied)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"{supplied.Item1} is not supported yet. Rebranding only rewrites the bundled fixture's name, so a custom workspace keeps its own schema name: " +
            "the suite would deploy over the agent it already names, and cleanup would delete it. Remove the override and use the bundled fixtures.");

        return false;
    }

    private static async Task<(bool SignedIn, string Detail)> ReadSignInAsync(MsagentCli msagent, CancellationToken cancellationToken)
    {
        var result = await msagent.RunAsync(["auth", "status", "--json", "--non-interactive"], cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0
            ? (true, "signed in")
            : (false, result.StdErr.Trim() is { Length: > 0 } error ? Summarize(error) : $"msagent auth status exited {result.ExitCode}");
    }

    /// <summary>
    /// Checks Azure Resource Manager consent with a read-only probe.
    ///
    /// <para>This is not an e2e-specific requirement. ARM access goes through a separate
    /// consent-capable public client, because the CLI's Power Platform first-party app can only be
    /// preauthorized for ARM, never consented. A real operator consents once, in a browser, the first
    /// time they run any <c>abs</c> command — after which every later run, interactive or not, works
    /// silently. The suite runs everything with <c>--non-interactive</c> so no scenario can hang on a
    /// browser, which means it cannot perform that one-time consent itself. It is a precondition, in
    /// exactly the way a signed-in session is.</para>
    /// </summary>
    private static async Task<bool> ArmConsentGrantedAsync(MsagentCli msagent, E2EConfig config, CancellationToken cancellationToken)
    {
        var probe = ConsentProbeArguments(config);
        var result = await msagent.RunAsync([.. probe, "--non-interactive"], timeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != ExitNotSignedIn)
        {
            return true;
        }

        Console.WriteLine();
        Console.WriteLine("Azure Resource Manager consent has not been granted for this account, so every Azure");
        Console.WriteLine("scenario will be skipped. Grant it the same way a normal user does — run one `abs`");
        Console.WriteLine("command interactively and complete the browser prompt, once:");
        Console.WriteLine();
        Console.WriteLine("    msagent " + string.Join(' ', probe.Select(Quote)));
        Console.WriteLine();
        Console.WriteLine("That plans a bot and changes nothing. Every later run then works non-interactively.");
        Console.WriteLine();

        return false;
    }

    private static string[] ConsentProbeArguments(E2EConfig config) =>
    [
        "abs", "preview",
        "--bot-name", "msagent-e2e-consent-probe",
        "--resource-group", config.Azure.ResourceGroup!,
        "--subscription", config.Azure.SubscriptionId!,
        "--tenant", config.TenantId!,
        "--location", "global",
        "--endpoint", "https://msagent-e2e-consent-probe.example.invalid/api/messages",
        "--msa-app-id", "00000000-0000-0000-0000-000000000000",
    ];

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;

    private const int ExitNotSignedIn = 3;

    private static void WarnAboutPreviousRuns(string currentRunId)
    {
        if (!Directory.Exists(RunsRoot))
        {
            return;
        }

        var unclean = Directory.EnumerateDirectories(RunsRoot)
            .Where(directory => !string.Equals(Path.GetFileName(directory), currentRunId, StringComparison.Ordinal))
            .Where(HasOutstandingEntries)
            .Select(Path.GetFileName)
            .ToArray();

        if (unclean.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{unclean.Length} earlier run(s) left resources behind:");

        foreach (var runId in unclean)
        {
            Console.WriteLine($"  msagent-e2e cleanup --run-id {runId}");
        }
    }

    private static bool HasOutstandingEntries(string runDirectory)
    {
        try
        {
            return Ledger.Open(runDirectory, Path.GetFileName(runDirectory)).Outstanding.Count > 0;
        }
        catch (E2EException)
        {
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Finds the harness project directory: the nearest directory at or above the start that holds the harness project file.</summary>
    /// <param name="startDirectory">The directory the search starts from, usually the build output folder.</param>
    /// <returns>The harness project directory, or null when no directory above the start holds the project file.</returns>
    public static string? FindProjectDirectory(string startDirectory) => FindAncestorHolding(startDirectory, ProjectFileName);

    /// <summary>Finds the source root: the nearest directory at or above the start that holds the CLI project folder.</summary>
    /// <param name="startDirectory">The directory the search starts from, usually the harness project directory.</param>
    /// <returns>The source root directory.</returns>
    public static string SourceRoot(string startDirectory) => FindAncestorHolding(startDirectory, Path.Combine("Microsoft.Agents.Hosting.Cli", "Microsoft.Agents.Hosting.Cli.csproj")) ?? throw new E2EException("Could not locate the source root; Microsoft.Agents.Hosting.Cli.csproj was not found above the harness project.");

    private static string? FindAncestorHolding(string startDirectory, string relativeFilePath)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, relativeFilePath)))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string Summarize(string value) =>
        value.ReplaceLineEndings(" ") is { Length: > 160 } long_ ? long_[..160] + "…" : value.ReplaceLineEndings(" ");
}
