using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Azure;

/// <summary>App registration and blueprint scaffolding, all of it reachable without touching Entra.</summary>
public static class IdentityScenarios
{
    private static readonly ScenarioBuilder Builder = new("identity");

    private static readonly Capability[] None = [];
    private static readonly Capability[] Auth = [Capability.Auth];

    /// <summary>Every identity scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All()
    {
        yield return Builder.Make("ID-01", "identity create app --dry-run", Auth, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var result = await context.Msagent.RunAsync(
                [.. AppArguments(context, "dryrun"), "--dry-run", "--json", "--non-interactive"],
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(result, "a dry run plans without mutating Entra");
        });

        yield return Builder.Make("ID-02", "identity create blueprint --dry-run", Auth, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(
                ["identity", "create", "blueprint", "--tenant", context.Config.TenantId!,
                 "--display-name", $"ahe2e-blueprint-{context.RunSuffix}", "--auth-type", "ClientSecret",
                 "--config-format", "dotnet", "--dry-run", "--json", "--non-interactive"],
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(result, "a dry run plans without mutating Entra");
        });

        yield return Builder.Make("ID-03", "identity create app rejects an unknown --auth-type", None, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(
                ["identity", "create", "app", "--tenant", Guid.NewGuid().ToString(), "--display-name", "ahe2e",
                 "--auth-type", "not-a-type", "--config-format", "dotnet", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unknown auth type is a usage error");
        });

        yield return Builder.Make("ID-04", "identity create app rejects an unknown --config-format", None, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(
                ["identity", "create", "app", "--tenant", Guid.NewGuid().ToString(), "--display-name", "ahe2e",
                 "--auth-type", "ClientSecret", "--config-format", "cobol", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unknown config format is a usage error");
        });

        yield return Builder.Make("ID-05", "identity create app names a missing required flag", None, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(["identity", "create", "app", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "missing flags are a usage error");
            context.Assert.Contains(result, "--tenant", "the message names --tenant");
        });

    }

    private static string[] AppArguments(ScenarioContext context, string label) =>
    [
        "identity", "create", "app",
        "--tenant", context.Config.TenantId!,
        "--display-name", $"ahe2e-{label}-{context.RunSuffix}",
        "--auth-type", "ClientSecret",
        "--config-format", "dotnet",
    ];

}