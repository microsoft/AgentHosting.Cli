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

For preview releases, add `--prerelease`. For a local tool, omit `--global` and update
from its manifest directory; for a custom installation, use `--tool-path <directory>` instead.

In a terminal, msagent checks listed versions on nuget.org and shows an update notice on stderr
before command output or prompts. Newer prereleases are labeled `(prerelease)` and include the
preview update command. It never installs an update automatically.

Successful checks are cached for 24 hours. Feed requests share a three-second deadline; failures
are silent and retried no sooner than one hour later when the cache is writable. Checks are skipped
for help, version, invalid/navigation-only commands, `--json`, CI, or redirected stdout/stderr.
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
msagent auth create
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

Use `--non-interactive=true` for scripts that must not open identity UI. Explicit interactive
create/login, including device code, return 12007 / exit 3 before cache enumeration; a cached account
does not bypass this. `--json` alone is not non-interactive. Eligible workload credentials still
require appropriate tenant permissions:

```bash
msagent auth create --managedIdentity --non-interactive=true
```

## Authentication profiles and clouds

Create an owned profile, then use token-free metadata commands to inspect and select it:

```bash
msagent auth create -n Dev -ci Public
msagent auth list
msagent auth select --name Dev
msagent auth who
msagent auth name --index 1 --name Development
msagent auth delete --index 1
```

Names are 1–30 characters and case-sensitive unique. List positions are transient and 1-based.
Select accepts exactly one `--index` or `--name`, or an interactive metadata picker without selectors.
Delete requires exactly one selector. Renaming an inactive profile does not activate it.
`auth clear` repairs corrupt metadata and removes only owned
msagent profiles/credentials/caches, not PAC/Azure/broker state or source certificates.

Create uses `--cloud` / `-ci`: `Public` (default), `Preprod`, `Test`, `UsGov`, `UsGovHigh`,
`UsGovDod`, `China`, `UsNat`, `UsSec`. Authentication has no separate ring option.
Unsupported China derived routes and UsNat/UsSec Copilot Studio gateways
fail closed; a stored profile does not prove downstream service support.

Create's other local aliases are `--name -n`, `--environment -env`, `--tenant -t`,
`--deviceCode -dc`, `--username -un`, `--password -pw`, `--applicationId -id`,
`--clientSecret -cs`, `--certificateDiskPath -cdp`, `--certificatePassword -cp`,
`--managedIdentity -mi`, `--githubFederated -ghf`, `--azureDevOpsFederated -adof`.
Credential modes are exclusive; username/password must be paired. Client secret, PFX certificate
and federation require explicit tenant/application GUIDs. DefaultAzureCredential is configured
source selection, not a guarantee that managed identity wins. Developer-source renewal cannot
launch Azure CLI/PowerShell/azd when interaction is forbidden. ROPC cannot satisfy MFA and its
password is not persisted for renewal.

Passwords/secrets in arguments can be exposed by shell history and process inspection despite CLI
redaction. Owned client secrets, PFX passwords and caches require protected native storage; a
token-cache-only plaintext baseline never permits plaintext credentials. Federation assertions
are not persisted. GitHub federation uses `ACTIONS_ID_TOKEN_REQUEST_URL`/`TOKEN`; Azure DevOps
uses `MSAGENT_ADO_ID_TOKEN_REQUEST_URL`/`TOKEN`, both with audience `api://AzureADTokenExchange`.
Native storage and sovereign downstream operation require platform/service validation.
Installing .NET alone does not establish protected-store, system-browser or broker readiness;
locked or headless sessions may still be unavailable.

`auth status` keeps its existing JSON/exit contract.
Status/list/select/who never acquire tokens or unlock native stores. Caller cancellation returns
130 and can report safe committed/cleanup guidance; it does not promise rollback. Inspect metadata
before another mutation and retry intended owned cleanup with `auth clear` after repairing storage.
Partial deletion reports the profile selected at commit, or that no active profile remained.

## The environment selection follows the profile

Target precedence is an explicit command environment, a compatible owned override, the profile's
default, then a missing-target error. `env select` updates the selected profile's default:

```bash
msagent env list
msagent env select <environment-id>
msagent auth who
```

Foreign/incompatible overrides are not silently adopted. To change another profile's default,
first select that profile with `auth select`, then use `env select`.

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
