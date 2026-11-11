using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Which long-lived agent a scenario wants.</summary>
public enum SessionAgentKind
{
    ScaffoldedCli,
    ScaffoldedClassic,
    FixtureCli,
    FixtureClassic,
}

/// <summary>Which long-lived Azure bot a scenario wants.</summary>
public enum SessionBotKind
{
    CodeAgentDeployment,
    AbsBot,
}

/// <summary>A deployed agent held for the length of a run.</summary>
public sealed record SessionAgent(SessionAgentKind Kind, Workspace Workspace, ManagedAgentDeployResult Deployed, string DeploymentName)
{
    /// <summary>The local record's identifier, which is what <c>--agent-id</c> names.</summary>
    public string AgentId => Deployed.AgentId;

    /// <summary>The service-assigned id of the cloud agent.</summary>
    public string CloudAgentId => Deployed.TargetMcsAgentId
        ?? throw new E2EException($"The {Kind} session agent carries no cloud id.");

    /// <summary>The environment the agent was deployed into.</summary>
    public string EnvironmentId => Deployed.TargetEnvironmentId;
}

/// <summary>
/// An Azure bot held for the length of a run, together with the throwaway app registration bound to
/// it. Azure permits only one bot per app id, so the two are always created and deleted as a pair.
/// </summary>
public sealed record SessionBot(
    SessionBotKind Kind,
    string Name,
    string ResourceGroup,
    string ApplicationId,
    string Endpoint,
    string? AgentId = null,
    string? DeploymentId = null,
    string? ProjectDirectory = null);

/// <summary>
/// The resources a run creates once and shares.
///
/// <para>Creating a Copilot Studio agent, an Azure bot, or an app registration costs a minute or
/// more each, and the suite previously made one of each per scenario. Everything that only needs a
/// resource to exist now reuses one, and the scenarios that destroy one run last and consume it.</para>
/// </summary>
public sealed class SessionResources
{
    private readonly SessionCache<SessionAgentKind, SessionAgent> _agents = new();
    private readonly SessionCache<SessionBotKind, SessionBot> _bots = new();
    private readonly ConcurrentDictionary<string, SessionCache<string, object>> _pools = new(StringComparer.Ordinal);

    /// <summary>Returns a run-scoped value from a named pool, creating it on first use.</summary>
    public async Task<T> PooledAsync<T>(string pool, string name, Func<Task<T>> create) where T : notnull => (T)await _pools.GetOrAdd(pool, _ => new SessionCache<string, object>()).GetAsync(name, async () => await create().ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>Forgets a pooled value a scenario destroyed.</summary>
    public void ConsumePooled(string pool, string name) => _pools.GetOrAdd(pool, _ => new SessionCache<string, object>()).Consume(name);

    /// <summary>Returns a run-scoped value from a named pool, keeping a creation failure so later callers are blocked by it rather than repeating it.</summary>
    public async Task<T> SettledAsync<T>(string pool, string name, Func<Task<T>> create) where T : notnull
    {
        var created = false;
        var settled = await PooledAsync(pool, name, async () =>
        {
            created = true;

            try
            {
                return new Settled<T>(await create().ConfigureAwait(false), null);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return new Settled<T>(default, ExceptionDispatchInfo.Capture(failure));
            }
        }).ConfigureAwait(false);

        if (settled.Failure is null)
        {
            return settled.Value!;
        }

        if (created)
        {
            settled.Failure.Throw();
        }

        throw new ScenarioBlockedException($"{name} could not be created earlier in this run: {settled.Failure.SourceException.Message}", $"{pool} {name} unavailable");
    }

    /// <summary>Returns a long-lived Copilot Studio agent, deploying it on first use.</summary>
    public Task<SessionAgent> AgentAsync(SessionAgentKind kind, Func<Task<SessionAgent>> deploy) => _agents.GetAsync(kind, deploy);

    /// <summary>Returns a long-lived Azure bot, provisioning it and its app registration on first use.</summary>
    public Task<SessionBot> BotAsync(SessionBotKind kind, Func<Task<SessionBot>> provision) => _bots.GetAsync(kind, provision);

    /// <summary>Forgets an agent a scenario deleted.</summary>
    public void ConsumeAgent(SessionAgentKind kind) => _agents.Consume(kind);

    /// <summary>Forgets a bot a scenario deleted.</summary>
    public void ConsumeBot(SessionBotKind kind) => _bots.Consume(kind);

    private sealed record Settled<T>(T? Value, ExceptionDispatchInfo? Failure);
}
