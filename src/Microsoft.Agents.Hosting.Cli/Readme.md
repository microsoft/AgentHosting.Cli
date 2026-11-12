# Microsoft.Agents.Hosting.Cli (`msagent`)

Microsoft.Agents.Hosting.Cli is a .NET command-line tool for working with Microsoft Agent Hosting resources. It helps developers and automation workflows authenticate, select a target environment, manage agents, and manage deployments.

This tool is intended for both humans and coding agents that need a consistent command interface to interact with the Agent Hosting platform.

The CLI is preview software. Obtain the package and review the terms of the preview before installing it.

> [!IMPORTANT]
>
> - This is a preview feature.
> - These features are subject to [supplemental terms of use](https://www.microsoft.com/en-us/business-applications/legal/supp-powerplatform-preview/), and are available before an official release so that customers can get early access and provide feedback.

## What this tool does

The CLI supports the following core activities:

- Sign in and manage authentication state
- List and select Power Platform environments
- Create, list, show, pull, and delete agent resources
- Manage deployment lifecycle and connection configuration
- Evaluate agent quality through Copilot Studio and query published agents from the terminal or Playground

## Install

The tool requires the .NET 10 and ASP.NET Core 10 runtimes. The telemetry SDK
adds the ASP.NET Core shared-framework dependency.

Download .NET 10 from <https://dotnet.microsoft.com/download/dotnet/10.0>. Installing the .NET 10 SDK
includes both runtimes and provides the `dotnet tool install` command used below. If you only run
an already-installed tool, the ".NET Runtime" and "ASP.NET Core Runtime" installers are enough.
On Windows you can also use winget:

```powershell
winget install Microsoft.DotNet.SDK.10
```

Verify that both runtimes are present (look for `Microsoft.NETCore.App 10.x` and
`Microsoft.AspNetCore.App 10.x`):

```bash
dotnet --list-runtimes
```

Install the tool as a global .NET tool:

```bash
dotnet tool install --global Microsoft.Agents.Hosting.Cli
```

Then run the command:

```bash
msagent --help
```

To update to the latest version:

```bash
dotnet tool update --global Microsoft.Agents.Hosting.Cli
```

For preview releases, add `--prerelease`. For a local tool, omit `--global` and update
from its manifest directory; for a custom installation, use `--tool-path <directory>` instead.

In a terminal, msagent checks listed versions on nuget.org and shows an update notice on stderr
before command output or prompts. Newer prereleases are labeled `(prerelease)` and include the
preview update command. It never installs an update automatically.

Successful checks are cached for 24 hours. Feed requests share a three-second deadline; failures
are silent and retried no sooner than one hour later when the cache is writable. Checks are skipped
for version output, invalid/navigation-only commands, `--json`, CI, or redirected stdout/stderr.
Set `AH_CLI_SKIP_UPDATE_CHECK=true` (case-insensitive) or `1` to disable checks and notices.
`--non-interactive` alone does not disable the check.

## Command overview

```bash
msagent [command] [options]
```

Common command groups include:

- `msagent auth` — sign in, check auth state, switch accounts, sign out
- `msagent env` — list available environments and select a target environment
- `msagent agent` — create, initialize, list, show, pull, delete, deploy, and evaluate agents
- `msagent deployment` — manage deployment records and connection binding
- `msagent test ask` — send a live query to a selected Copilot Studio agent
- `msagent test playground` — launch the M365 Agents Playground for a selected agent
- `msagent telemetry` — inspect and change telemetry collection preferences

Live test commands are available by default. They perform live operations that can have real
effects; use only an approved agent and destination. See the [live testing command reference](../Microsoft.Agents.Hosting.Cli.Docs/Command_Test.md)
for target selection, prerequisites, and behavior.

Global options:

```bash
-j,  --json              Emit machine-readable JSON output
-nc, --no-color          Disable colored output
-ni, --non-interactive   Fail instead of opening browser-based authentication flows
-h,  --help              Show help and usage information
-v,  --version           Show version information
```

## Typical workflow

1. Sign in to the tenant or account you want to work with.
2. List and select the target environment.
3. Create or initialize an agent project.
4. Create an MCS deployment and deploy the MCS project to it.
5. Configure deployment connections as needed.
6. Evaluate the agent with `msagent agent eval run`, and review the results with `msagent agent eval list` / `show`.
7. Publish the reviewed conversational-agent changes with `msagent agent deploy --publish`.
8. Test the published agent with `msagent test ask` or `msagent test playground`.

Creating or initializing source is local registration, not cloud deployment.
Cloud cloning establishes tracking; initializing files from Git does not restore
an existing cloud connection. Existing deployment records keep their own target
environment when the active environment changes.

## Example usage

### Sign in

```bash
msagent auth login
```

### Check authentication status

```bash
msagent auth status
```

### List environments

```bash
msagent env list
```

### Select an environment

```bash
msagent env select <environment-id>
```

### Create an agent

```bash
msagent agent create --agent-type MCSAgent --agent-name "Contoso Agent" --description "Contoso helper" --initial-prompt "Help users with Contoso questions."
```

### Deploy a draft and publish

From the registered project, create a named local target and upload the working tree:

```bash
msagent deployment create --deployment-name develop --deployment-type dev
msagent agent deploy --deployment-name develop
msagent deployment connection list --deployment-name develop
```

Resolve connection and workflow diagnostics before publishing. Binding with
`msagent deployment connection update --deployment-name develop` may be needed
before an incomplete deployment can fully succeed. Connections and consent are
environment-specific. Attached workflows can activate during a draft deploy or
binding; review their effects independently of conversational-agent publication.

After review and evaluation, publish with:

```bash
msagent agent deploy --deployment-name develop --publish
```

Publishing does not configure channels or grant user access. Deployment uploads
the current working tree synchronously; it does not select source by Git commit.
A development target checks for cloud drift, while test/production targets do not
provide the same guard. Use `--overwrite` only after deciding to replace cloud
changes; it is not a merge.

### Query the published agent

```bash
msagent test ask --deployment-name develop --query "What can you help me with?"
msagent test playground --deployment-name develop
```

The signed-in account must be able to access the published agent. Use
`--conversation-id <returned-id>` with the same destination to continue a
conversation; otherwise each query starts a new one. `--plain` returns final-only
human output; `--json` streams one activity/result document, not the ordinary
success envelope. These options cannot be combined. A completed response is not
proof that its answer is correct or that a remote action succeeded.

Playground requires Node.js 22.17.1 or newer and the
`@microsoft/m365agentsplayground` package, version 0.2.29 or newer. It opens a new
terminal and owns its session authentication, which can still require interaction
with `--non-interactive`. It rejects `--json`. Direct Line mode
(`--target directline`) requires an interactive terminal and a published no-auth
agent; enter credentials only at the native masked prompt.

Live queries and evaluations use Copilot Studio services and can consume Copilot
Credits or trigger billable actions. Local execution is not a dry run. Coding
assistants have separate licensing and usage charges.

### List agents

```bash
msagent agent list
```

### Show a project or agent configuration

```bash
msagent agent show
```

### Evaluate an agent

Runs the same Copilot Studio evaluation the maker portal runs, and the results appear there beside
the ones started from the portal. Evaluations consume Copilot Credits; the cost is reported when the
run finishes.

Start from the portal's CSV template — `conversationNumber`, `question`, `response` — and run it in
one line. `--name` names the test set this creates, and `--watch` waits for the grader:

```bash
msagent agent eval run --from-csv EvalConversationTemplate.csv --name EvaluationV1 --watch
```

A test set is durable: create it once, run it as often as you like.

```bash
msagent agent eval run --testset EvaluationV1 --watch
```

To have Copilot Studio invent the conversations instead of supplying a file:

```bash
msagent agent eval run --generate 10 --turns 3 --name "Smoke tests" --watch
```

Review what has run, and read one run back:

```bash
msagent agent eval list
msagent agent eval show --run-id <run-id>
```

Read the conversations the agent actually had. The terminal view shortens long replies; `--output`
writes the whole thing to a file you can open:

```bash
msagent agent eval show --run-id <run-id> --conversation 2
msagent agent eval show --run-id <run-id> --transcript
msagent agent eval show --run-id <run-id> --output run.md
```

Manage the test sets themselves:

```bash
msagent agent eval testset list
msagent agent eval testset export --testset EvaluationV1 --output conversations.csv
msagent agent eval testset update --testset EvaluationV1 --from-csv conversations.csv --replace
```

A queued or in-progress run can be stopped, and a finished one deleted. Deleting a run leaves the
test set it used in place:

```bash
msagent agent eval cancel --run-id <run-id>
msagent agent eval delete --run-id <run-id>
```

## JSON output

For automation and agent-driven workflows, pass `--json` to emit structured output:

```bash
msagent env list --json
msagent agent list --json
```

This is especially useful for coding agents that need to inspect tool output programmatically.

## Non-interactive mode

Authenticate interactively first, then use an existing session for supported
scripted commands. `auth login --non-interactive` is always rejected, even when
cached tokens exist. Non-interactive mode does not provide service-principal
authentication, new consent, or an unattended CI sign-in mechanism.

```bash
msagent auth login
msagent env list --non-interactive --json
msagent test ask --deployment-name develop --query "What can you help me with?" --non-interactive --json
```

Connection commands can still require selections on stdin or manual completion
of a printed consent link. Playground owns its own interactive authentication.
Do not treat `--non-interactive` or `--json` as a universal unattended-operation
guarantee.

## Selecting a deployment ring

By default every command targets the production Power Platform API. The ring is recorded on the
signed-in account, so you set it where you sign in:

```bash
msagent auth login --cloud preprod     # sign in, targeting preprod
msagent auth switch --cloud test       # retarget the active account; no sign-in
msagent auth status                    # shows the ring in use
```

Omitting `--cloud` on a later `auth login` keeps the ring the account already had; signing in a new
account inherits the ring the session is already on, and the very first sign-in defaults to `prod`. An
unrecognized value fails with a usage error rather than falling back to production.

`auth switch --cloud` takes effect from the next command: the one running it keeps addressing the
ring it started in, and says so instead of quietly using it.

## The environment selection follows the account

The selected environment belongs to the account that selected it, in the cloud it was selected for.
Signing in or switching to a different account, or to a different cloud, clears it and tells you so,
so `auth status` never reports a target you did not choose:

```bash
msagent env select fa5c1b48-…        # selected by op@contoso.com
msagent auth login                   # signs in other@contoso.com
# Signed in as other@contoso.com (prod).
# Cleared the selected environment because the active account or cloud changed. Run `msagent env select` to choose one.
```

Signing the same account in again, against the same cloud, keeps the selection. Every cloud's
selection is cleared, so switching clouds and back cannot resurrect an old target.

## Telemetry

The CLI sends usage telemetry, including command names and outcomes, never argument
values, paths, tokens, or other command content. When available, telemetry can
include tenant context and a validated tenant-local account object ID on command
attempts. These identifiers are not device or installation IDs. See the
[telemetry details](https://github.com/microsoft/AgentHosting.Cli/blob/main/docs/developer/Telemetry.md)
and the Privacy Statement below.

To opt out, use either of the following:

```bash
# Persistent: saved preference
msagent telemetry disable

# Current process only
AH_CLI_TELEMETRY_OPTOUT=1 msagent --help
```

In PowerShell, set the variable for the current session:

```powershell
$env:AH_CLI_TELEMETRY_OPTOUT = "1"
```

To opt back in, run `msagent telemetry enable` and set `AH_CLI_TELEMETRY_OPTOUT` to `0` or `false`
(or remove it). Run `msagent telemetry status` to check the current setting. `AH_CLI_TELEMETRY_OPTOUT`
accepts `1`/`true` to disable and `0`/`false` to enable; any other nonempty value is treated as
opted out.

## Notes

- The CLI is designed to be used from a shell or automation environment.
- Commands are generally safe for deterministic scripting when used with `--json` and `--non-interactive` as appropriate.
- The CLI supports TLS 1.3 and TLS 1.2.
- For full command details, run:

```bash
msagent --help
msagent <command> --help
```

## Privacy Statement

We value your privacy. To learn more about how your data is handled when using the AgentHosting CLI, please review our Privacy Statement: <https://www.microsoft.com/en-us/privacy/privacystatement>

## Summary

AgentHosting CLI gives developers and coding agents a single entry point for authenticating, selecting environments, preparing agent resources, and managing deployment configuration in an Agent Hosting environment. It is intentionally designed to be scriptable, readably structured, and easy to use from both terminal workflows and automated tooling.
