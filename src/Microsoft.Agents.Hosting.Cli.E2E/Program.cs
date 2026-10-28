using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;
using Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

namespace Microsoft.Agents.Hosting.Cli.E2E;

/// <summary>The harness entry point: <c>plan</c>, <c>run</c>, <c>cleanup</c>, and <c>report</c>.</summary>
public static class Program
{
    /// <summary>Dispatches to a command and maps its result to a process exit code.</summary>
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            var options = CommandLine.Parse(arguments);

            return options.Command switch
            {
                "plan" => await Commands.PlanAsync(options, CancellationToken.None).ConfigureAwait(false),
                "run" => await Commands.RunAsync(options, CancellationToken.None).ConfigureAwait(false),
                "cleanup" => await Commands.CleanupAsync(options, CancellationToken.None).ConfigureAwait(false),
                "report" => Commands.Report(options),
                _ => CommandLine.PrintUsage(),
            };
        }
        catch (E2EException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 4;
        }
    }
}

/// <summary>The harness's own options, kept deliberately small.</summary>
public sealed record CommandOptions
{
    public required string Command { get; init; }

    public RunFilters Filters { get; init; } = new();

    public string? RunId { get; init; }

    public string? MsagentPath { get; init; }

    public bool Yes { get; init; }

    public bool NoColour { get; init; }
}

/// <summary>Parses the harness's arguments.</summary>
public static class CommandLine
{
    /// <summary>Reads the command and its filters.</summary>
    public static CommandOptions Parse(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new CommandOptions { Command = "help" };
        }

        var packs = new List<string> { "core" };
        var features = new List<string>();
        var only = new List<string>();
        var smoke = false;
        string? runId = null;
        string? msagentPath = null;
        var yes = false;
        var noColour = false;

        for (var index = 1; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--packs": packs = [.. Split(Next(arguments, ref index))]; break;
                case "--feature": features.AddRange(Split(Next(arguments, ref index))); break;
                case "--only": only.AddRange(Split(Next(arguments, ref index))); break;
                case "--run-id": runId = RunId(Next(arguments, ref index)); break;
                case "--msagent-path":
                case "--ah-path": msagentPath = Next(arguments, ref index); break;
                case "--smoke": smoke = true; break;
                case "--yes": yes = true; break;
                case "--no-color": noColour = true; break;
                default: throw new E2EException($"Unknown option {arguments[index]}.");
            }
        }

        return new CommandOptions
        {
            Command = arguments[0],
            Filters = new RunFilters { Packs = packs, Features = features, Only = only, Smoke = smoke },
            RunId = runId,
            MsagentPath = msagentPath,
            Yes = yes,
            NoColour = noColour,
        };
    }

    /// <summary>Prints the usage text and returns the configuration exit code.</summary>
    public static int PrintUsage()
    {
        Console.WriteLine("""
            msagent-e2e — live end-to-end suite for the msagent CLI

              plan                     resolve capabilities and describe what a run would do
              run                      execute the scheduled scenarios, then clean up
              cleanup --run-id <id>    remove whatever a previous run left behind
              report  --run-id <id>    re-print a previous run

            Options
              --packs core,azure,eval  include the Azure pack, the evaluation pack, or both
                                       swf adds the aggressive standalone-workflow suite, and
                                       swf-chaos (with swf) adds its kill, concurrency and fault scenarios
              --feature <names>        mcs-agent, mcs-workflow, mcs-standaloneworkflow, code-agent, abs,
                                       identity, mcs-eval
              --only <patterns>        MA-18, MA-2*, AB-07, EV-01, SW-B0*
              --smoke                  a fast subset: one deploy per fixture shape plus the checks
                                       that pin previously shipped bugs
              --msagent-path <file>    drive an existing binary instead of packing one
                                       (--ah-path is still accepted)
              --yes                    skip the confirmation prompt
              --no-color               plain output
            """);

        return 2;
    }

    private static string Next(string[] arguments, ref int index) =>
        ++index < arguments.Length ? arguments[index] : throw new E2EException($"{arguments[index - 1]} needs a value.");

    /// <summary>Accepts a run id only in the shape the harness generates, so it is safe as a path segment.</summary>
    /// <param name="value">The supplied run id.</param>
    private static string RunId(string value) =>
        value.Length >= 4 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : throw new E2EException($"--run-id '{value}' is not a run id. Expected at least four characters of letters, digits, '-' or '_'.");

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
