using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios;

/// <summary>
/// Agent sharing against the live Copilot Studio control plane.
/// </summary>
public static class McsShareScenarios
{
    private static readonly ScenarioBuilder Builder = new("mcs-agent");

    private static readonly Capability[] Dev = [Capability.Auth, Capability.McsDev];
    private static readonly Capability[] Sharing = [Capability.Auth, Capability.McsDev, Capability.McsShareUser];

    /// <summary>An agent id no environment holds, for the cases that are rejected before it is read.</summary>
    private const string UnusedAgentId = "00000000-0000-0000-0000-000000000001";

    /// <summary>A principal no environment holds, for the cases that are rejected before it is read.</summary>
    private const string UnusedPrincipalId = "00000000-0000-0000-0000-000000000002";

    /// <summary>Every sharing scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() =>
    [
        .. Arguments(),
        .. Reading(),
        .. Granting(),
        .. OrgWide(),
    ];

    /// <summary>
    /// The cases the command line rejects on its own.
    /// </summary>
    private static IEnumerable<Scenario> Arguments()
    {
        yield return Builder.Make("MA-73", "agent share show without a selector is a usage error", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "share", "show", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a missing agent selector is a usage error");
            context.Assert.Contains(result, "--agent-id or --agent-name is required", "the failure names what is missing");
        });

        yield return Builder.Make("MA-74", "agent share rejects a role the service does not have", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "share", "--agent-id", UnusedAgentId, "--user", UnusedPrincipalId, "--role", "administrator", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "an unknown role is a usage error");
            context.Assert.Contains(result, "use, edit, view", "the failure lists the roles that exist");
        });

        yield return Builder.Make("MA-84", "agent share rejects a role alongside org-wide access", [], SafetyTier.ReadOnly, run: async context =>
        {
            var result = await RunAsync(context, ["agent", "share", "--agent-id", UnusedAgentId, "--share-with-org", "--role", "edit", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a role with no user is a usage error");
            context.Assert.Contains(result, "applies to --user", "the failure says where a role belongs");
        });
    }

    private static IEnumerable<Scenario> Reading()
    {
        yield return Builder.Make("MA-70", "agent share show reports the owner", Dev, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var shown = await ShowAsync(context, session).ConfigureAwait(false);

            context.Assert.Equal(session.CloudAgentId, shown.McsAgentId, "the agent read is the one deployed");
            context.Assert.Equal(session.EnvironmentId, shown.EnvironmentId, "the environment is the one deployed to");
            context.Assert.True(Holds(shown, RequireOwner(shown), "owner"), "an owner holds the agent");
        });

        yield return Builder.Make("MA-71", "agent share show names every principal by object id", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var shown = await ShowAsync(context, session).ConfigureAwait(false);

            context.Assert.True(shown.Shares.All(share => Guid.TryParse(share.ObjectId, out _)), "every principal carries an object id");
            context.Assert.True(shown.Shares.All(share => !string.IsNullOrWhiteSpace(share.Role)), "every principal carries a role");
        });

        yield return Builder.Make("MA-72", "agent share show resolves the same agent by id and by name", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);

            context.Assert.False(
                string.Equals(session.AgentId, session.Workspace.AgentName, StringComparison.OrdinalIgnoreCase),
                "the record id and the display name are different values, so --agent-id and --agent-name are different selectors");

            var byName = await RunAsync(
                context,
                ["agent", "share", "show", "--agent-name", session.Workspace.AgentName, "--json", "--non-interactive"],
                session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(byName, "agent share show --agent-name");

            var byId = await ShowAsync(context, session).ConfigureAwait(false);

            context.Assert.Equal(session.CloudAgentId, byId.McsAgentId, "--agent-id names the deployed agent");
            context.Assert.Equal(byId.McsAgentId, byName.Json<ManagedAgentShareShowResult>().McsAgentId, "the name and the id resolve to one agent");
        });
    }

    private static IEnumerable<Scenario> Granting()
    {
        yield return Builder.Make("MA-75", "agent share grants use access and agent unshare removes it", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;

            await GrantedAsync(context, session, principal, async () =>
            {
                var shared = await ShareAsync(context, session, principal, "use").ConfigureAwait(false);

                context.Assert.Equal("shared", shared.Status, "the grant is reported as a change");
                context.Assert.Equal("use", shared.Role, "the role granted is the role asked for");
                context.Assert.Equal(principal, shared.Principal?.ObjectId, "the principal granted is the one named");

                var shown = await ShowAsync(context, session).ConfigureAwait(false);

                context.Assert.True(Holds(shown, principal, "use"), "the service reports the grant on a later read");
            }).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-76", "agent share keeps the owner while granting somebody else", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;
            var owners = OwnerObjectIds(await ShowAsync(context, session).ConfigureAwait(false));

            await GrantedAsync(context, session, principal, async () =>
            {
                await ShareAsync(context, session, principal, "use").ConfigureAwait(false);

                var shown = await ShowAsync(context, session).ConfigureAwait(false);

                context.Assert.True(owners.All(owner => Holds(shown, owner, "owner")), "every owner still owns the agent");
                context.Assert.True(Holds(shown, principal, "use"), "the grant is in place alongside them");
            }).ConfigureAwait(false);
        });

        yield return Builder.Make("MA-77", "agent share repeating the same role changes nothing", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;

            await GrantedAsync(context, session, principal, async () =>
            {
                await ShareAsync(context, session, principal, "use").ConfigureAwait(false);
                var repeated = await ShareAsync(context, session, principal, "use").ConfigureAwait(false);

                context.Assert.Equal("unchanged", repeated.Status, "a repeated grant is reported as no change");
                context.Assert.False(repeated.Changed, "nothing was written the second time");
            }).ConfigureAwait(false);
        });

        // Analytics access is granted before anything asks for edit. Granting edit provisions
        // Environment Maker, which is environment-wide, is never removed by unshare, and which the
        // service refuses to combine with analytics-only access.
        yield return Builder.Make("MA-85", "agent share grants analytics-only view access", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;

            await GrantedAsync(context, session, principal, async () =>
            {
                var granted = await GrantViewAsync(context, session, principal).ConfigureAwait(false);

                context.Assert.Equal("view", granted.Role, "the role granted is the role asked for");

                var shown = await ShowAsync(context, session).ConfigureAwait(false);

                context.Assert.True(Holds(shown, principal, "view"), "the service reports analytics access rather than chat access");
                context.Assert.False(Holds(shown, principal, "use"), "analytics access is not chat access");
            }).ConfigureAwait(false);
        });
        yield return Builder.Make("MA-78", "agent share changes an existing role in place", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;

            await GrantedAsync(context, session, principal, async () =>
            {
                await ShareAsync(context, session, principal, "use").ConfigureAwait(false);
                var changed = await ShareAsync(context, session, principal, "edit").ConfigureAwait(false);

                context.Assert.Equal("shared", changed.Status, "the change is reported as one");
                context.Assert.Equal("edit", changed.Role, "the new role replaced the old one");

                var shown = await ShowAsync(context, session).ConfigureAwait(false);

                context.Assert.True(shown.Shares.Count(share => IsPrincipal(share, principal)) == 1, "the principal is listed once, not twice");
                context.Assert.True(Holds(shown, principal, "edit"), "the service reports the new role");
            }).ConfigureAwait(false);
        });


        yield return Builder.Make("MA-79", "agent unshare of a principal with no access changes nothing", Sharing, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;

            await RequireNoAccessAsync(context, session, principal).ConfigureAwait(false);

            var before = await ShowAsync(context, session).ConfigureAwait(false);

            var result = await RunAsync(
                context,
                ["agent", "unshare", "--agent-id", session.AgentId, "--user", principal, "--json", "--non-interactive"],
                session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitZero(result, "agent unshare");
            context.Assert.Equal("unchanged", result.Json<ManagedAgentUnshareResult>().Status, "revoking access nobody holds is not a change");
            context.Assert.Equal(Describe(before), Describe(await ShowAsync(context, session).ConfigureAwait(false)), "nobody else lost access");
        });

        yield return Builder.Make("MA-80", "agent share refuses to change the owner's access", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var owner = RequireOwner(await ShowAsync(context, session).ConfigureAwait(false));

            var result = await RunAsync(
                context,
                ["agent", "share", "--agent-id", session.AgentId, "--user", owner, "--role", "edit", "--non-interactive"],
                session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "targeting the owner is a usage error");
            context.Assert.Contains(result, "owner", "the failure says the owner cannot be changed by sharing");
            context.Assert.True(Holds(await ShowAsync(context, session).ConfigureAwait(false), owner, "owner"), "the owner still owns the agent");
        });

        yield return Builder.Make("MA-81", "agent unshare refuses to remove the owner's access", Dev, SafetyTier.ReadOnly, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var owner = RequireOwner(await ShowAsync(context, session).ConfigureAwait(false));

            var result = await RunAsync(
                context,
                ["agent", "unshare", "--agent-id", session.AgentId, "--user", owner, "--non-interactive"],
                session.Workspace.ProjectDirectory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "targeting the owner is a usage error");
            context.Assert.True(Holds(await ShowAsync(context, session).ConfigureAwait(false), owner, "owner"), "the owner still owns the agent");
        });
    }

    private static IEnumerable<Scenario> OrgWide()
    {
        yield return Builder.Make("MA-82", "agent share turns org-wide use access on and off", Dev, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var restore = (await ShowAsync(context, session).ConfigureAwait(false)).ShareWithOrg;

            try
            {
                var enabled = await SetOrgWideAsync(context, session, "true").ConfigureAwait(false);

                context.Assert.True(enabled.ShareWithOrg, "the setting is reported as on");
                context.Assert.True((await ShowAsync(context, session).ConfigureAwait(false)).ShareWithOrg, "a later read agrees");

                var disabled = await SetOrgWideAsync(context, session, "false").ConfigureAwait(false);

                context.Assert.False(disabled.ShareWithOrg, "the setting is reported as off");
                context.Assert.False((await ShowAsync(context, session).ConfigureAwait(false)).ShareWithOrg, "a later read agrees");
            }
            finally
            {
                await RestoreOrgWideAsync(context, session, restore).ConfigureAwait(false);
            }
        });

        yield return Builder.Make("MA-83", "org-wide access leaves an existing grant exactly as it was", Sharing, SafetyTier.Reversible, run: async context =>
        {
            var session = await McsSteps.SessionAsync(context, SessionAgentKind.ScaffoldedCli).ConfigureAwait(false);
            var principal = context.ShareUserObjectId;
            var restore = (await ShowAsync(context, session).ConfigureAwait(false)).ShareWithOrg;

            await GrantedAsync(context, session, principal, async () =>
            {
                await ShareAsync(context, session, principal, "use").ConfigureAwait(false);

                var before = await ShowAsync(context, session).ConfigureAwait(false);

                try
                {
                    var result = await SetOrgWideAsync(context, session, "true").ConfigureAwait(false);

                    context.Assert.True(result.Principal is null, "no principal is reported when none was named");

                    var after = await ShowAsync(context, session).ConfigureAwait(false);

                    context.Assert.True(Holds(after, principal, "use"), "the named grant survived the org-wide change");
                    context.Assert.Equal(Describe(before), Describe(after), "every principal holds exactly what it held before");
                }
                finally
                {
                    await RestoreOrgWideAsync(context, session, restore).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Runs a body that grants the configured principal access, and removes the grant afterwards.
    /// </summary>
    private static async Task GrantedAsync(ScenarioContext context, SessionAgent session, string principal, Func<Task> body)
    {
        await RequireNoAccessAsync(context, session, principal).ConfigureAwait(false);

        var failed = false;

        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            var removed = await TryRevokeAsync(context, session, principal).ConfigureAwait(false);

            if (!failed)
            {
                context.Assert.True(removed, "the grant this scenario made was removed again");
            }
        }
    }

    /// <summary>
    /// Refuses to start while the configured principal already holds access.
    /// </summary>
    private static async Task RequireNoAccessAsync(ScenarioContext context, SessionAgent session, string principal)
    {
        if (HasAccess(await ShowAsync(context, session).ConfigureAwait(false), principal))
        {
            throw new ScenarioBlockedException(
                $"{Short(principal)} already holds access to the session agent, so a grant made here could not be told from one left behind",
                "share user already has access",
                Capability.McsShareUser);
        }
    }

    /// <summary>
    /// Removes a grant and reports whether it is gone.
    /// </summary>
    private static async Task<bool> TryRevokeAsync(ScenarioContext context, SessionAgent session, string principal)
    {
        var revoked = await context.Msagent.RunAsync(
            ["agent", "unshare", "--agent-id", session.AgentId, "--user", principal, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        context.Transcript.Note($"revoke {Short(principal)}: exit {revoked.ExitCode}");

        if (revoked.ExitCode != 0)
        {
            return false;
        }

        var shown = await context.Msagent.RunAsync(
            ["agent", "share", "show", "--agent-id", session.AgentId, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        return shown.ExitCode == 0 && !HasAccess(shown.Json<ManagedAgentShareShowResult>(), principal);
    }

    /// <summary>Puts org-wide access back the way the scenario found it, asserting nothing.</summary>
    private static async Task RestoreOrgWideAsync(ScenarioContext context, SessionAgent session, bool shareWithOrg)
    {
        var result = await context.Msagent.RunAsync(
            ["agent", "share", "--agent-id", session.AgentId, "--share-with-org", shareWithOrg ? "true" : "false", "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        context.Transcript.Note($"restore org-wide to {shareWithOrg}: exit {result.ExitCode}");
    }

    private static async Task<ManagedAgentShareShowResult> ShowAsync(ScenarioContext context, SessionAgent session)
    {
        var result = await RunAsync(
            context,
            ["agent", "share", "show", "--agent-id", session.AgentId, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        context.Assert.ExitZero(result, "agent share show");

        return result.Json<ManagedAgentShareShowResult>();
    }

    private static async Task<ManagedAgentShareResult> ShareAsync(ScenarioContext context, SessionAgent session, string principal, string role)
    {
        var result = await RunAsync(
            context,
            ["agent", "share", "--agent-id", session.AgentId, "--user", principal, "--role", role, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        context.Assert.ExitZero(result, $"agent share --role {role}");

        return result.Json<ManagedAgentShareResult>();
    }

    /// <summary>
    /// Grants analytics-only access, or blocks when the principal already holds Environment Maker.
    ///
    /// Granting edit provisions Environment Maker, which is environment-wide and which unshare
    /// deliberately never removes, and the service refuses to combine it with analytics-only access.
    /// Once any run has granted edit to the configured principal the conflict is permanent for that
    /// environment, so this stops rather than reporting a defect in the command.
    /// </summary>
    private static async Task<ManagedAgentShareResult> GrantViewAsync(ScenarioContext context, SessionAgent session, string principal)
    {
        var result = await RunAsync(
            context,
            ["agent", "share", "--agent-id", session.AgentId, "--user", principal, "--role", "view", "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        if (result.ExitCode != 0 && result.Output.Contains("Environment Maker", StringComparison.OrdinalIgnoreCase))
        {
            throw new ScenarioBlockedException(
                $"{Short(principal)} already holds Environment Maker in this environment, which the service refuses to combine with analytics-only access",
                "share user holds Environment Maker");
        }

        context.Assert.ExitZero(result, "agent share --role view");

        return result.Json<ManagedAgentShareResult>();
    }

    private static async Task<ManagedAgentShareResult> SetOrgWideAsync(ScenarioContext context, SessionAgent session, string value)
    {
        var result = await RunAsync(
            context,
            ["agent", "share", "--agent-id", session.AgentId, "--share-with-org", value, "--json", "--non-interactive"],
            session.Workspace.ProjectDirectory).ConfigureAwait(false);

        context.Assert.ExitZero(result, $"agent share --share-with-org {value}");

        return result.Json<ManagedAgentShareResult>();
    }

    /// <summary>
    /// Runs the CLI under the scenario's own bound, so a command still in flight when the scenario
    /// times out is cancelled rather than left to change the next scenario's agent.
    /// </summary>
    private static Task<MsagentResult> RunAsync(ScenarioContext context, IReadOnlyList<string> arguments, string? workingDirectory = null) =>
        context.Msagent.RunAsync(
            arguments,
            workingDirectory ?? context.Workspaces.EmptyDirectory(context.ScenarioId),
            cancellationToken: context.CancellationToken);

    /// <summary>
    /// The agent's owner, or a block when the service reports none.
    ///
    /// A freshly deployed agent answers a read with no targets at all, so there is no owner to assert
    /// about or to refuse a change to. That is the service's behaviour rather than a defect in the
    /// command, so the scenarios that need one stop rather than fail.
    /// </summary>
    private static string RequireOwner(ManagedAgentShareShowResult shown) =>
        OwnerObjectIds(shown) is [var owner, ..]
            ? owner
            : throw new ScenarioBlockedException("the agent reports no owner to target", "no owner");

    private static IReadOnlyList<string> OwnerObjectIds(ManagedAgentShareShowResult shown) =>
        [.. shown.Shares.Where(share => HoldsRole(share, "owner")).Select(share => share.ObjectId)];

    private static bool IsPrincipal(ManagedAgentShareEntry share, string objectId) =>
        string.Equals(share.ObjectId, objectId, StringComparison.OrdinalIgnoreCase);

    private static bool HasAccess(ManagedAgentShareShowResult shown, string objectId) =>
        shown.Shares.Any(share => IsPrincipal(share, objectId));

    private static bool Holds(ManagedAgentShareShowResult shown, string objectId, string role) =>
        shown.Shares.Any(share => IsPrincipal(share, objectId) && HoldsRole(share, role));

    /// <summary>
    /// Whether a principal holds one named access level.
    /// </summary>
    private static bool HoldsRole(ManagedAgentShareEntry share, string role) =>
        share.Role.Split('+').Any(held => string.Equals(held.Trim(), role, StringComparison.OrdinalIgnoreCase));

    /// <summary>Who holds what, as one comparable line that names object ids rather than people.</summary>
    private static string Describe(ManagedAgentShareShowResult shown) =>
        string.Join(",", shown.Shares.Select(share => $"{share.ObjectId}:{share.Role}").Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>An object id shortened for the transcript, which records what ran rather than who.</summary>
    private static string Short(string objectId) => objectId.Length <= 8 ? objectId : objectId[..8] + "...";
}