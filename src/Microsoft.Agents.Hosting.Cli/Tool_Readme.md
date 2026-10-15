# Microsoft.Agents.Hosting.Cli (`msagent`)

Microsoft.Agents.Hosting.Cli is a .NET command-line tool for working with Microsoft Agent Hosting resources. It helps developers and automation workflows authenticate, select a target environment, manage agents, and manage deployments.

This tool is intended for both humans and coding agents that need a consistent command interface to interact with the Agent Hosting platform.

## What this tool does

The CLI supports the following core activities:

- Sign in and manage authentication state
- List and select Power Platform environments
- Create, list, show, pull, and delete agent resources
- Manage deployment lifecycle and connection configuration

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

## Command overview

```bash
msagent [command] [options]
```

Common command groups include:

- `msagent auth` — sign in, check auth state, switch accounts, sign out
- `msagent env` — list available environments and select a target environment
- `msagent agent` — create, initialize, list, show, pull, delete, deploy, and evaluate agents
- `msagent deployment` — manage deployment records and connection binding

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

Use `--non-interactive` for scripts, CI pipelines, or controlled automation that should not open a browser or interactive sign-in flow:

```bash
msagent auth login --non-interactive
```

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

The CLI sends usage telemetry (command names and outcomes, never argument values, paths,
tokens, or other command content). To learn more, see the Privacy Statement below.

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
- For full command details, run:

```bash
msagent --help
msagent <command> --help
```

## Privacy Statement

We value your privacy. To learn more about how your data is handled when using the AgentHosting CLI, please review our Privacy Statement: <https://www.microsoft.com/en-us/privacy/privacystatement>

## Summary

AgentHosting CLI gives developers and coding agents a single entry point for authenticating, selecting environments, preparing agent resources, and managing deployment configuration in an Agent Hosting environment. It is intentionally designed to be scriptable, readably structured, and easy to use from both terminal workflows and automated tooling.
